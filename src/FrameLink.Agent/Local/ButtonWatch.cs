using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Resources;

namespace FrameLink.Agent.Local;

/// <summary>What the agent's own button watch currently knows about itself.</summary>
/// <param name="Pin">The BCM line it is configured for.</param>
/// <param name="Consumer">The name its claim is recorded under.</param>
/// <param name="Holding">Whether a claim attempt is in flight right now.</param>
/// <param name="Failure">Why the last attempt ended, or null if none has ended yet.</param>
/// <param name="Presses">How many presses have arrived since this process started.</param>
/// <param name="LastPressUtc">When the last one arrived, or null if none has.</param>
public readonly record struct ButtonState(
    int Pin,
    string Consumer,
    bool Holding,
    string? Failure,
    long Presses,
    DateTimeOffset? LastPressUtc)
{
    /// <summary>What a catalog built without a button watch reports.</summary>
    public static ButtonState None { get; } = new(
        ButtonWatch.DefaultPin,
        ButtonWatch.ConsumerName,
        Holding: false,
        Failure: "the agent's button watch is not running",
        Presses: 0,
        LastPressUtc: null);

    /// <summary>The state in one sentence, for a delta and for telemetry.</summary>
    public string Describe() => Holding
        ? Presses == 0
            ? "the agent is holding the line and has not seen a press yet"
            : string.Create(CultureInfo.InvariantCulture, $"the agent is holding the line and has seen {Presses} press(es)")
        : $"the agent is not holding the line ({Failure ?? "no attempt has finished"})";
}

/// <summary>
/// <b>The call button</b> — guide 11's daemon, inside the agent.
/// </summary>
/// <remarks>
/// <para>
/// The catalog retires <c>framelink-gpio.service</c>, its three apt packages and its WebSocket
/// server outright: "the WebSocket server on <c>127.0.0.1:8889</c> is an internal detail of the v1
/// split between daemon and SPA; with both inside one binary there is no port". What it requires to
/// be reimplemented rather than dropped is three behaviours. Two of them are supervision and live
/// in <c>Supervisor</c> — the camera recycle after every call-end, and the kiosk-liveness watchdog.
/// The third is this: a press on the physical button toggles the frame between the photographs and
/// the call, and a <c>SIGUSR1</c>-equivalent simulates one for testing.
/// </para>
/// <para>
/// <b>The press rides the same local channel as everything else.</b> There is no second server and
/// no second origin — the page that is already checking in for §2.10's liveness rule is the page
/// that receives the toggle, and it arrives as an ordinary stage frame carrying a command. A page
/// that has not loaded yet misses the press, which is correct: there is nothing to toggle.
/// </para>
/// <para>
/// <b>It must fail loudly when the real backend is unavailable, and that is a scar.</b> v1 used
/// gpiozero with the lgpio backend, whose most dangerous property the catalog asks to be carried
/// into v2 as a test: without <c>python3-lgpio</c>, gpiozero <i>silently</i> fell back to a mock
/// pin factory — the daemon started cleanly, reported healthy, and the button simply never fired.
/// Nothing here has a fallback. A claim that cannot be made is recorded as a failure, reported in
/// the resource's delta, and retried; it is never a quiet success.
/// </para>
/// <para>
/// <b>A frame with no button wired is not a failure.</b> The claim is on the line, and a line with
/// nothing attached to it sits at the pull-up's high level for ever and produces no edges. That is
/// an entirely healthy state — the wire is the one part of this that only a human can confirm
/// (guide 11 step 5), so a frame that has never seen a press reports exactly that and nothing
/// escalates.
/// </para>
/// </remarks>
public sealed class ButtonWatch
{
    /// <summary>Fleet setting carrying the line the button is wired to (§3.4).</summary>
    public const string SettingKey = "button.gpioPin";

    /// <summary>BCM 17, physical pin 11 — guide 11's documented default.</summary>
    public const int DefaultPin = 17;

    /// <summary>The chip the Pi's 40-pin header lives on.</summary>
    public const string DefaultChip = "gpiochip0";

    /// <summary>The name the claim is recorded under, and what <c>gpioinfo</c> shows.</summary>
    public const string ConsumerName = "fl-agent-button";

    /// <summary>The command a press sends to the page.</summary>
    public const string ToggleCommand = "toggle";

