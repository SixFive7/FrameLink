using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>Everything the catalog needs to build its resources.</summary>
public sealed record DeviceCatalogContext
{
    /// <summary>The parts of the filesystem the agent does not own.</summary>
    public required ISystemFiles Files { get; init; }

    /// <summary>The agent's own state directory.</summary>
    public required IStateStore Store { get; init; }

    /// <summary>How external commands are run, with compiled argument vectors only (§2.2).</summary>
    public required IProcessRunner Processes { get; init; }

    /// <summary>The narrow window onto systemd.</summary>
    public required ISystemControl SystemControl { get; init; }

    /// <summary>Whether anything can show a picture.</summary>
    public required IDisplayProbe Display { get; init; }

    /// <summary>How a reboot is told from a service restart.</summary>
    public required IBootIdentity Boot { get; init; }

    /// <summary>Source of time.</summary>
    public required IAgentClock Clock { get; init; }

    /// <summary>Where refusals and rollbacks are recorded.</summary>
    public required IAgentLog Log { get; init; }

    /// <summary>The Fleet Manager's effective settings (§3.4).</summary>
    public FleetValues Values { get; init; } = FleetValues.None;

    /// <summary>
    /// What the Fleet Manager last actually said — including whether it said anything (§2.6).
    /// </summary>
    /// <remarks>
    /// The default is <see cref="ServerAnswer.Silence"/> and not "not adopted", which is the
    /// whole distinction: a catalog built before anything has answered knows nothing about this
    /// frame's adoption, and must not act as though it had been told.
    /// </remarks>
    public Func<ServerAnswer> FleetAnswer { get; init; } = () => ServerAnswer.Silence;

    /// <summary>The name to keep when the Fleet Manager has not set one.</summary>
    public string? FallbackHostname { get; init; }

    /// <summary>The display name the Fleet Manager assigned at adoption.</summary>
    public Func<string?> DesiredDeviceName { get; init; } = () => null;
}

/// <summary>
/// The compiled catalog — §2.2's "static logic, dynamic values" as an object graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is M3 in progress, not the fleet's catalog.</b> The full enumeration is 79 resources
/// in <c>reference/resource-catalog.md</c>. What was here at M2 is the set that makes every rung
/// of §2.3's status vocabulary reachable by something that touches a real system; M3 adds the
/// catalog's blocks to it in dependency order, and the package block is the first of them.
/// </para>
/// <para>
/// <b>Declaration order is the tie-break</b> in <see cref="ResourceGraph"/>, so the order below
/// is the order a bare frame converges in. The display comes first by explicit decision: §5.5
/// would schedule a <c>/boot/firmware</c> write last, and the catalog's proposed ordering puts
/// the panel overlay 76th of 79, but a frame that provisions with a dark panel has no honesty
/// mechanism at all — measured on the mule 2026-08-15, a stock image has no framebuffer and
/// every console write succeeds invisibly. §2.7 wins for this one resource, and §5.5's other
/// three mitigations pay for it (<see cref="BootPartitionGuard"/>).
/// </para>
/// <para>
/// The two display resources depend on nothing else, which is the point: lighting the panel
/// needs no package, no session and no adoption, and a pending frame has to be able to show its
/// own fingerprint (§3.3).
/// </para>
/// </remarks>
public static class DeviceCatalog
{
    /// <summary>Builds the M2 resource set, in declaration order.</summary>
    public static IReadOnlyList<IResource> Build(DeviceCatalogContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var guard = new BootPartitionGuard(
            context.Files,
            context.Store,
            context.Boot,
            context.Clock,
            context.Log);

        return
        [
            // First, so that everything after it can be watched happening.
            new DisplayPanelOverlayResource(context.Files, guard, context.Display, context.Log),
            new ConsoleRotationResource(context.Files, guard, context.Log),

            // Second, so that if anything below goes wrong there is a record of it. A volatile
            // journal is what made the August 2026 failures invisible for days.
            new JournalStorageResource(context.Files, context.Values),

            // The root of everything the Fleet Manager supplies a value for (decision 34).
            new AdoptionResource(context.Store, context.FleetAnswer),
            new DeviceNameResource(context.Store, context.DesiredDeviceName),

            // Positions 6–22 of the catalog's own ordering: the package block, after the agent
            // roots and ahead of system configuration. None of these depends on adoption, so a
            // pending frame still builds its kiosk stack — which is what §2.7's browser stage
            // needs in order to render the repair screen the pending frame is showing.
            .. PackageCatalog.Build(new AptPackages(context.Processes)),

            new HostnameResource(context.Files, context.Processes, context.Values, context.FallbackHostname),

            // The three-level chain that makes Blocked(dependency) and the escalation ladder
            // reachable against real system state: the unit file, its enablement, and the
            // governor value the unit is supposed to produce at boot.
            new CpuGovernorUnitResource(context.Files, context.SystemControl),
            new CpuGovernorUnitEnabledResource(context.SystemControl),
            new CpuGovernorResource(context.Files, context.Values),
        ];
    }

    /// <summary>Builds the set and validates its ordering (§2.2).</summary>
    public static ResourceGraph BuildGraph(DeviceCatalogContext context) => new(Build(context));
}
