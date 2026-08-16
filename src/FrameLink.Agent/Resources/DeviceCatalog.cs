using FrameLink.Agent.Hosting;
using FrameLink.Agent.Kiosk;
using FrameLink.Agent.Local;
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

    /// <summary>
    /// The login user's home and their <c>systemd --user</c> manager.
    /// </summary>
    /// <remarks>
    /// Required from the session and kiosk block onwards: everything guide 5 builds lives in one
    /// unprivileged user's session, and the agent is root in the system manager.
    /// </remarks>
    public required IUserSession Session { get; init; }

    /// <summary>The server the agent serves the product app and the repair screen from (§2.1).</summary>
    public required LocalOrigin Origin { get; init; }

    /// <summary>The page's own reports, for the <c>app.config.*</c> cross-check.</summary>
    public required LocalChannel Channel { get; init; }

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

    /// <summary>
    /// The agent's own claim on the call button's GPIO line (guide 11), where one is running.
    /// </summary>
    /// <remarks>
    /// Optional rather than required, because the claim is a live object with a loop behind it and
    /// a catalog is also built in places that have no such loop — the graph tests, and anything
    /// that only wants to inspect the resource set. <c>gpio.button.line</c> reports its absence as
    /// the fault it would be on a frame rather than skipping itself.
    /// </remarks>
    public ButtonWatch? Button { get; init; }

    /// <summary>
    /// The Immich Kiosk child the agent supervises (guide 9), where one is running.
    /// </summary>
    /// <remarks>
    /// Optional for the same reason <see cref="Button"/> is: it is a live object with a process
    /// behind it, and a catalog is also built where there is no process to have. When it is absent
    /// the block still builds — against a child that is simply never running — so the graph, the
    /// dependency edges and the resource count stay assertable off a frame.
    /// </remarks>
    public KioskProcess? Kiosk { get; init; }

    /// <summary>
    /// How the pinned Immich Kiosk release is fetched (§2.1: fetched, never redistributed).
    /// </summary>
    /// <remarks>
    /// Absent means <see cref="UnreachableKioskDownload"/>, so a catalog built off a frame reports
    /// the fetch as unreachable rather than reaching the network from a test.
    /// </remarks>
    public IKioskDownload? KioskDownload { get; init; }

    /// <summary>
    /// How the pinned reSpeaker control tool is fetched (decision 63: fetched, never vendored).
    /// </summary>
    /// <remarks>
    /// Its own seam rather than a share of <see cref="KioskDownload"/>, following the same house
    /// shape <c>ILiveKitDownload</c> takes: one installer, one download, so a test can starve or
    /// corrupt either without touching the other. Absent means
    /// <see cref="UnreachableXvfHostDownload"/>, so a catalog built off a frame reports the fetch as
    /// unreachable rather than reaching the network from a test.
    /// </remarks>
    public IXvfHostDownload? XvfHostDownload { get; init; }

    /// <summary>How files the agent creates are locked down.</summary>
    /// <remarks>
    /// Needed from the kiosk block onwards: the fetched executable has to carry the executable bit,
    /// which is a mode the installer sets on the staging file <i>before</i> the rename, so that the
    /// target is never briefly non-executable.
    /// </remarks>
    public IFilePermissions Permissions { get; init; } = PosixFilePermissions.Instance;

    /// <summary>The version of the binary that is executing (§2.8).</summary>
    public string RunningVersion { get; init; } = AgentBuild.Version;

    /// <summary>
    /// What the Fleet Manager's versionless update endpoint last served, or null if it has never
    /// answered.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a value, so <c>agent.version</c> notices a fleet rolled back between
    /// two passes. Null is the honest default off a frame: nothing has answered, so nothing is
    /// known, and the resource reports that rather than inventing a match.
    /// </remarks>
    public Func<string?> ServedVersion { get; init; } = () => null;

    /// <summary>Brings the out-of-band update check forward (§2.8).</summary>
    /// <remarks>
    /// Defaults to doing nothing, which is the correct behaviour where there is no update loop to
    /// wake: the hourly tick is the mechanism and this is only the optimisation, so a catalog built
    /// without one still describes the resource truthfully.
    /// </remarks>
    public Action ConvergeVersion { get; init; } = () => { };

    /// <summary>The identity this process is running as (§2.9, §3.3).</summary>
    /// <remarks>
    /// Read through a delegate for the same reason <see cref="FleetAnswer"/> is: the value
    /// <c>agent.keypair</c> compares against is what the frame <i>is</i> right now, and a catalog
    /// that captured it at construction could never observe it having moved.
    /// </remarks>
    public Func<string> DeviceId { get; init; } = () => "unknown";
}

