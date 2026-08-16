using System.Globalization;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Supervise;

/// <summary>
/// <b>A line budget over one supervised child's output</b>, so that a chatty child cannot rotate
/// the frame's own memory away.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure this exists to prevent, measured on the mule 2026-08-16.</b> Immich Kiosk was
/// running with no album scope, could select no asset, and logged
/// <c>SaveOfflineAsset: generateViewData err="selecting asset: no assets found for random"</c> at
/// about seven lines a second. Sustained, that is roughly 600,000 lines a day; against guide 12's
/// <c>SystemMaxUse=64M</c> it evicted every archived journal file, and the earliest surviving
/// <c>fl-agent</c> entry became the running process's own start line. The agent's persistent
/// journal is the frame's forensic record — it is what root-caused the autologin and browser
/// defects the same night — so a child that can erase it is a hazard <i>whatever</i> it is
/// shouting about, and bounding it must not wait for the particular bug that made it shout.
/// </para>
/// <para>
/// <b>Why not systemd's own rate limiting.</b> It was the first candidate and the arithmetic
/// refuses it. journald's per-unit limit defaults to <c>RateLimitBurst=10000</c> per
/// <c>RateLimitIntervalSec=30s</c>, multiplied by a free-space factor of one to six — so the
/// measured flood, about 210 lines per interval, sat roughly fifty times <i>below</i> the point at
/// which journald would have dropped a single line. The volume was the problem, never the rate.
/// Tuning <c>LogRateLimitBurst=</c> down far enough to bound a day's volume would put the ceiling
/// near a few hundred lines per interval, which is inside what the agent itself legitimately
/// writes while narrating seventy-nine resources through a first provision — so the setting that
/// would have caught this child would also have silenced the frame's own honesty surface. A
/// per-unit limit also cannot tell the child's lines from the agent's, and it was the
/// <i>agent's</i> history that was lost.
/// </para>
/// <para>
/// <b>Why not a journal namespace.</b> <c>LogNamespace=</c> would give the unit its own store with
/// its own cap, which is structurally stronger against the <i>system</i> journal — but the harm
/// measured here was the agent's own history being evicted by the agent's own child, and both live
/// in the same unit, so a namespace moves the collision rather than resolving it. It would also
/// take <c>journalctl -u fl-agent</c> away from every guide that tells a reader to run it.
/// </para>
/// <para>
/// <b>Bounded and visibly truncated, never silent.</b> Suppressed lines are counted and the count
/// is written out, in the same shape journald uses for its own drops
/// (<c>Suppressed N messages from …</c>). Two extra lines per window is the whole overhead: one at
/// the moment the budget runs out, so the condition is visible immediately rather than at the end
/// of a ten-minute window, and one when the window closes carrying the total. A child that is
/// permanently broken therefore produces a small, regular, unmistakable drumbeat instead of either
/// a flood or a silence.
/// </para>
/// <para>
/// <b>The budget is shared across relaunches on purpose.</b> The instance belongs to the
/// supervisor, not to one child process, so a child that fails, floods and is restarted cannot
/// reset its own allowance by dying — which is exactly the shape a crash loop would take.
/// </para>
/// </remarks>
public sealed class ChildOutputBudget
{
    /// <summary>
    /// Lines a child may write per <see cref="DefaultWindow"/>.
    /// </summary>
    /// <remarks>
    /// Derived rather than picked. Guide 12 sizes the journal at 64 MB and calls that one to two
    /// weeks of this frame's logs, so a week of history means the whole frame stays under roughly
    /// 9 MB a day and one supervised child should be a modest share of that. Sixty lines per ten
    /// minutes is about 8,600 lines a day — of the order of 1.7 MB at the ~200 bytes a Kiosk log
    /// line runs to — while leaving a healthy slideshow, which changes photo twice a minute, an
    /// order of magnitude more room than it uses. Against the measured flood it drops 99.9% of the
    /// lines and keeps the first sixty of every window, which is more than enough to read what the
    /// child is complaining about.
    /// </remarks>
    public const int DefaultLinesPerWindow = 60;

    /// <summary>The window <see cref="DefaultLinesPerWindow"/> is measured over.</summary>
    /// <remarks>
    /// Long enough that the notice is not itself noise, short enough that a new fault appearing
    /// just after a budget is spent surfaces within ten minutes rather than within an hour. The
    /// delay is real and is the price of the bound; it is stated here rather than hidden.
    /// </remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    private readonly IAgentLog _log;
    private readonly IAgentClock _clock;
    private readonly string _child;
    private readonly int _budget;
    private readonly TimeSpan _window;
    private readonly Lock _gate = new();

