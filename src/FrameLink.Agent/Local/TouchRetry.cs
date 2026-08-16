using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.State;

namespace FrameLink.Agent.Local;

/// <summary>Everything the touch retry needs.</summary>
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
    /// Whether a retry is on offer right now — the same fact the screen renders.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a captured flag, and the same predicate the console and the browser
    /// use, so the button and the sentence describing it cannot come to disagree. §2.7 item 9: a
    /// retry with a full budget already available would reset nothing and teach the person that
    /// holding the screen does nothing, which is the same harm as an affordance that is not wired.
    /// </remarks>
    public required Func<bool> Offered { get; init; }

    /// <summary>What a completed hold does.</summary>
    public required Action Retry { get; init; }
}

/// <summary>
/// <b>Retry, pressed at the frame, on the console stage</b> — §2.5 rung 5, §2.7 item 9,
/// decision 77.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a hold rather than a button, and the reason is coordinates.</b> The console stage
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
/// retry that starts a frame rebooting. Three seconds is deliberate in a way a tap cannot be.
/// </para>
/// <para>
/// <b>One reset, two callers, now three.</b> A completed hold calls exactly what the Fleet
/// Manager's retry and the browser stage's button call, so a press at the frame and a press two
/// hundred kilometres away cannot come to mean different things.
/// </para>
/// <para>
/// <b>What it renders while the finger is down is not an exception to decision 70.</b> That rule
/// forbids animating work that is not happening; a hold indicator reports the person's own finger,
/// it is determinate rather than a travelling marquee, and it disappears the instant they let go.
/// Nothing about it says the reconciler is doing anything, because it is not.
/// </para>
/// </remarks>
public sealed class TouchRetry
{
    /// <summary>How long a finger has to stay down (§2.7 item 9).</summary>
    public static TimeSpan HoldDuration { get; } = TimeSpan.FromSeconds(3);

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
        State = TouchRetryState.None with { Hold = HoldDuration };
    }

    /// <summary>What the screen should say and draw about touch right now.</summary>
    public TouchRetryState State { get; private set; }

    /// <summary>How many holds have completed since this process started.</summary>
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
                    "No touchscreen was found, so this frame's own screen offers no retry and says so. "
                    + "This is said once rather than on every look.");
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
            $"The frame's touchscreen is {device.Name} at {device.Node}; holding it for "
            + string.Create(CultureInfo.InvariantCulture, $"{(int)HoldDuration.TotalSeconds} s")
            + " asks a stopped frame to try again.");

        Publish(device, null);
        return true;
    }

    /// <summary>One poll of the touchscreen.</summary>
    /// <remarks>
    /// Separated from <see cref="RunAsync"/> so the whole state machine — down, held, fired,
    /// released — is assertable without a clock, a loop or a device.
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
            // The panel went away: unplugged, or a driver reloaded. Say so and start looking
            // again, because a screen that silently stops answering the one affordance it offers
            // is exactly what decision 72 shipped by accident.
            _services.Log.Warn($"The touchscreen stopped answering ({exception.Message}); looking for it again.");
            Close();
            return;
        }

        if (change is { } touching)
        {
            _down = touching;
        }

        var now = _services.Clock.UtcNow;

        if (!_down)
        {
            _since = null;
            _fired = false;
        }
        else
        {
            _since ??= now;
        }

        var offered = _services.Offered();

        if (_down && offered && !_fired && _since is { } began && now - began >= HoldDuration)
        {
            _fired = true;
            Holds++;
            _services.Log.Info("Somebody held this frame's screen; asking it to try again.");
            _services.Retry();
        }

        Publish(_device, offered && _down && !_fired ? _since : null);
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
    /// the console repaints on its own tick and works the remaining seconds out from
    /// <see cref="TouchRetryState.HoldingSince"/> and the instant it is rendering, which is what
    /// makes the whole renderer a pure function of its arguments.
    /// </remarks>
    private void Publish(TouchDevice? device, DateTimeOffset? holdingSince)
    {
        var next = new TouchRetryState(device?.Node, HoldDuration, holdingSince);

        if (next == State)
        {
            return;
        }

        State = next;
        _services.Hub.Publish(status => status with { Touch = next });
    }
}