/// <summary>
/// The compiled catalog — §2.2's "static logic, dynamic values" as an object graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>M3 is complete: this is the whole catalog.</b> <c>reference/resource-catalog.md</c>
/// enumerates <b>80</b> resources, of which <b>79 are implementable</b> and all 79 are here.
/// <c>pkg.git</c> is the one that is not, and it is an exclusion rather than a gap — open
/// question 3's adopted reading obtains <c>xvf_host</c> as a pinned, checksum-verified upstream
/// artifact rather than a clone, and the catalog says outright that "if it does not, this resource
/// disappears". Two resources here are <i>not</i> in the catalog: <c>agent.device-name</c>, the
/// display name the Fleet Manager assigns at adoption, which the catalog's cross-guide section
/// never enumerated; and <c>kiosk.config.albums</c>, which scopes what the slideshow selects from
/// and which neither guide 9 nor the catalog ever had — a gap the frame proved by finding no
/// photographs at all. <b>So the shipped count is 81</b>, and the arithmetic is 79 catalog entries
/// plus those two.
/// </para>
/// <para>
/// <b>The two totals are read, never typed.</b> 80 is what <c>CatalogDocument.Parse</c> counts in
/// the catalog file and what <c>ParityHarnessTests</c> asserts; 81 is
/// <c>graph.Count</c> in <c>AgentResourceGraphTests</c>, and the harness's progress ledger reads
/// both back out of those two files rather than carrying its own copy. The graph exceeding the
/// catalog is legitimate and is the reason the ledger reports a <c>beyondCatalog</c> figure at all:
/// it is the <i>net</i> — two resources the catalog does not carry, less the one it carries and
/// this does not — and not a count of either set.
/// </para>
/// <para>
/// <b>Declaration order is the tie-break</b> in <see cref="ResourceGraph"/>, so the order below
/// is the order a bare frame converges in, and it follows the catalog's own proposed ordering
/// wherever the two can agree. One place it deliberately does not.
/// </para>
/// <para>
/// It is <c>journal.storage-persistent</c>, which the catalog schedules 28th and which runs here
/// as early as it can instead. A volatile journal is what made the August 2026 failure chain
/// invisible for days, and everything below it is worth having a record of.
/// </para>
/// <para>
/// <b>The display used to be the second, and is not one any more.</b> §5.5 would schedule a
/// <c>/boot/firmware</c> write last, and this code put the two display resources at positions 2–3
/// against that — because a frame that provisions with a dark panel has no honesty mechanism at
/// all, measured on the mule 2026-08-15, where a stock image has no framebuffer and every console
/// write succeeds invisibly. The catalog has since adopted the same carve-out as Exception 1 of
/// its ordering table (decision 46), which schedules
/// <c>boot.config.dtoverlay-waveshare-panel</c> <b>3rd of 80</b> and
/// <c>boot.cmdline.fbcon-rotate</c> 2nd, ahead of <c>agent.keypair</c> and <c>agent.adoption</c>.
/// So the two now agree and the deviation is against §5.5 alone, whose other three mitigations pay
/// for it (<see cref="BootPartitionGuard"/>). The two display resources depend on nothing else,
/// which is the point: lighting the panel needs no package, no session and no adoption, and a
/// pending frame has to be able to show its own fingerprint (§3.3).
/// </para>
/// <para>
/// <b>The three agent roots declare no edges, and that is not an oversight.</b> The catalog gives
/// <c>agent.version</c> nothing, <c>agent.keypair</c> <c>agent.version</c>, and
/// <c>agent.adoption</c> <c>agent.keypair</c> — which under the catalog's own convention, where
/// <c>—</c> <i>means</i> "agent.version and nothing else", is the same statement an empty
/// <see cref="IResource.DependsOn"/> makes. Materialising them would be actively wrong: a frame
/// whose Fleet Manager is unreachable cannot evaluate <c>agent.version</c>, so an edge on it would
/// mark all eighty other resources <see cref="ResourceStatusKind.Blocked"/> and the frame
/// would provision nothing — the exact opposite of §1.2.2's "a frame must provision and self-heal
/// with the server unreachable". Declaration order gives the roots their positions; the DAG gives
/// them no veto.
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
            // Position 1, and first for the reason §2.8 gives: the applied version is the root of
            // the DAG. First in *declaration order* only — no resource declares an edge on it,
            // because the catalog's `—` already means "agent.version and nothing else" and a
            // materialised edge would report every other resource Blocked on a frame whose Fleet
            // Manager is unreachable, which is the opposite of §1.2.2.
            new AgentVersionResource(context.RunningVersion, context.ServedVersion, context.ConvergeVersion),

            // Positions 2–3, so that everything after them can be watched happening.
            new DisplayPanelOverlayResource(context.Files, guard, context.Display, context.Log),
            new ConsoleRotationResource(context.Files, guard, context.Log),

            // Position 4. The identity everything the Fleet Manager knows about this frame hangs
            // off, and the one resource whose failure a person has to resolve rather than the
            // agent — a regenerated keypair is a new device wearing the old frame's name (§3.3).
            new AgentKeypairResource(context.Store, context.Files, context.DeviceId),

            // Ahead of its catalog slot, so that if anything below goes wrong there is a record of
            // it. A volatile journal is what made the August 2026 failures invisible for days.
            new JournalStorageResource(context.Files, context.Values),

            // Position 5, the root of everything the Fleet Manager supplies a value for
            // (decision 34).
            new AdoptionResource(context.Store, context.FleetAnswer),
            new DeviceNameResource(context.Store, context.DesiredDeviceName),

            // Positions 6–22 of the catalog's own ordering: the package block, after the agent
            // roots and ahead of system configuration. None of these depends on adoption, so a
            // pending frame still builds its kiosk stack — which is what §2.7's browser stage
            // needs in order to render the repair screen the pending frame is showing.
            .. PackageCatalog.Build(new AptPackages(context.Processes)),

            // Positions 23–37, the system-configuration phase. `swap.zram-active` is a guide 5
            // resource that the catalog's ordering places here rather than with the session, and
            // `boot.autologin.getty-tty1` has to precede everything user-scoped: the whole
            // user-unit layer hangs off that one drop-in, because there is no `enable-linger`
            // anywhere in this build.

            // Positions 23–24, the head of the phase. Both are `locale.*` values the Fleet Manager
            // owns and neither has a catalog default — a time zone and a keyboard belong to the
            // room the frame stands in — so both declare adoption and both leave the frame alone
            // until a value arrives.
            new TimeZoneResource(context.Files, context.Processes, context.Values),
            new LocaleResource(context.Files, context.Processes, context.Values),

            new SwapZramResource(context.Processes, context.SystemControl),

            // Position 30, and the negative half of the resource above it. Separate because "there
            // is no swap" and "swap is eating the card" are different diagnoses with different
            // fixes; dependent because asserting that nothing swaps to the card is only meaningful
            // once something else is providing the swap.
            new NoFileSwapResource(context.Processes, context.SystemControl),

            // Position 25, and ahead of the autologin drop-in on purpose: a supplementary group
            // only reaches a process through a *new login session*, so the membership has to be
            // right before the session that will carry it is created.
            new UserGroupsResource(context.Processes, context.Session),

            new ConsoleAutologinResource(
                context.Files,
                context.SystemControl,
                context.Processes,
                context.Session),

            // Position 27. Ahead of the session and the browser, because Chromium's whole working
            // profile lives under /tmp and a frame that mounts it at the guide's 100 MB fallback
            // is a frame the browser fails on in ways nothing explains.
            new TmpfsMountResource(context.Files, context.Processes, context.SystemControl),

            // Positions 31–32. Both hang off `pkg.unattended-upgrades`, which the package block
            // already installed: the machinery, then the switch that turns it on, then the policy
            // that says what it is allowed to install.
            new AptAutoUpgradesResource(context.Files, context.Processes, context.Values),
            new UnattendedUpgradesPolicyResource(context.Files, context.Processes, context.Values),

            new HostnameResource(context.Files, context.Processes, context.Values, context.FallbackHostname),

            // Guide 4 step 1, which the catalog schedules here rather than with the rest of the
            // audio block: it is a modprobe option, it takes effect only when the module loads,
            // and everything downstream — every `amixer -c 0`, `alsactl store`, the app's capture
            // device — is written against the array being card 0.
            new SndUsbAudioIndexResource(context.Files),

            // The three-level chain that makes Blocked(dependency) and the escalation ladder
            // reachable against real system state: the unit file, its enablement, and the
            // governor value the unit is supposed to produce at boot.
            new CpuGovernorUnitResource(context.Files, context.SystemControl),
            new CpuGovernorUnitEnabledResource(context.SystemControl),
            new CpuGovernorResource(context.Files, context.Values),

            // Positions 38–47: the session and kiosk stack, front-loaded per §2.7 so that the
            // browser stage exists as early as the DAG allows. Everything from here to the
            // Chromium unit lives in the login user's session.
            new BashProfileLabwcResource(context.Files, context.Processes, context.Session),
            .. KioskStack(context),

            // Positions 54–61: the array firmware and then the audio state it validates. After
            // the session, deliberately — WirePlumber is the mixer's second owner and applies its
            // own stored device volume once the session starts, so a reading taken before that is
            // a reading of a value something else may still change (see SessionAudio).
            .. AudioCatalog.Build(context),

            // Position 76, the last of the product layer. Guide 11 keeps only two resources and
            // this is the second of them; the daemon that used to hold this line is inside the
            // agent now (see ButtonWatch), so what is left on the device is the claim itself.
            new GpioButtonLineResource(context.Processes, context.Values, context.Button),

            // Position 77, first of §5.5's last phase. Guide 6's, and it stays here rather than
            // moving up beside the rest of the camera chain: the display group is the only
            // carve-out from "brick-capable last", and a camera that appears twenty minutes later
            // costs nothing, where a dark panel costs §2.7's whole honesty mechanism.
            new CameraAutoDetectResource(context.Files, guard, context.Session, context.Log),

            // Position 78, in §5.5's last phase with the other brick-capable boot-partition
            // writes. It is guide 4's, and it is here rather than beside its siblings because a
            // `/boot/firmware` write is scheduled by risk rather than by subject.
            new HdmiAudioOffResource(context.Files, guard, context.Log),

            // Position 79. The second writer of `cmdline.txt`'s single line — the first is the
            // console rotation, seventy-five positions earlier — so it goes through the same
            // line-aware editor and reads the file at Act time rather than re-serialising from
            // anything older.
            new WifiRegulatoryDomainResource(context.Files, guard, context.Processes, context.Values, context.Log),

            // Position 80, and last of everything by recovery cost: a bad EEPROM write is the one
            // change on this frame that no software can put back.
            new EepromConfigResource(context.Processes, context.Files, context.Store, guard, context.Log),
        ];
    }

    /// <summary>
    /// The session and kiosk stack, in catalog order.
    /// </summary>
    /// <remarks>
    /// Extracted so the two resources that need the same <see cref="LabwcAutostartResource"/>
    /// instance — its mode bit and the transform it declares — get it, rather than each holding a
    /// second copy of a resource whose desired value is a fleet setting that can move underneath
    /// them.
    /// </remarks>
    private static IReadOnlyList<IResource> KioskStack(DeviceCatalogContext context)
    {
        var autostart = new LabwcAutostartResource(context.Files, context.Session, context.Values);
        var kioskUnit = new ChromiumKioskUnitResource(context.Files, context.Session);

        return
        [
            autostart,
            new LabwcAutostartExecutableResource(context.Files, autostart),
            new LabwcTouchMapResource(context.Files, context.Session),
            new DisplayTransformResource(context.Session, autostart),

            // Position 43. A guide 6 resource that the catalog schedules with the session rather
            // than with the camera chain, because it is a user-unit drop-in like everything else
            // here and because the interface it unlocks is what the chain below is for. The kiosk
            // workstream left it for the camera block deliberately.
            new PortalDesktopDropInResource(context.Files, context.Session),

            // §2.1: the app is inside the binary and the agent serves it. Ahead of the browser
            // unit because that unit's readiness guard polls this origin before Chromium opens.
            new LocalOriginResource(context.Origin),

            kioskUnit,
            new ChromiumKioskEnabledResource(context.Session),
            new ChromiumKioskRunningResource(context.Files, context.Session, kioskUnit),

            // Positions 48–53, the camera chain. It sits after the browser and before the product
            // layer, and its own order is the catalog's: switch WirePlumber's camera hunting off
            // first, then create the one node, then the portal that hands it to Chromium, and only
            // then assert that the node is actually there — which is the assertion that exists
            // because the unit reports `active` while the camera is dead.
            .. CameraChain(context),

            // Positions 62–69, the head of the product layer: guide 9's whole block. This is where
            // Docker leaves the frame — the Engine, the Compose plugin, containerd, the docker0
            // bridge and docker-selfheal existed to keep one process running, and the agent is that
            // process's parent instead. Ahead of app.config.* because app.config.immich-kiosk-url
            // names the address kiosk.listen-address publishes.
            .. KioskBlock(context),

            // The five values guide 10's config.json used to hold, now issued by the Fleet Manager
            // and recorded by the agent. Blocked behind adoption, because §3.3 gives a pending
            // device nothing.
            .. AppConfigCatalog.Build(context.Store, context.Values, context.Channel, context.Clock),
        ];
    }

    /// <summary>
    /// Guide 9's eight resources, with the child and the installer they share.
    /// </summary>
    /// <remarks>
    /// Extracted because seven of the eight need the same <see cref="KioskProcess"/> — the paths it
    /// owns, and the pid it is the only holder of — and a second instance would be a second child
    /// nobody is supervising.
    /// </remarks>
    private static IReadOnlyList<IResource> KioskBlock(DeviceCatalogContext context)
    {
        var kiosk = context.Kiosk ?? new KioskProcess(new KioskProcessServices
        {
            Store = context.Store,
            Clock = context.Clock,
            Log = context.Log,
            Settings = () => KioskCatalog.SettingsFrom(
                context.Store,
                Path.Combine(context.Store.Root, KioskProcess.DirectoryName)),
        });

        var installer = new KioskInstaller(
            kiosk.BinaryPath,
            context.KioskDownload ?? UnreachableKioskDownload.Instance,
            context.Permissions,
            context.Log);

        return KioskCatalog.Build(context with { Kiosk = kiosk }, installer);
    }

    /// <summary>
    /// The camera chain, in catalog order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Six of guide 6's sixteen resources. The other ten are elsewhere by the catalog's own
    /// scheduling: seven are apt packages in the package block (six present, one asserted
    /// <i>absent</i>), <c>unit.xdg-desktop-portal.dropin-desktop</c> sits with the session,
    /// <c>unit.chromium-kiosk.running-matches-content</c> with the browser, and
    /// <c>boot.config.camera-auto-detect</c> in the last phase with the other boot-partition
    /// writes.
    /// </para>
    /// <para>
    /// The permission store comes before the interface check for a practical reason: observing the
    /// interface D-Bus-activates the portal, and a portal that starts while the permission is still
    /// unset is a portal that will pop a dialog at the first call.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<IResource> CameraChain(DeviceCatalogContext context) =>
    [
        new WirePlumberCameraMonitorsResource(context.Files, context.Session),
        new CameraUnitResource(context.Files, context.Session),
        new CameraUnitEnabledResource(context.Session),
        new PortalCameraPermissionResource(context.Session),
        new PortalCameraInterfaceResource(context.Session),
        new CameraNodeResource(context.Session),
    ];

    /// <summary>Builds the set and validates its ordering (§2.2).</summary>
    public static ResourceGraph BuildGraph(DeviceCatalogContext context) => new(Build(context));
}