    /// <summary>Guide 11's <c>bounce_time=0.05</c>, in the units libgpiod wants.</summary>
    public static TimeSpan Debounce { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a lost or refused claim waits before being tried again.</summary>
    /// <remarks>
    /// Long enough that a frame with no GPIO at all is not spawning a process every second, short
    /// enough that a line released by whoever was holding it is picked up while somebody is still
    /// standing in front of the frame.
    /// </remarks>
    public static TimeSpan RetryDelay { get; } = TimeSpan.FromSeconds(30);

    private readonly ButtonWatchServices _services;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _attempt;
    private string? _failure;
    private bool _holding;
    private bool _rearmed;
    private long _presses;
    private DateTimeOffset? _lastPress;

    /// <summary>Creates the watch.</summary>
    public ButtonWatch(ButtonWatchServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>The line this frame is configured to watch.</summary>
    /// <remarks>
    /// Read at call time rather than captured, exactly as every other fleet value is: a pin changed
    /// in the Fleet Manager becomes drift on the next pass instead of at the next process start.
    /// A value that is not a number is ignored rather than obeyed — the alternative is claiming
    /// line 0, which on a Pi is a pin somebody else's hardware is using.
    /// </remarks>
    public int Pin =>
        int.TryParse(_services.Values.Find(SettingKey), CultureInfo.InvariantCulture, out var pin) && pin >= 0
            ? pin
            : DefaultPin;

    /// <summary>How many presses have arrived since this process started.</summary>
    public long Presses => Interlocked.Read(ref _presses);

    /// <summary>Why the last claim attempt ended, or null while the first is still running.</summary>
    public string? LastStandDown { get; private set; }

    /// <summary>Everything the resource and telemetry need, taken together under one lock.</summary>
    public ButtonState State()
    {
        lock (_gate)
        {
            return new ButtonState(Pin, ConsumerName, _holding, _failure, Interlocked.Read(ref _presses), _lastPress);
        }
    }

    /// <summary>Holds the line for as long as the agent runs, re-claiming it if it is ever lost.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            lock (_gate)
            {
                _attempt = attempt;
                _holding = true;
            }

            string reason;

            try
            {
                reason = await _services.Lines
                    .WatchAsync(
                        new GpioLineRequest(DefaultChip, Pin, ConsumerName, Debounce),
                        () => Press("the button"),
                        attempt.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                reason = "the claim was released";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // A watch that dies takes the button with it, silently, which is the whole failure
                // class this class exists to refuse. Recorded and retried.
                reason = exception.Message;
            }

            bool rearmed;

            lock (_gate)
            {
                _attempt = null;
                _holding = false;
                rearmed = _rearmed;
                _rearmed = false;

                if (rearmed)
                {
                    reason = "the agent was asked to claim the line again";
                }

                _failure = reason;
            }

            LastStandDown = reason;

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (rearmed)
            {
                // The Act asked for the claim to be made again, so this is not a failure and it
                // does not wait out the backoff. Backing off here would mean the repair took half a
                // minute to happen and the verifying reboot would land in the middle of it.
                _services.Log.Info($"The call button's claim on line {Pin} is being made again on request.");
                continue;
            }

            _services.Log.Warn($"The call button's claim on line {Pin} ended: {reason}");

            try
            {
                await _services.Clock.DelayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Drops the current claim so the next loop iteration makes it again — the Act of
    /// <c>gpio.button.line</c>.
    /// </summary>
    /// <returns>Whether there was an attempt in flight to interrupt.</returns>
    public bool ReArm()
    {
        CancellationTokenSource? attempt;

        lock (_gate)
        {
            attempt = _attempt;

            if (attempt is not null)
            {
                _rearmed = true;
            }
        }

        if (attempt is null)
        {
            return false;
        }

        try
        {
            attempt.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The loop moved on between the read and the cancel, which is the outcome asked for.
            return false;
        }
    }

    /// <summary>
    /// A press that did not come from the wire — guide 11 step 4's <c>SIGUSR1</c>, without a signal.
    /// </summary>
    /// <remarks>
    /// v1 exercised "everything except the wire" by sending the daemon <c>SIGUSR1</c>, and step 5
    /// was the human who closed the remaining half by pushing the button. The same split survives
    /// here; what changes is that the simulated press arrives over §3.6's diagnostics path rather
    /// than as a signal, because there is no longer a separate process to signal.
    /// </remarks>
    public Task SimulateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return PressAsync("a simulated press", cancellationToken);
    }

    private void Press(string source) => _ = PressAsync(source, CancellationToken.None);

    private async Task PressAsync(string source, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _presses);

        lock (_gate)
        {
            _lastPress = _services.Clock.UtcNow;
        }

        var frame = _services.Stage();

        if (!frame.ProductRuns)
        {
            // §2.6 gives the agent the screen whenever the product is not running, and a toggle
            // into a call that cannot start is not a repair. The press is still counted, so
            // "somebody is pressing the button and nothing happens" stays visible.
            LastStandDown = "the product is not running, so the press was not passed on";
            _services.Log.Info($"The call button was pressed ({source}) while {LastStandDown}.");
            return;
        }

        try
        {
            await _services.Channel
                .PublishAsync(frame with { Command = ToggleCommand }, cancellationToken)
                .ConfigureAwait(false);

            _services.Log.Info($"The call button was pressed ({source}); the frame was told to switch mode.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Log.Warn($"The call button was pressed ({source}) and the page could not be told: {exception.Message}");
        }
    }
}

/// <summary>Everything <see cref="ButtonWatch"/> needs.</summary>
public sealed record ButtonWatchServices
{
    /// <summary>Where the toggle is published.</summary>
    public required LocalChannel Channel { get; init; }

    /// <summary>How the line is claimed and heard.</summary>
    public required IGpioLines Lines { get; init; }

    /// <summary>
    /// The narration frame as it stands right now, which the command rides on.
    /// </summary>
    /// <remarks>
    /// The command travels inside an honest, current stage frame rather than in a bare message of
    /// its own, so a page that ignores the command field still renders the truth rather than being
    /// told the device is in some default condition. It is also what makes the "is the product
    /// running" question answerable here without this class knowing anything about §2.6's ladder.
    /// </remarks>
    public required Func<StageMessage> Stage { get; init; }

    /// <summary>Source of time and of waiting.</summary>
    public required IAgentClock Clock { get; init; }

    /// <summary>Where a lost claim is recorded.</summary>
    public required IAgentLog Log { get; init; }

    /// <summary>The fleet's <c>button.*</c> values.</summary>
    public FleetValues Values { get; init; } = FleetValues.None;
}
