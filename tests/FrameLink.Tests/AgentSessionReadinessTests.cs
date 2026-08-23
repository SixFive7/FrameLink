using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// The shared session-readiness gate — <see cref="UserSessionGate"/> and the eleven Observes that
/// sit behind it or deliberately do not.
/// </summary>
/// <remarks>
/// <para>
/// The defect these cover was measured on the mule across ten boots: the agent's first reconcile
/// pass runs at boot+10.0–10.6 s and the user manager comes up 0.03–0.7 s <i>after</i> its verdict,
/// so every resource whose answer lives in the login session was reading "not there" and reporting
/// drift on a frame where nothing was wrong. Four of them burned attempts to escalation.
/// </para>
/// <para>
/// The load-bearing assertion in each case is <see cref="ObservationOutcome.Unevaluable"/> rather
/// than merely "not in sync": drift spends an attempt, acts and reboots, and it was the spending
/// that turned a ten-second race into two escalations and twelve blocked dependents.
/// </para>
/// </remarks>
public sealed class AgentSessionReadinessTests
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    private static FakeUserSession Waiting() => new()
    {
        Readiness = new SessionReadiness(false, "the login session has not started yet (/run/user/1000 does not exist)"),
    };

    [Fact]
    public async Task The_probe_reports_the_missing_runtime_directory_by_name()
    {
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();
        processes.Answers["id -u framelink"] = new ProcessResult(0, "1000", string.Empty);

        var session = new LoginUserSession(processes, () => "framelink", files.Files);

        var readiness = await session.ReadinessAsync(None);

        Assert.False(readiness.Ready);
        Assert.Contains("/run/user/1000", readiness.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_probe_waits_for_the_bus_and_not_only_for_the_directory()
    {
        // The measured symptom of three of the four escalating resources was the bus, not the
        // directory: "Failed to connect to user scope bus ... No such file or directory". A gate
        // that passed as soon as /run/user/<uid> appeared would still let those three report drift.
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();
        processes.Answers["id -u framelink"] = new ProcessResult(0, "1000", string.Empty);

        var session = new LoginUserSession(processes, () => "framelink", files.Files);

        files.Files.EnsureDirectory("/run/user/1000");
        var starting = await session.ReadinessAsync(None);
        Assert.False(starting.Ready);
        Assert.Contains("/run/user/1000/bus", starting.Why, StringComparison.Ordinal);

        files.Files.WriteText("/run/user/1000/bus", string.Empty);
        Assert.True((await session.ReadinessAsync(None)).Ready);
    }

    [Fact]
    public async Task A_user_that_does_not_resolve_reports_ready_so_the_failure_stays_visible()
    {
        // Failing towards silence here would hide a misconfigured device.user behind "not settled
        // yet" for ever. There is no session coming on a frame with no such account, so the
        // resources behind the gate are let through to report what they genuinely find.
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner { Default = new ProcessResult(1, string.Empty, "no such user") };

        var session = new LoginUserSession(processes, () => "nobody", files.Files);

        Assert.True((await session.ReadinessAsync(None)).Ready);
    }

    [Fact]
    public async Task The_gate_carries_the_resources_own_expected_value_into_the_unevaluable()
    {
        var session = Waiting();

        var waiting = await UserSessionGate.NotSettledAsync(session, "enabled", None);

        Assert.NotNull(waiting);
        Assert.Equal(ObservationOutcome.Unevaluable, waiting.Outcome);
        Assert.Equal("enabled", waiting.Expected);
        Assert.Contains("could not be determined", waiting.Delta, StringComparison.Ordinal);
        Assert.Contains("/run/user/1000", waiting.Delta, StringComparison.Ordinal);

        session.Readiness = SessionReadiness.Up;
        Assert.Null(await UserSessionGate.NotSettledAsync(session, "enabled", None));
    }

    [Fact]
    public async Task The_four_measured_resources_wait_instead_of_drifting()
    {
        // The exact four that were burning attempts on the mule, with the two that reached 5/5
        // first. Each is asserted Unevaluable, which is what stops the attempt being spent.
        using var files = new TemporaryFiles();
        var processes = new RecordingProcessRunner();

        var labwcSession = Waiting();
        var labwc = new BashProfileLabwcResource(files.Files, processes, labwcSession);
        files.Seed(labwc.Path, BashProfileLabwcResource.DesiredContent);
        Assert.Equal(ObservationOutcome.Unevaluable, (await labwc.ObserveAsync(None)).Outcome);

        var portalSession = Waiting();
        var portal = new PortalDesktopDropInResource(files.Files, portalSession);
        await portal.ActAsync(None);
        Assert.Equal(ObservationOutcome.Unevaluable, (await portal.ObserveAsync(None)).Outcome);

        Assert.Equal(
            ObservationOutcome.Unevaluable,
            (await new ChromiumKioskEnabledResource(Waiting()).ObserveAsync(None)).Outcome);

        Assert.Equal(
            ObservationOutcome.Unevaluable,
            (await new CameraUnitEnabledResource(Waiting()).ObserveAsync(None)).Outcome);
    }

    [Fact]
    public async Task The_four_the_audit_named_wait_too_rather_than_being_bitten_in_turn()
    {
        // 9b83e81's audit named this family in advance: the runtime-shaped Observes nearby. They
        // are gated before they escalate rather than after, which is the whole reason for one
        // shared gate instead of five copies of d275689's window.
        using var files = new TemporaryFiles();

        var session = Waiting();
        var unit = new ChromiumKioskUnitResource(files.Files, session);
        await unit.ActAsync(None);
        var running = new ChromiumKioskRunningResource(files.Files, session, unit);
        Assert.Equal(ObservationOutcome.Unevaluable, (await running.ObserveAsync(None)).Outcome);

        Assert.Equal(
            ObservationOutcome.Unevaluable,
            (await new CameraNodeResource(Waiting(), new MemorySystemFiles()).ObserveAsync(None)).Outcome);

        Assert.Equal(
            ObservationOutcome.Unevaluable,
            (await new PortalCameraInterfaceResource(Waiting()).ObserveAsync(None)).Outcome);

        Assert.Equal(
            ObservationOutcome.Unevaluable,
            (await new PortalCameraPermissionResource(Waiting()).ObserveAsync(None)).Outcome);

        var autostart = new LabwcAutostartResource(files.Files, Waiting(), FleetValues.None);
        Assert.Equal(
            ObservationOutcome.Unevaluable,
            (await new DisplayTransformResource(Waiting(), autostart).ObserveAsync(None)).Outcome);
    }

    [Fact]
    public async Task A_durable_half_that_is_wrong_is_still_drift_while_the_session_is_down()
    {
        // d275689's rule, kept: "the window forgives one clause and one only". A .bash_profile with
        // the wrong bytes will never start a compositor, and that is as true ten seconds into a
        // boot as ten minutes in — so the file half is compared on every observation and only the
        // runtime half is gated. Without this the gate would hide real faults for the whole of
        // every boot.
        using var files = new TemporaryFiles();
        var session = Waiting();
        var resource = new BashProfileLabwcResource(files.Files, new RecordingProcessRunner(), session);

        files.Seed(resource.Path, "# not the guarded exec labwc block\n");

        var observation = await resource.ObserveAsync(None);

        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
        Assert.False(observation.InSync);
    }

    [Fact]
    public async Task A_settled_session_still_reports_the_real_verdict()
    {
        // The gate must not become the place a real failure goes to be quiet — CameraNodeResource's
        // own remark. Once the session is up, a failing wpctl is drift exactly as it always was.
        var session = new FakeUserSession
        {
            Default = new ProcessResult(1, string.Empty, "wpctl: command not found"),
        };

        var observation = await new CameraNodeResource(session, new MemorySystemFiles()).ObserveAsync(None);

        Assert.Equal(ObservationOutcome.Drifted, observation.Outcome);
        Assert.Contains("wpctl", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_two_resources_whose_predicate_is_not_session_scoped_are_left_alone()
    {
        // Both run a session command and neither is gated, because in both the probe rides in the
        // observed text while the predicate stays on the file the resource owns. Gating them would
        // hide a genuinely wrong file for the first ten seconds of every boot and buy nothing.
        // Asserted so that a later reading of "eleven Observes touch the session" cannot quietly
        // turn into "eleven Observes should be gated".
        using var files = new TemporaryFiles();
        var session = Waiting();

        var wireplumber = new WirePlumberCameraMonitorsResource(files.Files, session);
        await wireplumber.ActAsync(None);
        var settled = await wireplumber.ObserveAsync(None);

        Assert.NotEqual(ObservationOutcome.Unevaluable, settled.Outcome);
        Assert.True(settled.InSync);
    }
}
