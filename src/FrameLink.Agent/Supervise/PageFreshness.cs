using System.Globalization;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Supervise;

/// <summary>What the agent knows about the document its browser is running.</summary>
public enum PageVerdict
{
    /// <summary>
    /// Nothing may be concluded — no page has reported, or none has reported a document age.
    /// </summary>
    /// <remarks>
    /// <see cref="Reconcile.ObservationOutcome.Unevaluable"/>'s reasoning, in the other
    /// responsibility: an
    /// answer that did not arrive is not evidence about the frame. A page nothing is known about is
    /// never refreshed.
    /// </remarks>
    Unknown,

    /// <summary>The running document is the app this agent serves.</summary>
    Fresh,

    /// <summary>The running document was served by an earlier agent carrying a different app.</summary>
    Stale,
}

/// <summary>
/// <b>Whether the page on the screen is the page the agent is serving</b> — §2.10's fifth
/// supervised behaviour, decision 84.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists for, measured 2026-08-16.</b> Commit <c>958ac9c</c> added an accent
/// colour computed in the agent and drawn by <c>app/frame-stage.js</c>. After deploying it the
/// agent <i>was</i> serving the new page — <c>curl</c> on the frame returned the new file, byte for
/// byte — the headline updated correctly because it is server-composed text on the live channel, and
/// the accent never appeared at all: the capture is entirely greyscale. Chromium had been running
/// since <c>09:09:28Z</c> and <c>fl-agent</c> restarted at <c>10:35:10Z</c>, so the live document
/// came from an agent that no longer existed. §2.8 makes the agent update itself; §2.1 puts the app
/// inside the agent; nothing put the two together, so <b>half of every update was invisible.</b>
/// </para>
/// <para>
/// <b>Why nothing already covered it.</b> The kiosk-liveness rule watches the local channel, and the
/// channel came back — the page's WebSocket reconnects on its own backoff, so the journal recorded
/// <i>"The page checked in after 0 s"</i> about a document an hour and a half old. A reconnect and a
/// load are the same event at the socket layer, which is why this needs a fact from inside the
/// document rather than a fact about the connection. <c>unit.chromium-kiosk.running-matches-content</c>
/// compares the running process's argv against the unit's <c>ExecStart</c>, and an app change moves
/// neither. The daily restart would eventually fix it, up to twenty-four hours later and by accident.
/// </para>
/// <para>
/// <b>The rule, and it is one sentence.</b> A document younger than this agent process was, by
/// construction, served by this agent process — so it is running the app this binary carries, and
/// that build is recorded. A document older than this agent process is running whatever was
/// recorded last. If the record disagrees with what is now served, the page is stale.
/// </para>
/// <para>
/// <b>Both halves of the comparison are monotonic on purpose.</b> The page reports
/// <c>performance.now()</c>, which counts from its own navigation; the agent measures itself with
/// <see cref="Environment.TickCount64"/>, which counts from system boot. Neither moves when
/// <c>systemd-timesyncd</c> steps a clock that had no RTC to start from, which on this hardware
/// happens seconds after every boot — and a wall-clock comparison across that step is a page judged
/// against an agent whose clock moved underneath it. The reading is also allowed to be up to one
/// heartbeat old, and the direction that costs is the safe one: an age measured a moment ago makes
/// the document look <i>younger</i> than it is, which loses a detection rather than inventing one.
/// </para>
/// <para>
/// <b>The record is durable, and that is what closes the multi-restart hole.</b> Keeping "did the
/// build change when I started" as a flag in memory would lose the fact the moment the agent
/// restarted again for any other reason — a crash, a <c>systemctl restart</c>, the next update — and
/// a page stranded two agents ago would then look fine for ever. Recording <i>what the running page
/// loaded</i> instead of <i>what changed at startup</i> makes the answer independent of how many
/// processes have come and gone since.
/// </para>
/// <para>
/// <b>It is inert for the update that introduces it, by construction, and that is the safety
/// property that matters most.</b> An agent that has never written the record has nothing to compare
/// against and reports <see cref="PageVerdict.Unknown"/>, so the first page it can ever refresh is
/// one loaded under a binary that already carries this file — which is to say a page that reports
/// its own call state. No page that predates the reporting half can be interrupted by the acting
/// half, and that is why <see cref="Local.PageMessage.InCall"/> can be a plain flag rather than a
/// third unknown.
/// </para>
/// </remarks>
public sealed class PageFreshness
{
    /// <summary>Where the build the running page loaded is remembered (§2.1's persisted state).</summary>
    public const string StateFileName = "app-build";

