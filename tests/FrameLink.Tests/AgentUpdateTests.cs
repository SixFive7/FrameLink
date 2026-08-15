using System.Security.Cryptography;
using System.Text;
using FrameLink.Agent;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Link;
using FrameLink.Agent.State;
using FrameLink.Agent.Update;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// Self-update — version2.md §2.8.
/// </summary>
/// <remarks>
/// Two properties carry the whole design and both are asserted here rather than assumed. The
/// hourly out-of-band check is the <i>mechanism</i>, so it converges a frame with no socket
/// involved at all; and the agent <i>matches</i> the served version rather than comparing it, so
/// reverting the container tag reverts the fleet within the hour.
/// </remarks>
public sealed class AgentUpdateTests
{
    private const string RunningBinary = "the running binary";
    private static readonly Uri Endpoint = new("https://framelink.example.org/");

    [Fact]
    public async Task A_matching_version_downloads_nothing()
    {
        using var harness = new UpdateHarness("0.1.0", Feed("0.1.0"));

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.AlreadyMatching, outcome);
        Assert.Equal(0, harness.Releases.DownloadCalls);
        Assert.Empty(harness.Restart.Requests);
    }

    [Fact]
    public async Task A_newer_served_version_is_fetched_verified_and_put_in_place()
    {
        var feed = Feed("0.2.0");
        using var harness = new UpdateHarness("0.1.0", feed);

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Applied, outcome);
        Assert.Equal(feed.Payload, await harness.ReadTargetAsync());
        Assert.Single(harness.Restart.Requests);
    }

    [Fact]
    public async Task An_older_served_version_is_applied_just_the_same()
    {
        // §2.8: the agent matches the served version — upgrade or downgrade, always. Reverting the
        // container tag has to revert the fleet, so refusing a downgrade would break the operator's
        // only rollback.
        var feed = Feed("0.2.0");
        using var harness = new UpdateHarness("0.9.0", feed);

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Applied, outcome);
        Assert.Equal(feed.Payload, await harness.ReadTargetAsync());
        Assert.Single(harness.Restart.Requests);
    }

    [Fact]
    public async Task A_binary_that_fails_its_checksum_never_reaches_the_target()
    {
        var feed = Feed("0.2.0");
        using var harness = new UpdateHarness(
            "0.1.0",
            feed with { Release = feed.Release with { Sha256 = new string('a', 64) } });

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.VerificationFailed, outcome);
        Assert.Equal(RunningBinary, await harness.ReadTargetTextAsync());
        Assert.Empty(harness.Restart.Requests);
        Assert.False(File.Exists(harness.TargetPath + FileBinarySwap.StagingSuffix));
    }

    [Fact]
    public async Task A_truncated_download_is_rejected_before_it_is_hashed()
    {
        var feed = Feed("0.2.0");
        using var harness = new UpdateHarness(
            "0.1.0",
            feed with { Release = feed.Release with { SizeBytes = feed.Payload.Length + 4096 } });

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.VerificationFailed, outcome);
        Assert.Equal(RunningBinary, await harness.ReadTargetTextAsync());
        Assert.False(File.Exists(harness.TargetPath + FileBinarySwap.StagingSuffix));
    }

    [Fact]
    public async Task An_overlong_download_is_rejected_rather_than_filling_the_card()
    {
        var feed = Feed("0.2.0");
        using var harness = new UpdateHarness(
            "0.1.0",
            feed with { Payload = Encoding.UTF8.GetBytes(new string('x', 200_000)) });

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.VerificationFailed, outcome);
        Assert.Equal(RunningBinary, await harness.ReadTargetTextAsync());
    }

    [Fact]
    public async Task An_unreachable_server_changes_nothing_and_does_not_throw()
    {
        using var harness = new UpdateHarness("0.1.0", feed: null);

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Unreachable, outcome);
        Assert.Equal(RunningBinary, await harness.ReadTargetTextAsync());
        Assert.Empty(harness.Restart.Requests);
    }

    [Fact]
    public async Task A_download_that_dies_halfway_changes_nothing()
    {
        using var harness = new UpdateHarness("0.1.0", Feed("0.2.0"));
        harness.Releases.DownloadFails = true;

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.Unreachable, outcome);
        Assert.Equal(RunningBinary, await harness.ReadTargetTextAsync());
    }

    [Fact]
    public async Task The_hourly_tick_converges_the_frame_with_no_socket_involved()
    {
        // The property that makes every failure mode self-healing: a protocol mismatch that makes
        // the socket useless, a restarted server, a frame offline for a week. Nothing in this test
        // touches the control link, and the update still lands.
        var feed = Feed("0.2.0");
        using var harness = new UpdateHarness("0.1.0", feed);
        harness.Clock.Hold = true;
        using var stop = new CancellationTokenSource();
        harness.Clock.OnDelay = _ => stop.Cancel();

        await harness.Service.RunAsync(stop.Token);

        Assert.Equal(UpdateOutcome.Applied, harness.Service.LastOutcome);
        Assert.Equal(feed.Payload, await harness.ReadTargetAsync());
        Assert.Single(harness.Restart.Requests);
        Assert.Equal(UpdateService.DefaultInterval, Assert.Single(harness.Clock.Delays));
    }

    [Fact]
    public async Task The_handshake_only_makes_the_check_happen_sooner()
    {
        // §2.8: "The handshake is an optimisation, not a mechanism." The clock is held here, so the
        // hourly wait never elapses and only the trigger can produce a second check.
        using var harness = new UpdateHarness("0.1.0", Feed("0.1.0"));
        harness.Clock.Hold = true;
        using var stop = new CancellationTokenSource();

        var loop = harness.Service.RunAsync(stop.Token);
        await WaitForAsync(() => harness.Clock.HeldCount > 0);

        harness.Service.TriggerNow();
        await WaitForAsync(() => harness.Service.CompletedChecks >= 2);

        await stop.CancelAsync();
        harness.Clock.ReleaseOne();
        await loop;

        Assert.True(harness.Releases.ReleaseCalls >= 2, $"expected two checks, saw {harness.Releases.ReleaseCalls}");
    }

    [Fact]
    public async Task An_operator_who_turned_updates_off_gets_no_updates()
    {
        using var harness = new UpdateHarness("0.1.0", Feed("0.2.0"), enabled: false);

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.AlreadyMatching, outcome);
        Assert.Equal(0, harness.Releases.ReleaseCalls);
        Assert.Equal(RunningBinary, await harness.ReadTargetTextAsync());
        Assert.Empty(harness.Restart.Requests);
    }

    [Fact]
    public async Task A_frame_with_no_endpoint_yet_waits_rather_than_failing()
    {
        using var harness = new UpdateHarness("0.1.0", Feed("0.2.0"), withEndpoint: false);

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.NoEndpoint, outcome);
        Assert.Equal(0, harness.Releases.ReleaseCalls);
    }

    [Fact]
    public async Task The_served_version_reaches_the_screen_even_when_it_matches()
    {
        using var harness = new UpdateHarness("0.1.0", Feed("0.1.0"));

        await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("0.1.0", harness.Hub.Current.ServedAgentVersion);
    }

    [Fact]
    public async Task A_completed_swap_tells_the_live_connection_to_stand_down()
    {
        // Without this, an attempt happily reading a healthy socket keeps the old binary running
        // for as long as the Fleet Manager stays up.
        using var harness = new UpdateHarness("0.1.0", Feed("0.2.0"));

        await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(harness.Hub.Current.RestartPending);
    }

    [Fact]
    public void The_agent_reports_exactly_the_version_it_was_built_with()
    {
        // §2.8 matches the served version string against this one; it never compares them. That
        // makes the informational version a wire value, and the SDK's default behaviour breaks it:
        // it appends '.$(SourceRevisionId)', so a release published as 0.0.0+a273b31 reports itself
        // as 0.0.0+a273b31.<40-char-sha>. The two can never be equal, so every frame downloads the
        // binary it is already running, swaps it, restarts — and repeats an hour later, for good,
        // across the fleet. Verified by hand against the real build flags before this was written.
        Assert.DoesNotMatch(@"\.[0-9a-f]{40}$", AgentBuild.Version);
        Assert.DoesNotMatch(@"\.[0-9a-f]{7,}\.[0-9a-f]{7,}$", AgentBuild.Version);
    }

    [Fact]
    public async Task An_agent_offered_its_own_reported_version_does_nothing()
    {
        // The convergence loop has to terminate. If it does not, the symptom on a frame is not an
        // error, it is a restart every hour with nothing in the journal to explain it.
        using var harness = new UpdateHarness(AgentBuild.Version, Feed(AgentBuild.Version));

        var outcome = await harness.Service.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateOutcome.AlreadyMatching, outcome);
        Assert.Equal(0, harness.Releases.DownloadCalls);
    }

    [Fact]
    public void The_release_route_is_versionless_and_names_the_runtime()
    {
        // §4.2: "The update endpoint never changes shape either: plain, versionless HTTPS routes
        // outside the negotiated protocol." An agent too old to speak the protocol still repairs
        // itself through this URL.
        //
        // The runtime identifier is a path segment because that is the route the Fleet Manager
        // maps. This test previously asserted a query string and passed while the real feed
        // answered 404 to every agent alive — which is why AgentControlIntegrationTests now polls
        // the actual server rather than trusting either side's idea of the shape.
        var url = ControlRoutes.ReleaseFor(Endpoint, "linux-arm64");

        Assert.Equal("/agent/release/linux-arm64", url.AbsolutePath);
        Assert.Empty(url.Query);
    }

    [Fact]
    public void The_socket_route_is_the_agent_path_over_web_sockets()
    {
        Assert.Equal(new Uri("wss://framelink.example.org/agent"), WebSocketControlTransportFactory.SocketUriFor(Endpoint));
        Assert.Equal(new Uri("ws://192.168.1.9:8080/agent"), WebSocketControlTransportFactory.SocketUriFor(new Uri("http://192.168.1.9:8080/")));
    }

    private static ReleaseFeed Feed(string version, string? content = null)
    {
        var payload = Encoding.UTF8.GetBytes(content ?? $"the fl-agent binary for {version}");

        return new ReleaseFeed(
            new AgentRelease
            {
                Version = version,
                RuntimeIdentifier = "linux-arm64",
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
                SizeBytes = payload.Length,
                Url = "/agent/binary",
            },
            payload);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition never became true");
    }

    private sealed record ReleaseFeed(AgentRelease Release, byte[] Payload);

    private sealed class UpdateHarness : IDisposable
    {
        private readonly string _directory;

        public UpdateHarness(
            string currentVersion,
            ReleaseFeed? feed,
            bool enabled = true,
            bool withEndpoint = true)
        {
            _directory = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            TargetPath = Path.Combine(_directory, "fl-agent");
            File.WriteAllText(TargetPath, RunningBinary);

            Releases = new StubReleaseSource { Release = feed?.Release, Payload = feed?.Payload };
            Clock = new ManualClock();
            Hub = new AgentStatusHub(AgentStatusFactory.Starting());
            Restart = new RecordingRestart();

            Service = new UpdateService(
                Releases,
                new FileBinarySwap(TargetPath, new RecordingPermissions(), NullLog.Instance),
                Clock,
                Hub,
                Restart,
                NullLog.Instance,
                () => withEndpoint ? Endpoint : null,
                currentVersion,
                "linux-arm64")
            {
                Enabled = enabled,
            };
        }

        public UpdateService Service { get; }

        public StubReleaseSource Releases { get; }

        public ManualClock Clock { get; }

        public AgentStatusHub Hub { get; }

        public RecordingRestart Restart { get; }

        public string TargetPath { get; }

        public Task<byte[]> ReadTargetAsync() => File.ReadAllBytesAsync(TargetPath, TestContext.Current.CancellationToken);

        public Task<string> ReadTargetTextAsync() => File.ReadAllTextAsync(TargetPath, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
