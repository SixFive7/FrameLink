using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>boot.config.dtoverlay-waveshare-panel</c> — light the DSI panel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scheduled early by explicit decision, against §5.5's default.</b> The catalog's proposed
/// ordering puts this 76th of 79 because writing <c>config.txt</c> is brick-capable and §5.5
/// schedules brick-capable resources last. Measured on the mule 2026-08-15, that ordering has a
/// cost the catalog had already flagged and this makes concrete: on a stock image there is no
/// framebuffer at all — <c>config.txt</c> carries only <c>dtoverlay=vc4-kms-v3d</c>, both HDMI
/// connectors read <c>disconnected</c>, there is no DSI connector, <c>/dev/fb0</c> does not
/// exist and <c>/sys/class/backlight/</c> is empty — while <c>tty1</c> is nonetheless an active
/// console, so every frame the agent writes succeeds and produces no pixels. Under the default
/// ordering the panel would stay dark through nearly the whole of a one-to-two-hour provision,
/// and §2.7's narration, which is the product's primary honesty mechanism, would narrate to
/// nobody.
/// </para>
/// <para>
/// The user weighed that against the brick risk and chose §2.7. What makes the early slot
/// affordable is §5.5's other three mitigations, which are implemented here rather than
/// assumed: the content is a known-good literal, the edit is validated as
/// minimal before it is written (<see cref="BootConfigText.ValidateConfig"/>), the previous
/// <c>config.txt</c> is copied to the FAT32 boot partition where a card reader can find it, and
/// <see cref="BootPartitionGuard"/> puts it back automatically if the frame comes back twice
/// without the change taking effect.
/// </para>
/// <para>
/// <b>Its dependencies are genuinely nothing.</b> Lighting the panel needs no package, no
/// session and no adoption — a pending frame has to be able to display its own fingerprint
/// (§3.3), so gating this behind adoption would defeat the reason it was moved.
/// </para>
/// </remarks>
public sealed class DisplayPanelOverlayResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "boot.config.dtoverlay-waveshare-panel";

    /// <summary>
    /// The exact line guide 3 step 1 appends, matched to the 800×1280 panel on the
    /// heatsink-side DSI port.
    /// </summary>
    public const string OverlayLine = "dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a";

    private readonly ISystemFiles _files;
    private readonly BootPartitionGuard _guard;
    private readonly IDisplayProbe _display;
    private readonly IAgentLog _log;

    /// <summary>Creates the resource.</summary>
    /// <param name="files">The boot partition.</param>
    /// <param name="guard">Backup, validation and boot-count self-repair (§5.5).</param>
    /// <param name="display">
    /// Asked whether a picture is actually appearing. Not the in-sync predicate — see the
    /// remarks on <see cref="ObserveAsync"/> — but it is what closes the trial, so a line that
    /// is written and lights nothing gets rolled back rather than believed.
    /// </param>
    /// <param name="log">Where a refused write is recorded.</param>
    public DisplayPanelOverlayResource(
        ISystemFiles files,
        BootPartitionGuard guard,
        IDisplayProbe display,
        IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(log);

        _files = files;
        _guard = guard;
        _display = display;
        _log = log;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "The screen on this frame is not switched on yet.";

    /// <inheritdoc/>
    public string WhyItMatters => "Until it is, this frame cannot show you anything at all — including what it is doing.";

    /// <summary>Reads the config line, and asks whether a picture is actually appearing.</summary>
    /// <remarks>
    /// <para>
    /// <b>In sync means the line is present, not that the panel lit.</b> A frame whose ribbon
    /// cable is loose would otherwise never converge and would escalate forever over something
    /// no software can fix — and §2.5's ladder is for settings the agent can act on. The panel's
    /// actual visibility is carried in the observed text instead, so the delta and the telemetry
    /// both say plainly whether anything can be seen.
    /// </para>
    /// <para>
    /// It is not merely decorative, though: the boot trial is only closed when the line is
    /// present <i>and</i> a display is visible. A line that is written and lights nothing
    /// therefore stays on trial, runs out its boot budget and is rolled back automatically —
    /// which is the whole reason the boot-count mitigation exists.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation.</param>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Self-repair first, before anything reads the file (§5.5). A rollback that happened on
        // this boot must be visible to the compare that follows it.
        var verdict = _guard.Tick(BootConfigText.ConfigPath);
        var content = _files.ReadText(BootConfigText.ConfigPath);
        var present = BootConfigText.HasLine(content, OverlayLine);
        var visibility = _display.Probe();

        if (present && visibility.Visible)
        {
            _guard.Confirm(BootConfigText.ConfigPath);
        }

        if (verdict is GuardVerdict.RolledBack or GuardVerdict.Locked && !present)
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                $"{BootConfigText.ConfigPath} contains '{OverlayLine}'",
                $"the panel setting was tried and put back automatically because the frame came back without it "
                    + $"working; {BootPartitionGuard.BackupFor(BootConfigText.ConfigPath)} holds the version before the change"));
        }

        var expected = $"{BootConfigText.ConfigPath} contains '{OverlayLine}', and a display is visible";
        var observed = present
            ? $"the line is present; {visibility.Reason} [{visibility.Evidence}]"
            : $"the panel line is absent; {visibility.Reason} [{visibility.Evidence}]";

        return ValueTask.FromResult(new ResourceObservation(present, expected, observed));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var current = _files.ReadText(BootConfigText.ConfigPath);

        if (!_guard.BeginTrial(BootConfigText.ConfigPath))
        {
            // Locked by an earlier rollback. Writing anyway would retry into the same wall,
            // which is the specific thing boot-count self-repair exists to stop.
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.ConfigPath} — this change has already been rolled back once",
                "This frame already tried switching its screen on and had to undo it. It will not try again on its own."));
        }

        var updated = BootConfigText.AppendLine(current, OverlayLine);
        var check = BootConfigText.ValidateConfig(current, updated, OverlayLine);

        if (!check.Valid)
        {
            _log.Fail($"Refusing to write {BootConfigText.ConfigPath}: {check.Problem}");
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.ConfigPath} — {check.Problem}",
                "This frame checked the change it was about to make to its start-up settings, did not like it, and left them alone."));
        }

        _files.WriteText(BootConfigText.ConfigPath, updated);

        return ValueTask.FromResult(new ResourceAction(
            $"append '{OverlayLine}' to {BootConfigText.ConfigPath} "
                + $"(backed up to {BootPartitionGuard.BackupFor(BootConfigText.ConfigPath)})",
            "Telling this frame which screen is attached to it, so the picture comes on when it restarts."));
    }
}