    private DateTimeOffset _windowStart;
    private bool _started;
    private int _written;
    private int _suppressed;

    /// <summary>Creates a budget over the output of <paramref name="child"/>.</summary>
    /// <param name="log">Where the lines and the suppression notices go.</param>
    /// <param name="clock">Source of time, so a test does not have to wait out a window.</param>
    /// <param name="child">What to call the child in every line it produces.</param>
    /// <param name="linesPerWindow">Lines allowed per window; anything below one is treated as one.</param>
    /// <param name="window">The window, or null for <see cref="DefaultWindow"/>.</param>
    public ChildOutputBudget(
        IAgentLog log,
        IAgentClock clock,
        string child,
        int linesPerWindow = DefaultLinesPerWindow,
        TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(child);

        _log = log;
        _clock = clock;
        _child = child;
        _budget = Math.Max(1, linesPerWindow);
        _window = window is { Ticks: > 0 } given ? given : DefaultWindow;
    }

    /// <summary>How many lines this budget has dropped since the agent started.</summary>
    /// <remarks>
    /// Cumulative across windows and across relaunches, so it answers "has this frame been losing
    /// output" rather than "is it losing output this minute".
    /// </remarks>
    public int Dropped { get; private set; }

    /// <summary>
    /// Takes one line of the child's output, writing it or counting it as dropped.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> is the end-of-stream marker <c>Process</c>'s line-reading events
    /// deliver and is ignored, as is a blank line: neither carries anything and both would spend
    /// budget a real message needs.
    /// </remarks>
    public void Write(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        string? notice = null;
        string? rolled = null;

        lock (_gate)
        {
            var now = _clock.UtcNow;

            if (!_started)
            {
                _windowStart = now;
                _started = true;
            }
            else if (now - _windowStart >= _window)
            {
                rolled = CloseWindow();
                _windowStart = now;
            }

            if (_written < _budget)
            {
                _written++;
            }
            else
            {
                _suppressed++;
                Dropped++;

                if (_suppressed == 1)
                {
                    var until = (_windowStart + _window).UtcDateTime.ToString(
                        "HH:mm:ss",
                        CultureInfo.InvariantCulture);

                    notice = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{_child} has used its whole budget of {_budget} log lines per {Describe(_window)}. Everything else it writes before {until} UTC is dropped, so that this frame keeps its own history. The count follows when the window closes.");
                }

                line = null;
            }
        }

        // Outside the lock: writing is the slowest thing here and nothing below reads the counters.
        if (rolled is not null)
        {
            _log.Warn(rolled);
        }

        if (notice is not null)
        {
            _log.Warn(notice);
        }

        if (line is not null)
        {
            _log.Info($"{_child}: {line}");
        }
    }

    /// <summary>
    /// Writes any outstanding suppression count, for a child that has gone quiet or stopped.
    /// </summary>
    /// <remarks>
    /// Without it the last window's count would wait for a line that may never come — so a child
    /// that floods and then dies would take the size of its flood with it, which is the silence
    /// this class exists to avoid.
    /// </remarks>
    public void Flush()
    {
        string? rolled;

        lock (_gate)
        {
            rolled = CloseWindow();
            _windowStart = _clock.UtcNow;
        }

        if (rolled is not null)
        {
            _log.Warn(rolled);
        }
    }

    /// <summary>How long a window is, in the register a person reads.</summary>
    private static string Describe(TimeSpan window) =>
        window.TotalMinutes >= 1 && window.TotalMinutes == Math.Floor(window.TotalMinutes)
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)window.TotalMinutes} minutes")
            : string.Create(CultureInfo.InvariantCulture, $"{window.TotalSeconds:0.##} seconds");

    /// <summary>Resets the counters, returning the notice the closing window owes, if any.</summary>
    private string? CloseWindow()
    {
        var suppressed = _suppressed;

        _written = 0;
        _suppressed = 0;

        if (suppressed == 0)
        {
            return null;
        }

        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"Suppressed {suppressed} lines from {_child} over the last {Describe(_window)} ({Dropped} in total since this agent started).");

        return head
            + " They are gone, not hidden: the budget exists so that one noisy child cannot rotate"
            + " this frame's journal away.";
    }
}
