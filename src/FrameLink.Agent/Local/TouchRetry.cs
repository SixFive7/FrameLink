using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.State;

namespace FrameLink.Agent.Local;

/// <summary>
/// Something more urgent than the recovery gestures, asking for the same hold.
/// </summary>
/// <param name="Purpose">What a completed hold would do, for the journal.</param>
/// <param name="Hold">How long the finger stays down, which need not be either recovery length.</param>
/// <param name="Confirm">What a completed hold does.</param>
/// <remarks>
/// <b>One reader, more than one meaning, and the precedence written down rather than assumed.</b>
/// The panel has one evdev node and the agent opens it once; a second watcher on the same device
/// would be a second input path, a second poll loop and a second published state, and the two would
/// eventually disagree about whether a finger was down. So the reader stays exactly as it was and
/// what a completed hold <i>means</i> is resolved here: an ask outranks the recovery holds, always,
/// because an ask is on the screen and they are not — a person holding the panel is answering the
/// sentence in front of them, and there must be no arrangement of state in which the frame does
/// something other than what it just said it would do.
/// </remarks>
public sealed record TouchAsk(string Purpose, TimeSpan Hold, Action Confirm);

/// <summary>Everything the touch watch needs.</summary>
public sealed record TouchRetryServices
{
    /// <summary>How the panel's touchscreen is found and read.</summary>
    public required ITouchInput Input { get; init; }

    /// <summary>Where the hold's progress is published, so the screen can draw it.</summary>
    public required AgentStatusHub Hub { get; init; }

    /// <summary>Source of time, and of the poll cadence.</summary>
    public required IAgentClock Clock { get; init; }

    /// <summary>The journal.</summary>
    public required IAgentLog Log { get; init; }

    /// <summary>
    /// Whether the two recovery gestures are on offer right now — the same fact the screen renders.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a captured flag, and the same predicate the console and the browser
    /// use, so the buttons and the sentences describing them cannot come to disagree. §2.7 item 9:
    /// a retry with a full budget already available would reset nothing and teach the person that
    /// holding the screen does nothing, which is the same harm as an affordance that is not wired.
    /// </remarks>
    public required Func<bool> Offered { get; init; }

    /// <summary>What the shorter hold does — clear the budgets and restart the frame.</summary>
    public required Action Restart { get; init; }

    /// <summary>
    /// What the longer hold does — switch the frame off (decision 94).
    /// </summary>
    /// <remarks>
    /// The second of §2.5 rung 5's two buttons, on the surface that had only ever offered the
    /// first. The browser stage draws both as buttons; the console has no coordinates and so has to
    /// draw both as lengths of the one gesture it can read.
    /// </remarks>
    public required Action Shutdown { get; init; }

    /// <summary>
    /// Something on the screen that outranks the recovery holds right now, or null (decision 91).
    /// </summary>
    /// <remarks>
    /// Null on every frame with nothing to ask, which is every frame nearly all of the time. When it
    /// is not null the frame is showing a firmware screen, the hold answers <i>that</i>, and the
    /// hold's length comes from the ask rather than from <see cref="TouchRetry.RestartHold"/> —
    /// five seconds to agree to a write that cannot be undone, against three to try a resource
    /// again.
    /// </remarks>
    public Func<TouchAsk?>? Ask { get; init; }
}

