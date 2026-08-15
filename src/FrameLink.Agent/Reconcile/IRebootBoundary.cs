using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Reconcile;

/// <summary>What happened when the loop tried to cross a reboot boundary.</summary>
public enum RebootCrossing
{
    /// <summary>
    /// The machine has booted and this code is running on the other side. The loop may verify.
    /// </summary>
    /// <remarks>
    /// On a frame this value is never returned — the process does not survive the reboot, so the
    /// verify happens in the <i>next</i> process, resumed from the journal. It is returned by the
    /// in-process boundary, which is what makes the whole cross-reboot sequence testable.
    /// </remarks>
    Crossed,

    /// <summary>
    /// The reboot was accepted and this process is about to be killed. The loop must stop and
    /// claim nothing.
    /// </summary>
    Restarting,

    /// <summary>The reboot could not be requested at all.</summary>
    Refused,
}

/// <summary>The loop's request to restart the machine.</summary>
public sealed record RebootRequest
{
    /// <summary>Which resource's change is being proven.</summary>
    public required string Resource { get; init; }

    /// <summary>The change that was just written, verbatim, for the journal and the log.</summary>
    public required string Change { get; init; }

    /// <summary>Which attempt this is.</summary>
    public required int Attempt { get; init; }
}

/// <summary>The result of a crossing.</summary>
/// <param name="Crossing">What happened.</param>
/// <param name="Detail">Why, when it was refused.</param>
public readonly record struct RebootOutcome(RebootCrossing Crossing, string? Detail = null);

/// <summary>
/// <b>The reboot boundary.</b> The seam that makes §2.4's "every resource reboots" testable.
/// </summary>
/// <remarks>
/// <para>
/// §2.4 forbids claiming "applied" from a successful write: only an observation made after the
/// setting had to survive a boot counts. That rule puts a process death in the middle of the
/// resource contract, which would normally make the most important behaviour in the agent the
/// one behaviour no test can reach. This interface is the answer, and it is a deliverable in its
/// own right because everything in M3 depends on being able to test it.
/// </para>
/// <para>
/// It works because the loop is written to be <b>resumable rather than continuous</b>. Before
/// crossing, the loop writes its intent to the journal; after crossing, it verifies. Those two
/// halves are the same two halves whether the boundary killed the process or not:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="SystemRebootBoundary"/> returns <see cref="RebootCrossing.Restarting"/> and the
/// machine goes down. The verify happens in the next process, which reads the journal and
/// re-enters at the verify step.
/// </description></item>
/// <item><description>
/// <see cref="InProcessRebootBoundary"/> returns <see cref="RebootCrossing.Crossed"/> after
/// changing the boot identity and running whatever the machine's <i>other</i> owners do at boot.
/// The same loop then verifies inline.
/// </description></item>
/// </list>
/// <para>
/// The second one is not a mock of the first: both drive the identical journal-write, identical
/// boot-identity comparison and identical verify. What differs is only whether the process
/// survives — and the journal exists precisely so that it does not have to.
/// </para>
/// </remarks>
public interface IRebootBoundary
{
    /// <summary>Restarts the machine so the change has to survive a boot.</summary>
    Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken);
}

/// <summary>Asks systemd to reboot the frame.</summary>
/// <remarks>
/// Returns rather than blocking forever. <c>systemctl reboot</c> is asynchronous — it returns
/// as soon as the job is queued — and the loop has to be able to stop cleanly in the seconds
/// between the request and the machine actually going down, or the last thing on the screen
/// before the panel dies is a frame mid-repaint.
/// </remarks>
public sealed class SystemRebootBoundary : IRebootBoundary
{
    private readonly ISystemControl _systemControl;
    private readonly IAgentLog _log;

    /// <summary>Creates the boundary over <paramref name="systemControl"/>.</summary>
    public SystemRebootBoundary(ISystemControl systemControl, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(systemControl);
        ArgumentNullException.ThrowIfNull(log);

        _systemControl = systemControl;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _log.Info($"{request.Resource}: rebooting to prove '{request.Change}' stuck (attempt {request.Attempt}).");

        var result = await _systemControl.RunAsync(["reboot"], cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return new RebootOutcome(RebootCrossing.Restarting);
        }

        // A refused reboot is a first-class failure, not an exception. The frame carries on
        // running and the resource stays AwaitingReboot with a reason on screen — which is the
        // honest picture, because the change really has been written and really has not been
        // proven.
        _log.Fail($"{request.Resource}: the reboot was refused — {result.Output}");
        return new RebootOutcome(RebootCrossing.Refused, result.Output);
    }
}

/// <summary>
/// A boundary that crosses without restarting anything.
/// </summary>
/// <remarks>
/// <para>
/// Two uses, and neither is a stub. In the test suite it is how the full
/// write-journal-reboot-verify sequence runs on a workstation, including the case that matters
/// most: <see cref="OnBoot"/> is where the machine's <i>other</i> owners get their turn, so a
/// test can model cloud-init putting the hostname back exactly as the mule does.
/// </para>
/// <para>
/// In a container — a virtual agent per §5.3 — it is also the correct production behaviour,
/// because a container has no machine to reboot and a <c>systemctl reboot</c> there would simply
/// fail. A virtual agent that reconciles without rebooting is a faithful model of every
/// non-hardware resource.
/// </para>
/// </remarks>
public sealed class InProcessRebootBoundary : IRebootBoundary
{
    private readonly MutableBootIdentity _boot;

    /// <summary>Creates a boundary that advances <paramref name="boot"/> on every crossing.</summary>
    public InProcessRebootBoundary(MutableBootIdentity boot)
    {
        ArgumentNullException.ThrowIfNull(boot);
        _boot = boot;
    }

    /// <summary>Every crossing so far, in order.</summary>
    public List<RebootRequest> Crossings { get; } = [];

    /// <summary>
    /// What happens during the boot, before the loop is allowed to verify.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the abstraction pays for itself. cloud-init, WirePlumber's
    /// device-state restore, <c>alsa-restore</c>, a package postinst — all of them run here on a
    /// real frame, and all of them can put a setting back. Modelling them as a callback makes
    /// "the write succeeded and the setting is still wrong" an ordinary, assertable outcome.
    /// </remarks>
    public Func<RebootRequest, CancellationToken, Task>? OnBoot { get; set; }

    /// <inheritdoc/>
    public async Task<RebootOutcome> CrossAsync(RebootRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Crossings.Add(request);
        _boot.Advance();

        if (OnBoot is not null)
        {
            await OnBoot(request, cancellationToken).ConfigureAwait(false);
        }

        return new RebootOutcome(RebootCrossing.Crossed);
    }
}