    private readonly IStateStore _store;
    private readonly IAgentLog _log;
    private readonly Func<TimeSpan> _uptime;
    private readonly Lock _gate = new();

    private string? _loaded;

    /// <summary>Creates the check over <paramref name="store"/>.</summary>
    /// <param name="store">Where the loaded build is recorded across restarts.</param>
    /// <param name="served">The app this binary carries — <see cref="Local.EmbeddedApp.BuildId"/>.</param>
    /// <param name="log">The journal.</param>
    /// <param name="uptime">
    /// How long this agent process has been running, monotonically. Defaults to a reading of
    /// <see cref="Environment.TickCount64"/> taken from construction, which is where a real agent
    /// builds this.
    /// </param>
    public PageFreshness(IStateStore store, string served, IAgentLog log, Func<TimeSpan>? uptime = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(served);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _log = log;

        var startedAt = Environment.TickCount64;
        _uptime = uptime ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt));

        Served = served;
        _loaded = store.ReadText(StateFileName)?.Trim() is { Length: > 0 } recorded ? recorded : null;
    }

    /// <summary>The app this binary serves.</summary>
    public string Served { get; }

    /// <summary>The app the running page loaded, or null while that is unknown.</summary>
    public string? Loaded
    {
        get
        {
            lock (_gate)
            {
                return _loaded;
            }
        }
    }

    /// <summary>Reads one report from the page and returns what it says about the document.</summary>
    /// <param name="documentAge">
    /// How long ago the running document began loading, or null if the page has not said.
    /// </param>
    /// <remarks>
    /// It records as well as answers, and the two cannot be separated without letting them disagree:
    /// the very reading that proves a document was served by this agent is the reading that makes
    /// the record true. Recording <i>before</i> judging is also what makes a refresh verify itself —
    /// the reloaded page reports a young document, the record moves to the served build, and the
    /// next call answers <see cref="PageVerdict.Fresh"/> without anything having to remember that a
    /// reload was asked for.
    /// </remarks>
    public PageVerdict Observe(TimeSpan? documentAge)
    {
        if (documentAge is not { } age)
        {
            return PageVerdict.Unknown;
        }

        if (age <= _uptime())
        {
            Record();
            return PageVerdict.Fresh;
        }

        lock (_gate)
        {
            if (_loaded is not { } loaded)
            {
                // Nothing was ever recorded, so an older document is not evidence of anything: this
                // is either the first agent to carry the check, or a frame whose state directory has
                // been replaced. Both mean "unknown", and the next page to load under this process
                // writes the record that makes the question answerable from then on.
                return PageVerdict.Unknown;
            }

            return string.Equals(loaded, Served, StringComparison.Ordinal)
                ? PageVerdict.Fresh
                : PageVerdict.Stale;
        }
    }

    /// <summary>Expected-versus-observed, in the one form §2.5 requires everywhere.</summary>
    public string Delta => string.Create(
        CultureInfo.InvariantCulture,
        $"expected the page to be running app {Served}, observed app {Loaded ?? "unknown"}");

    private void Record()
    {
        lock (_gate)
        {
            if (string.Equals(_loaded, Served, StringComparison.Ordinal))
            {
                return;
            }

            var previous = _loaded;
            _loaded = Served;
            _store.WriteText(StateFileName, Served);

            _log.Info(previous is null
                ? $"The page on this frame is running app {Served}."
                : $"The page on this frame is now running app {Served}, where it was running {previous}.");
        }
    }
}
