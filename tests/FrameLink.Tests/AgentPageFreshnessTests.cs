using FrameLink.Agent;
using FrameLink.Agent.Local;
using FrameLink.Agent.Resources;
using FrameLink.Agent.State;
using FrameLink.Agent.Supervise;

namespace FrameLink.Tests;

/// <summary>
/// §2.10's fifth behaviour — <b>the page on the screen is the page the agent serves</b> (decision 84).
/// </summary>
/// <remarks>
/// <para>
/// The defect, measured on hardware 2026-08-16: commit <c>958ac9c</c> added an accent computed in
/// the agent and drawn by <c>frame-stage.js</c>; after deploying it the agent served the new file
/// correctly, the server-composed headline updated, and the accent never appeared — the capture is
/// entirely greyscale. Chromium had been up since <c>09:09:28Z</c> and <c>fl-agent</c> restarted at
/// <c>10:35:10Z</c>, so the live document came from an agent that no longer existed.
/// </para>
/// <para>
/// The reason nothing caught it is the reason these tests are written the way they are: at the
/// socket layer a reconnect and a page load are the same event, which is how the journal came to
/// record <i>"The page checked in after 0 s"</i> about a document ninety minutes old. Every test
/// here therefore drives the channel with a <i>document age</i> and never with a connection.
/// </para>
/// </remarks>
public sealed class AgentPageFreshnessTests
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    private const string OldApp = "1111111111111111";
    private const string NewApp = "2222222222222222";

    [Fact]
    public void A_document_younger_than_the_agent_is_the_app_this_agent_serves()
    {
        using var store = new TemporaryStore();
        var log = new RecordingLog();
        var freshness = new PageFreshness(store.Store, NewApp, log, () => TimeSpan.FromMinutes(10));

        // Nothing else has to be known. A page can only have fetched its document from a server
        // that was listening, and the only server listening since this process started is this one.
        Assert.Equal(PageVerdict.Fresh, freshness.Observe(TimeSpan.FromMinutes(4)));
        Assert.Equal(NewApp, freshness.Loaded);
        Assert.Equal(NewApp, store.Store.ReadText(PageFreshness.StateFileName));
    }

    [Fact]
    public void A_document_older_than_the_agent_is_stale_when_the_app_it_loaded_has_changed()
    {
        using var store = new TemporaryStore();
        store.Store.WriteText(PageFreshness.StateFileName, OldApp);

        var freshness = new PageFreshness(
            store.Store,
            NewApp,
            new RecordingLog(),
            () => TimeSpan.FromMinutes(2));

        // The measured shape: a browser up for ninety minutes across an agent that restarted two
        // minutes ago. Every connection-level signal on this frame says healthy.
        Assert.Equal(PageVerdict.Stale, freshness.Observe(TimeSpan.FromMinutes(90)));
        Assert.Contains(NewApp, freshness.Delta, StringComparison.Ordinal);
        Assert.Contains(OldApp, freshness.Delta, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restart_that_did_not_change_the_app_leaves_the_page_alone()
    {
        using var store = new TemporaryStore();
        store.Store.WriteText(PageFreshness.StateFileName, NewApp);

        var freshness = new PageFreshness(
            store.Store,
            NewApp,
            new RecordingLog(),
            () => TimeSpan.FromMinutes(2));

        // §2.4 restarts this process once per resource and §2.8 replaces the binary hourly, so
        // "the agent restarted" is the most ordinary event on a frame. Reloading the product for
        // one would be a blink every time anything at all was applied — the digest is over the
        // served bytes precisely so that an agent release which does not move the page does not
        // move the page.
        Assert.Equal(PageVerdict.Fresh, freshness.Observe(TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void A_frame_with_nothing_recorded_is_unknown_rather_than_stale()
    {
        using var store = new TemporaryStore();
        var freshness = new PageFreshness(
            store.Store,
            NewApp,
            new RecordingLog(),
            () => TimeSpan.FromMinutes(2));

        // This is the roll-out safety property and it is load-bearing. The agent that *introduces*
        // this behaviour has never written the record, so it can conclude nothing about the page
        // it inherits — which means the first page it can ever refresh is one loaded under a binary
        // that already carries the reporting half. No page that predates `inCall` can be
        // interrupted by the half that acts on it.
        Assert.Equal(PageVerdict.Unknown, freshness.Observe(TimeSpan.FromMinutes(90)));
        Assert.Null(freshness.Loaded);
    }

    [Fact]
    public void A_page_that_says_nothing_about_its_document_is_never_refreshed()
    {
        using var store = new TemporaryStore();
        store.Store.WriteText(PageFreshness.StateFileName, OldApp);

        var freshness = new PageFreshness(
            store.Store,
            NewApp,
            new RecordingLog(),
            () => TimeSpan.FromMinutes(2));

        // An answer that did not arrive is not evidence about the frame — ObservationOutcome's rule,
        // in the other responsibility.
        Assert.Equal(PageVerdict.Unknown, freshness.Observe(null));
    }

    [Fact]
    public void The_record_outlives_an_agent_that_never_managed_to_act_on_it()
    {
        using var store = new TemporaryStore();

        // Agent A serves the old app and a page loads under it.
        var a = new PageFreshness(store.Store, OldApp, new RecordingLog(), () => TimeSpan.FromMinutes(30));
        Assert.Equal(PageVerdict.Fresh, a.Observe(TimeSpan.FromMinutes(5)));

        // Agent B serves the new app, sees the stale page, and dies before doing anything about it.
        var b = new PageFreshness(store.Store, NewApp, new RecordingLog(), () => TimeSpan.FromMinutes(1));
        Assert.Equal(PageVerdict.Stale, b.Observe(TimeSpan.FromMinutes(35)));

        // Agent C serves the same new app. A flag saying "the build changed when I started" would
        // be false here and the page would be stranded for ever; the record says what the *page*
        // loaded, so the answer does not depend on how many processes have come and gone.
        var c = new PageFreshness(store.Store, NewApp, new RecordingLog(), () => TimeSpan.FromMinutes(1));
        Assert.Equal(PageVerdict.Stale, c.Observe(TimeSpan.FromMinutes(36)));
    }

    [Fact]
    public void The_app_build_is_a_digest_of_the_page_and_not_the_agents_version()
    {
        var build = EmbeddedApp.BuildId;

        Assert.Equal(16, build.Length);
        Assert.All(build, character => Assert.Contains(character, "0123456789abcdef"));

        // Stable within a process, and — the part that matters — not the agent's version. §2.8
        // ships a new agent version hourly whether or not a byte of the app moved, so keying the
        // refresh on the version would blink the product on every release.
        Assert.Equal(build, EmbeddedApp.BuildId);
        Assert.NotEqual(AgentBuild.Version, build);
    }

    [Fact]
    public async Task A_page_left_over_from_a_previous_agent_is_told_to_reload()
    {
        using var frame = new RefreshedFrame(loadedApp: OldApp, servedApp: NewApp);

        frame.Check(documentAge: TimeSpan.FromMinutes(90));

        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Equal(Supervisor.ReloadCommand, Assert.Single(frame.Commands));

        // Never a browser restart. The stale thing is a document, and the narrowest action that
        // replaces a document is the one to take: a unit restart tears down a renderer, a
        // compositor connection and a GPU context to fix what a reload fixes in about a second.
        Assert.Empty(frame.Session.Commands);

        // §2.10 against §2.6's ladder: a supervised action leaves the device where it found it.
        // A stale page must not stop the product, blank the frame or narrate a repair — the frame
        // in the measured incident was showing photographs correctly the whole time.
        Assert.Equal(DeviceState.InSync, frame.Hub.Current.Condition.State);
        Assert.True(frame.Hub.Current.ProductRuns);
        Assert.Equal(Supervisor.PageRefresh, frame.Hub.Current.Supervision?.Behaviour);

        // §1.2 principle 3: nothing is repaired invisibly.
        var reported = Assert.Single(
            frame.Telemetry.Events,
            entry => string.Equals(entry.Kind, Supervisor.SupervisionEventKind, StringComparison.Ordinal));
        Assert.Equal(Supervisor.PageRefresh, reported.Resource);
        Assert.Contains(NewApp, reported.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reloaded_page_settles_the_condition_without_anything_remembering_the_ask()
    {
        using var frame = new RefreshedFrame(loadedApp: OldApp, servedApp: NewApp);

        frame.Check(TimeSpan.FromMinutes(90));
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));

        // The page reloaded, so its document is younger than this agent process. Observe and verify
        // are the same reading here for the same reason §2.3 makes them the same method: a check
        // written against "did the command go out" reports success while the page sits unchanged.
        frame.Check(TimeSpan.FromSeconds(2));

        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Equal(NewApp, frame.Store.Store.ReadText(PageFreshness.StateFileName));
    }

    [Fact]
    public async Task A_page_refresh_waits_out_a_call_and_takes_it_when_the_call_ends()
    {
        using var frame = new RefreshedFrame(loadedApp: OldApp, servedApp: NewApp);

        frame.Check(TimeSpan.FromMinutes(90), inCall: true);

        // The failure mode that is worse than the defect. A frame in a call is a telephone somebody
        // is talking into, and a cosmetic staleness must never be the thing that ends a
        // conversation.
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Empty(frame.Commands);
        Assert.Contains("call is in progress", frame.Supervisor.LastStandDown, StringComparison.Ordinal);

        // Not marked done, so the first tick after the call ends takes it — the same shape as the
        // daily restart waiting out a call and then taking the run it missed.
        frame.Check(TimeSpan.FromMinutes(91), inCall: false);
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Equal(Supervisor.ReloadCommand, Assert.Single(frame.Commands));
    }

    [Fact]
    public async Task A_refresh_the_page_ignores_is_spaced_by_a_cooldown_and_becomes_a_fault()
    {
        using var frame = new RefreshedFrame(loadedApp: OldApp, servedApp: NewApp);
        frame.Check(TimeSpan.FromMinutes(90));

        Assert.Equal(1, await frame.Supervisor.TickAsync(None));

        // The page never reloads. Without the floor this repeats on every 15 s tick for ever.
        frame.Clock.UtcNow += TimeSpan.FromMinutes(1);
        frame.Check(TimeSpan.FromMinutes(91));
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Contains("cooldown", frame.Supervisor.LastStandDown, StringComparison.Ordinal);

        // Past the floor it is asked again — the restarts continue, because §2.10's signal is a
        // rate and never a budget. What the repetition buys is that somebody is told.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            frame.Clock.UtcNow += TimeSpan.FromMinutes(6);
            frame.Check(TimeSpan.FromMinutes(97 + (attempt * 6)));
            Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        }

        Assert.True(frame.Hub.Current.Supervision?.AtFaultLevel);
        Assert.Contains(
            frame.Telemetry.Events,
            entry => string.Equals(entry.Kind, Supervisor.SupervisionFaultEventKind, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_reconciler_working_on_the_browser_keeps_the_page_refresh_off_it()
    {
        using var frame = new RefreshedFrame(loadedApp: OldApp, servedApp: NewApp);
        frame.Check(TimeSpan.FromMinutes(90));

        frame.Interlock.Applying(ChromiumKioskRunningResource.ResourceName);

        // §2.10 clause 1, unchanged for the new behaviour: a browser the reconciler is deliberately
        // holding down is not one to send commands to. It is about to be restarted anyway, which
        // reloads the page for free.
        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Empty(frame.Commands);
    }

    [Fact]
    public async Task The_daily_restart_finally_has_a_producer_for_the_call_it_defers_to()
    {
        using var frame = new RefreshedFrame(loadedApp: NewApp, servedApp: NewApp, zone: TimeZoneInfo.Utc);
        frame.Store.Store.WriteText(Supervisor.DailyRestartStampFile, "2026-08-15");

        // §2.10 has said since it was written that the daily restart "stands down while a call is
        // active", and until the page began reporting the level nothing in the agent ever set
        // Supervisor.CallActive — only tests did. On a real frame the 03:00 restart would have
        // ended a call in progress, silently.
        frame.Clock.UtcNow = new DateTimeOffset(2026, 8, 16, 3, 0, 30, TimeSpan.Zero);
        frame.Check(TimeSpan.FromMinutes(5), inCall: true);
        Assert.True(frame.Supervisor.CallActive);

        Assert.Equal(0, await frame.Supervisor.TickAsync(None));
        Assert.Empty(frame.Session.Commands);
        Assert.Contains("call is in progress", frame.Supervisor.LastStandDown, StringComparison.Ordinal);

        frame.Check(TimeSpan.FromMinutes(10), inCall: false);
        Assert.False(frame.Supervisor.CallActive);
        Assert.Equal(1, await frame.Supervisor.TickAsync(None));
        Assert.Contains("systemctl --user restart chromium-kiosk.service", frame.Session.Commands);
    }

    [Fact]
    public void The_page_says_how_old_its_document_is_and_reloads_when_it_is_told_to()
    {
        var stage = AgentButtonTests.Asset("frame-stage.js");
        var app = AgentButtonTests.Asset("frame-app.js");

        // The one fact only the page can supply. A reconnect and a load are the same event at the
        // socket layer; `performance.now()` counts from this document's own navigation, so it is
        // what separates them — and it is monotonic, which matters on a Pi whose clock steps
        // seconds after every boot.
        Assert.Contains("documentAgeMs", stage, StringComparison.Ordinal);
        Assert.Contains("performance.now()", stage, StringComparison.Ordinal);

        // The reload is acted on inside this file rather than dispatched as an app event. A page
        // whose app half failed to load still has to be replaceable, and that page never listens
        // for `framelink-command`.
        Assert.Contains(Supervisor.ReloadCommand, stage, StringComparison.Ordinal);
        Assert.Contains("location.reload()", stage, StringComparison.Ordinal);

        // The page's own half of the call guard. The agent's copy of `inCall` is up to one
        // heartbeat old; this one cannot be.
        Assert.Contains("inCall", stage, StringComparison.Ordinal);
        Assert.Contains("callStarted", stage, StringComparison.Ordinal);
        Assert.Contains("callStarted", app, StringComparison.Ordinal);
    }

    /// <summary>A supervisor over a green frame with one page connected to it.</summary>
    private sealed class RefreshedFrame : IDisposable
    {
        private readonly IDisposable _attachment;

        public RefreshedFrame(string loadedApp, string servedApp, TimeZoneInfo? zone = null)
        {
            Hub = new AgentStatusHub(AgentStatusFactory.Green());
            Clock = new ManualClock();
            Interlock = new SupervisionInterlock();
            Store.Store.WriteText(PageFreshness.StateFileName, loadedApp);

            _attachment = Channel.Attach((message, _) =>
            {
                if (message.Command is { Length: > 0 } command)
                {
                    Commands.Add(command);
                }

                return Task.CompletedTask;
            });

            Supervisor = new Supervisor(new SupervisionServices
            {
                Channel = Channel,
                Session = Session,
                Memory = Memory,
                Interlock = Interlock,
                Hub = Hub,
                Telemetry = Telemetry,
                Clock = Clock,
                Log = Log,
                Store = Store.Store,
                TimeZone = zone ?? TimeZoneInfo.Utc,
                DeviceId = "TEST-DEVI-CEID-0001",

                // Two minutes of uptime against a browser that has been up for an hour and a half:
                // the measured shape of 2026-08-16, where fl-agent restarted at 10:35:10Z and
                // chromium had been running since 09:09:28Z.
                Freshness = new PageFreshness(Store.Store, servedApp, Log, () => TimeSpan.FromMinutes(2)),
            });
        }

        public TemporaryStore Store { get; } = new();

        public LocalChannel Channel { get; } = new();

        public FakeUserSession Session { get; } = new();

        public StubMemoryProbe Memory { get; } = new();

        public SupervisionInterlock Interlock { get; }

        public AgentStatusHub Hub { get; }

        public RecordingTelemetry Telemetry { get; } = new();

        public ManualClock Clock { get; }

        public RecordingLog Log { get; } = new();

        public Supervisor Supervisor { get; }

        /// <summary>Every command the connected page has been sent.</summary>
        public List<string> Commands { get; } = [];

        /// <summary>One check-in from the page, as `frame-stage.js` sends it.</summary>
        public void Check(TimeSpan documentAge, bool inCall = false) =>
            Channel.Receive(
                new PageMessage
                {
                    Kind = PageMessage.KindAlive,
                    DocumentAgeMs = documentAge.TotalMilliseconds,
                    InCall = inCall,
                },
                Clock.UtcNow);

        public void Dispose()
        {
            _attachment.Dispose();
            Store.Dispose();
        }
    }
}