/// <summary>
/// <c>boot.cmdline.fbcon-rotate</c> — draw the text console the right way up.
/// </summary>
/// <remarks>
/// <para>
/// The panel is built portrait and the frame hangs landscape, so without this the console is
/// rendered sideways. Guide 3 step 1 appends <c>fbcon=rotate:1</c> to the single-line kernel
/// command line, and the v1 reference confirms it is there.
/// </para>
/// <para>
/// <b>A separate resource from the overlay, and dependent on it.</b> Separate because §2.2's
/// granularity sub-rule is "one file written atomically = one resource" and these are two files
/// with two different failure modes — a missing overlay is a dark panel, a missing rotation is a
/// sideways one. Dependent because rotating a console nobody can see is not a repair anyone can
/// judge, and because sequencing the two writes means the frame never has two edits to the same
/// partition in flight at once.
/// </para>
/// <para>
/// A sideways console after the first reboot is a strictly better state than a dark one, which
/// is what makes splitting them affordable under the early-scheduling decision.
/// </para>
/// </remarks>
public sealed class ConsoleRotationResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "boot.cmdline.fbcon-rotate";

    /// <summary>The parameter guide 3 step 1 appends.</summary>
    public const string RotateToken = "fbcon=rotate:1";

    /// <summary>Prefix used to find an existing rotation, whatever its value.</summary>
    public const string RotatePrefix = "fbcon=rotate:";

    /// <summary>What the running kernel was actually given.</summary>
    public const string ProcCmdlinePath = "/proc/cmdline";

    private readonly ISystemFiles _files;
    private readonly BootPartitionGuard _guard;
    private readonly IAgentLog _log;

    /// <summary>Creates the resource.</summary>
    public ConsoleRotationResource(ISystemFiles files, BootPartitionGuard guard, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(log);

        _files = files;
        _guard = guard;
        _log = log;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [DisplayPanelOverlayResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "The writing on this frame's screen is sideways.";

    /// <inheritdoc/>
    public string WhyItMatters => "The screen is built upright and this frame hangs on its side, so the text has to be turned round.";

    /// <summary>Reads the file the firmware will use, and the command line the kernel got.</summary>
    /// <remarks>
    /// Both, because they can disagree and the disagreement is the whole point: the file is what
    /// was written and <c>/proc/cmdline</c> is what actually took effect, so comparing only the
    /// file would be the write-only check §2.4 refuses. This is also the catalog's own Observe
    /// for the other <c>cmdline.txt</c> resource.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation.</param>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var verdict = _guard.Tick(BootConfigText.CmdlinePath);
        var onDisk = BootConfigText.FindToken(_files.ReadText(BootConfigText.CmdlinePath), RotatePrefix);
        var inForce = BootConfigText.FindToken(_files.ReadText(ProcCmdlinePath), RotatePrefix);

        var correct = string.Equals(onDisk, RotateToken, StringComparison.Ordinal)
            && string.Equals(inForce, RotateToken, StringComparison.Ordinal);

        if (correct)
        {
            _guard.Confirm(BootConfigText.CmdlinePath);
        }

        if (verdict is GuardVerdict.RolledBack or GuardVerdict.Locked && onDisk is null)
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                RotateToken,
                $"the rotation was tried and put back automatically; "
                    + $"{BootPartitionGuard.BackupFor(BootConfigText.CmdlinePath)} holds the version before the change"));
        }

        return ValueTask.FromResult(new ResourceObservation(
            correct,
            $"{RotateToken} in {BootConfigText.CmdlinePath} and in {ProcCmdlinePath}",
            $"{BootConfigText.CmdlinePath}={onDisk ?? "absent"}, {ProcCmdlinePath}={inForce ?? "absent"}"));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var current = _files.ReadText(BootConfigText.CmdlinePath);

        if (BootConfigText.FindToken(current, RotatePrefix) is { } existing)
        {
            // Guide 3's own guard is `grep -q 'fbcon=rotate:' || sed`, which deliberately leaves
            // a different rotation alone — somebody who changed 1 to 3 because their panel was
            // upside down meant it. Reproducing that is why Observe reports the token it found
            // rather than merely "absent".
            return ValueTask.FromResult(new ResourceAction(
                $"left {existing} in {BootConfigText.CmdlinePath} alone — a rotation is already set by hand",
                "This frame already has a screen rotation set, so it has not touched it."));
        }

        if (!_guard.BeginTrial(BootConfigText.CmdlinePath))
        {
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.CmdlinePath} — this change has already been rolled back once",
                "This frame already tried turning its screen round and had to undo it. It will not try again on its own."));
        }

        var updated = BootConfigText.AppendToken(current, RotateToken);
        var check = BootConfigText.ValidateCmdline(current, updated, RotateToken);

        if (!check.Valid)
        {
            _log.Fail($"Refusing to write {BootConfigText.CmdlinePath}: {check.Problem}");
            return ValueTask.FromResult(new ResourceAction(
                $"refused to write {BootConfigText.CmdlinePath} — {check.Problem}",
                "This frame checked the change it was about to make to its start-up settings, did not like it, and left them alone."));
        }

        _files.WriteText(BootConfigText.CmdlinePath, updated);

        return ValueTask.FromResult(new ResourceAction(
            $"append '{RotateToken}' to {BootConfigText.CmdlinePath} "
                + $"(backed up to {BootPartitionGuard.BackupFor(BootConfigText.CmdlinePath)})",
            "Turning the writing on this frame's screen the right way round."));
    }
}