/// <summary>
/// <b>Restart and shut down, pressed at the frame, on the console stage</b> — §2.5 rung 5,
/// §2.7 item 9, decisions 77 and 92.
/// </summary>
/// <remarks>
/// <para>
/// <b>They are holds rather than buttons, and the reason is coordinates.</b> The console stage
/// paints a character grid on a framebuffer that <c>boot.cmdline.fbcon-rotate</c> turns through 90°,
/// while the digitiser reports positions in the panel's own unrotated pixels. Hit-testing a drawn
/// button against that needs the console font's cell size and the rotation, and nothing in this
/// repository observes either — so a button would appear in one place and answer in another, which
/// is worse than no button at all. A hold needs no coordinates: only <c>BTN_TOUCH</c> going down
/// and coming up, which the frame's own capability bitmap proves it emits.
/// </para>
/// <para>
/// <b>And a hold rather than a tap, because the screen is at eye level in a living room.</b> A tap
/// would fire on a brush past the frame or on somebody wiping it clean, and what it fires is a
/// frame rebooting. Three seconds is deliberate in a way a tap cannot be, and ten seconds is
/// deliberate in a way three cannot be.
/// </para>
/// <para>
/// <b>Two lengths, and the second is why neither may act while the finger is down.</b> The browser
/// stage offers the operator's two buttons side by side; this surface has one gesture, so the two
/// verbs are two lengths of it — three seconds to restart, ten to switch off. A hold that restarted
/// the frame the moment it reached three could never reach ten, so the decision moves to the
/// release. That is forced rather than chosen, and it is what gives the gesture a way out: for the
/// first three seconds taking the finger off does nothing at all, and the screen says so while the
/// finger is resting there.
/// </para>
/// <para>
/// <b>Nothing here is on a timer.</b> No length expires, nothing is withdrawn if the finger stays
/// down too long, and nothing acts without a release. What the screen draws is the person's own
/// finger, which is why it is neither the animation decision 70 forbids nor the countdown that
/// gives up on its own that this project has already turned down once.
/// </para>
/// <para>
/// <b>One reset and one power switch, however they are reached.</b> A completed hold calls exactly
/// what the Fleet Manager and the browser stage's buttons call, so a press at the frame and a press
/// two hundred kilometres away cannot come to mean different things.
/// </para>
/// </remarks>
public sealed class TouchRetry
{
    /// <summary>How long a finger has to stay down for a restart (§2.7 item 9).</summary>
    public static TimeSpan RestartHold { get; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a finger has to stay down for a shutdown (decision 94).
    /// </summary>
    /// <remarks>
    /// Seven seconds past the restart mark, on a screen that spends all seven of them saying what
    /// letting go now would do and what letting go later would do instead. A frame that is off can
    /// be reached by nothing and brought back by no remote action at all, so the gesture that ends
    /// there has to be one nobody arrives at by accident.
    /// </remarks>
    public static TimeSpan ShutdownHold { get; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the node is asked what it has.</summary>
    /// <remarks>
    /// One non-blocking read of an idle character device, twenty times a second — measurably less
    /// than the console stage's own repaint tick beside it, and short enough that the progress bar
    /// starts within a frame of the finger landing.
    /// </remarks>
    public static TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the watch waits before looking for a touchscreen again.</summary>
    /// <remarks>
    /// A frame's first boot has no panel at all — the overlay is 2nd and 3rd in the catalog and
    /// takes a reboot to apply — so "there is no touchscreen" is the ordinary answer for the first
    /// minutes of a frame's life and must not be a permanent one. Two file reads a minute is the
    /// whole cost of never having to explain why a frame that grew a panel did not grow a button.
    /// </remarks>
    public static TimeSpan RediscoverDelay { get; } = TimeSpan.FromSeconds(30);

    private readonly TouchRetryServices _services;

    private ITouchReader? _reader;
    private TouchDevice? _device;
    private bool _down;
    private bool _fired;
    private bool _reportedAbsent;
    private DateTimeOffset? _since;

    /// <summary>Creates the watch.</summary>
    public TouchRetry(TouchRetryServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        State = TouchRetryState.None with { Hold = ShutdownHold, RestartAt = RestartHold };
    }

    /// <summary>What the screen should say and draw about touch right now.</summary>
    public TouchRetryState State { get; private set; }

    /// <summary>How many holds have done something since this process started.</summary>
    public long Holds { get; private set; }

    /// <summary>Finds the touchscreen and opens it, or reports that there is none.</summary>
    /// <returns>Whether a node is open.</returns>
    public bool EnsureOpen()
    {
        if (_reader is not null)
        {
            return true;
        }

        if (_services.Input.Find() is not { } device)
        {
            if (!_reportedAbsent)
            {
                _reportedAbsent = true;
                _services.Log.Info(
                    "No touchscreen was found, so this frame's own screen can offer neither a restart nor a "
                    + "shutdown, and says so. This is said once rather than on every look.");
            }

            Publish(null, null);
            return false;
        }

        if (_services.Input.Open(device) is not { } reader)
        {
            Publish(null, null);
            return false;
        }

        _device = device;
        _reader = reader;
        _reportedAbsent = false;
        _down = false;
        _fired = false;
        _since = null;

        _services.Log.Info(
            $"The frame's touchscreen is {device.Name} at {device.Node}; on a stopped frame, "
            + string.Create(
                CultureInfo.InvariantCulture,
                $"letting go after {(int)RestartHold.TotalSeconds} s restarts it and letting go after "
                    + $"{(int)ShutdownHold.TotalSeconds} s switches it off.")
            + " Nothing happens while the finger is still down.");

        Publish(device, null);
        return true;
    }

    /// <summary>One poll of the touchscreen.</summary>
    /// <remarks>
    /// Separated from <see cref="RunAsync"/> so the whole state machine — down, held, released,
    /// committed — is assertable without a clock, a loop or a device.
    /// </remarks>
    public void Tick()
    {
        if (_reader is null)
        {
            return;
        }

        bool? change;

        try
        {
            change = _reader.Drain();
        }
        catch (IOException exception)
        {
            // The panel went away: unplugged, or a driver reloaded. Say so and start looking again,
            // because a screen that silently stops answering the affordances it offers is exactly
            // what decision 72 shipped by accident.
            _services.Log.Warn($"The touchscreen stopped answering ({exception.Message}); looking for it again.");
            Close();
            return;
        }

        if (change is { } touching)
        {
            _down = touching;
        }

        var now = _services.Clock.UtcNow;

        // An ask outranks the recovery holds and brings its own duration with it. Resolved once,
        // here, so the bar being drawn, the length being counted and the action being taken are all
        // the same decision — a screen that counted three seconds and then did something a
        // five-second sentence had promised would be worse than no affordance at all.
        var ask = _services.Ask?.Invoke();
        var offered = ask is not null || _services.Offered();

        // The shape of the hold, and so what a release means. An ask has one mark and fires while
        // the finger is down; the recovery pair has two and fires when it comes off.
        var hold = ask?.Hold ?? ShutdownHold;
        var restartAt = ask is null ? RestartHold : (TimeSpan?)null;

        if (_down)
        {
            _since ??= now;
        }
        else
        {
            // The release edge, and the only place the two-way gesture ever acts. How long the
            // finger was down is the whole of the decision, so it is read before it is forgotten.
            var began = _since;
            var already = _fired;

            _since = null;
            _fired = false;

            if (began is { } from && !already && ask is null && offered)
            {
                Commit(new TouchRetryState(_device?.Node, hold, from, restartAt), now);
            }
        }

        if (ask is not null && _down && !_fired && _since is { } holding && now - holding >= ask.Hold)
        {
            // Decision 91's half, unchanged: one mark, one answer, taken while the finger is still
            // there. Waiting for a release would leave somebody holding a full bar in front of a
            // sentence that had stopped asking them for anything.
            _fired = true;
            Holds++;

            _services.Log.Info($"Somebody held this frame's screen: {ask.Purpose}.");
            ask.Confirm();
        }

        Publish(_device, offered && _down && !_fired ? _since : null, hold, restartAt);
    }

    /// <summary>Watches the touchscreen until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var open = EnsureOpen();

            if (open)
            {
                Tick();
            }

            try
            {
                await _services.Clock
                    .DelayAsync(open ? PollInterval : RediscoverDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Close();
                return;
            }
        }

        Close();
    }

    /// <summary>
    /// Takes whichever of the two verbs the finger's own length asked for.
    /// </summary>
    /// <remarks>
    /// The band is resolved by <see cref="TouchRetryState.Commit"/> rather than here, so the words
    /// the screen was showing under the bar and the thing that happens when the finger comes off are
    /// one decision rather than two implementations of it. A release inside the first band is
    /// somebody changing their mind and is deliberately not journalled: it happened, nothing came of
    /// it, and a line for every aborted touch would bury the ones that did something.
    /// </remarks>
    private void Commit(TouchRetryState hold, DateTimeOffset now)
    {
        switch (hold.Commit(now))
        {
            case TouchCommit.Restart:
                Holds++;
                _services.Log.Info(
                    "Somebody held this frame's screen past the first mark and let go; restarting it and "
                    + "trying again.");
                _services.Restart();
                break;

            case TouchCommit.Shutdown:
                Holds++;
                _services.Log.Info(
                    "Somebody held this frame's screen for ten seconds and let go; switching it off. "
                    + "Nothing else will happen until somebody at the frame switches it on again.");
                _services.Shutdown();
                break;

            default:
                break;
        }
    }

    private void Close()
    {
        _reader?.Dispose();
        _reader = null;
        _device = null;
        _down = false;
        _fired = false;
        _since = null;
        Publish(null, null);
    }

    /// <summary>
    /// Publishes the touch state, and only when it has actually changed.
    /// </summary>
    /// <remarks>
    /// Twenty polls a second against a hub every subscriber repaints on would be twenty console
    /// frames a second for a screen where nothing is happening. Nothing is lost by staying quiet:
    /// the console repaints on its own tick and works the bands out from
    /// <see cref="TouchRetryState.HoldingSince"/> and the instant it is rendering, which is what
    /// makes the whole renderer a pure function of its arguments.
    /// </remarks>
    private void Publish(
        TouchDevice? device,
        DateTimeOffset? holdingSince,
        TimeSpan? hold = null,
        TimeSpan? restartAt = null)
    {
        var next = hold is { } length
            ? new TouchRetryState(device?.Node, length, holdingSince, restartAt)
            : new TouchRetryState(device?.Node, ShutdownHold, holdingSince, RestartHold);

        if (next == State)
        {
            return;
        }

        State = next;
        _services.Hub.Publish(status => status with { Touch = next });
    }
}
