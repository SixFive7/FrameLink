using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Systemd;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>unit.fl-agent.content</c> — the unit that starts the reconciler, reconciled.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every systemd unit this product installs was reconciled except the one that starts the
/// reconciler.</b> The catalog already carries <c>unit.chromium-kiosk.*</c>,
/// <c>unit.cpu-performance.*</c>, <c>unit.framelink-camera.*</c> and the portal drop-in;
/// <c>fl-agent.service</c> was written once by <c>fl-agent install</c> — or by
/// <c>fl.py deploy</c> — and never looked at again. An agent update replaces the binary and never
/// the unit, so a unit written by an old installer survives for the life of the SD card, and the
/// four settings that file documents at length because they are easy to get silently wrong
/// (<c>StartLimitIntervalSec</c> under <c>[Unit]</c>, <c>TTYPath=/dev/tty8</c>, <c>User=root</c>,
/// <c>KillMode</c> left at its default) had nothing on a running frame checking any of them. It is
/// <c>reference/outside-the-dag-review.md</c> item 1, which put it at the top of its list, and the
/// operator approved bringing it in.
/// </para>
/// <para>
/// <b>The circularity is real, and it decides what this resource is for.</b> These three resources
/// are reconciled by the agent that this unit starts. A frame whose unit is broken cannot repair
/// it after the next boot, because after the next boot there is no agent: no reconcile pass, no
/// repair screen, no telemetry, and nothing left to report that anything is wrong. So the whole
/// value is in repairing it <i>now</i>, while the agent is still running — and the resource must
/// not pretend otherwise. That is why <see cref="WhyItMatters"/> says the repair is possible only
/// while the program is still running rather than describing a unit file, and why the Act writes
/// and reloads but never restarts: a restart would take down the one process that can still fix
/// this, in the middle of the pass that is fixing it.
/// </para>
/// <para>
/// <b>It is not circular in the way that would make it unsafe.</b> The agent does not need the
/// unit to be correct in order to run — it only needed it to be correct at the last boot. So the
/// file can be rewritten from under a running agent with no effect on that agent at all, and
/// §2.4's reboot is what turns the new file into the service. Reload first, reboot second: a
/// <c>daemon-reload</c> after the write is what makes systemd's loaded copy the file's current
/// content, and <c>unit.fl-agent.running-matches-content</c> is the resource that reads whether it
/// did.
/// </para>
/// <para>
/// <b>The bytes come from <see cref="UnitInstaller"/>, and so does the write.</b> The unit text is
/// an embedded resource (§2.1: "no supplemental program files, ever") and
/// <see cref="UnitInstaller.WriteUnitAsync"/> is the stage-flush-rename that keeps a power cut from
/// leaving half a unit behind. Sharing both with <c>fl-agent install</c> is deliberate: the verb
/// that first installs the unit and the resource that repairs it produce identical bytes by
/// construction, so a frame provisioned either way runs the same service. That is the defect the
/// two committed copies of this file already had once, and it is not worth having a third path to
/// it.
/// </para>
/// <para>
/// <b>The write goes through <see cref="ISystemFiles.Resolve"/> rather than
/// <see cref="ISystemFiles.WriteText"/></b>, which is the same seam <c>XvfHostInstaller</c> uses
/// for the same reason: <c>WriteText</c> is <c>File.WriteAllText</c> — truncate, then write — and
/// this is the one file on the frame where the window between those two costs the agent. Resolving
/// the path keeps the logical <c>/etc/systemd/system/fl-agent.service</c> meaning the same place on
/// a frame and under a test root.
/// </para>
/// <para>
/// <b>No dependency, deliberately.</b> Nothing has to be true before the agent's own unit can be
/// written, and an edge would be actively harmful in the direction that matters: a dependent of a
/// resource that is not in sync is <see cref="ResourceStatusKind.Blocked"/>, so any edge here would
/// let some other fault hide the one fault that ends the frame.
/// </para>
/// </remarks>
public sealed class AgentUnitResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.fl-agent.content";

    /// <summary>
    /// The unit's name, as systemd knows it.
    /// </summary>
    /// <remarks>
    /// The same string as the embedded resource's logical name, because the unit travels inside
    /// the binary under its own filename. Aliased rather than retyped so there is one of it.
    /// </remarks>
    public const string UnitName = UnitInstaller.ResourceName;

    /// <summary>Where systemd reads operator-installed units.</summary>
    public const string UnitPath = UnitInstaller.DefaultUnitPath;

    private readonly ISystemFiles _files;
    private readonly ISystemControl _systemControl;

    /// <summary>Creates the resource.</summary>
    public AgentUnitResource(ISystemFiles files, ISystemControl systemControl)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(systemControl);

        _files = files;
        _systemControl = systemControl;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "The instruction that starts the program looking after this frame is missing or out of date.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "That program is what repairs everything else here, and it can only put this right while it is still running.";

    /// <summary>The unit text this build ships, which is the desired value.</summary>
    public static string DesiredContent() => UnitInstaller.ReadUnit();

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desired = DesiredContent();
        var actual = _files.ReadText(UnitPath);

        var matches = string.Equals(
            JournalStorageResource.ShortHash(actual ?? string.Empty),
            JournalStorageResource.ShortHash(desired),
            StringComparison.Ordinal);

        return ValueTask.FromResult(new ResourceObservation(
            matches,
            $"{UnitPath} {JournalStorageResource.ShortHash(desired)}",
            actual is null ? $"{UnitPath} absent" : $"{UnitPath} {JournalStorageResource.ShortHash(actual)}"));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        // Read once. The bytes that reach the card and the hash that reaches the change record are
        // then the same read by construction, rather than two that happen to agree.
        var desired = DesiredContent();

        await UnitInstaller
            .WriteUnitAsync(_files.Resolve(UnitPath), desired, cancellationToken)
            .ConfigureAwait(false);

        // systemd will not notice a rewritten unit file on its own, and both resources that depend
        // on this one read a systemd that has read it. Never a restart: `systemctl restart
        // fl-agent.service` would kill the process running this pass, which is the one process
        // that can still repair this frame. §2.4's reboot is what puts the new unit into service,
        // and the journal write that precedes it is what survives the gap.
        var reloaded = await _systemControl.RunAsync(["daemon-reload"], cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            $"write {UnitPath} ({JournalStorageResource.ShortHash(desired)}) and run systemctl daemon-reload"
                + (reloaded.Succeeded ? string.Empty : $" (refused: {reloaded.Output})"),
            "Writing down how this frame starts the program that looks after it — which only that program, "
                + "while it is still running, is able to do.");
    }
}

/// <summary>
/// <c>unit.fl-agent.enabled</c> — the frame will start the agent again.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one with the silent failure, and it is the one the review did not propose.</b>
/// <c>reference/outside-the-dag-review.md</c> item 1 asked for two resources, the content and the
/// running-versus-content pair; the operator added this one. A disabled
/// <c>fl-agent.service</c> behaves perfectly: the agent is running, every pass is green, telemetry
/// flows, the screen is correct. Nothing is observably wrong until the next boot, after which
/// nothing comes back — and because what did not come back is the reporter, nothing is left to say
/// so. The frame simply stops, mid-sentence, and the fleet sees a device that went quiet.
/// </para>
/// <para>
/// <b><c>enabled-runtime</c> is rejected, for the reason
/// <see cref="AptDailyTimersResource"/> gives.</b> It is an enablement written under <c>/run</c>,
/// which is a tmpfs: the unit reads as enabled to anything asking for a boolean and the want is
/// gone at the next boot. Here that is not merely the wrong answer, it is the exact fault wearing
/// the costume of the fix — a frame that would report this resource green and then never come
/// back. So the comparison is against <see cref="SystemdUnits.EnabledState"/> exactly.
/// </para>
/// <para>
/// <b>A mask is somebody's decision and this resource does not overrule it.</b>
/// <c>systemctl enable</c> refuses outright against a masked unit, so an Act that tried anyway
/// would spend three attempts and three reboots to reach an escalation whose delta said only that
/// the unit is not enabled. It still walks the ladder to a person — a frame that will not start
/// its own agent is a fault however deliberately it was caused — but the sentence that reaches
/// them names the cause and the <c>systemctl unmask</c> that fixes it.
/// </para>
/// <para>
/// Separate from the content per §2.2's granularity rule: a unit can be byte-perfect and not
/// enabled, which is a different diagnosis with a different command.
/// </para>
/// </remarks>
public sealed class AgentUnitEnabledResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.fl-agent.enabled";

    private readonly ISystemControl _systemControl;

    /// <summary>Creates the resource.</summary>
    public AgentUnitEnabledResource(ISystemControl systemControl)
    {
        ArgumentNullException.ThrowIfNull(systemControl);
        _systemControl = systemControl;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [AgentUnitResource.ResourceName];

    /// <inheritdoc/>
    public string Detected =>
        "The program looking after this frame is running, but the frame is not set to start it again.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "It would not come back after the next restart, and nothing would be left to say so.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var enablement = await SystemdUnits
            .AnswerAsync(_systemControl, "is-enabled", AgentUnitResource.UnitName, cancellationToken)
            .ConfigureAwait(false);

        return new ResourceObservation(
            string.Equals(enablement, SystemdUnits.EnabledState, StringComparison.Ordinal),
            SystemdUnits.EnabledState,
            enablement + Note(enablement));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var enablement = await SystemdUnits
            .AnswerAsync(_systemControl, "is-enabled", AgentUnitResource.UnitName, cancellationToken)
            .ConfigureAwait(false);

        if (SystemdUnits.IsMasked(enablement))
        {
            return new ResourceAction(
                $"{AgentUnitResource.UnitName} left {enablement}: reversing a mask is the decision of whoever "
                    + "made it, not this agent's",
                "Somebody with access to this frame switched off the program that looks after it, and only a "
                    + "person can put that back.");
        }

        // A bare `enable` and never `enable --now`. The unit is already running — this agent is it
        // — so `--now` would ask systemd to start a service it is inside, which is at best a no-op
        // and at worst a restart of the process taking this action.
        var result = await _systemControl
            .RunAsync(["enable", AgentUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"systemctl enable {AgentUnitResource.UnitName}"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Output})"),
            "Setting this frame to start the program that looks after it every time the frame comes on.");
    }

    /// <summary>What an unhealthy answer needs said about it, or nothing.</summary>
    private static string Note(string enablement)
    {
        if (SystemdUnits.IsMasked(enablement))
        {
            return $" — a deliberate override that 'systemctl enable' cannot undo; only "
                + $"'systemctl unmask {AgentUnitResource.UnitName}' run by a person puts it back";
        }

        return string.Equals(enablement, SystemdUnits.RuntimeEnabledState, StringComparison.Ordinal)
            ? " — an enablement written under /run, which is a tmpfs, so it reads as enabled right now and is "
                + "gone at the next boot"
            : string.Empty;
    }
}

/// <summary>
/// <c>unit.fl-agent.running-matches-content</c> — the service systemd is running is the one the
/// file describes.
/// </summary>
/// <remarks>
/// <para>
/// The third of the trio, mirroring <see cref="ChromiumKioskRunningResource"/>: a unit file can be
/// byte-perfect on disk while the thing actually running was made from something else. For the
/// browser that is a stale process carrying the previous command line. For the agent it is three
/// separate faults, and each is asked of systemd rather than inferred.
/// </para>
/// <para>
/// <b><c>FragmentPath</c> — systemd loaded the file this catalog writes, and not a shadow of
/// it.</b> This is the same failure as the journald drop-in that a later file overrides, in the
/// unit search path instead of a merge directory: <c>/run/systemd/system/fl-agent.service</c>
/// outranks <c>/etc/systemd/system/fl-agent.service</c>, so a frame can carry a perfect
/// <c>/etc</c> copy — <c>unit.fl-agent.content</c> green, for ever — while systemd runs something
/// else entirely. And because <c>/run</c> is a tmpfs the shadow disappears at the next boot, which
/// makes it precisely the kind of state that is impossible to reason about after the fact. Only
/// systemd can say which file it actually loaded, so it is asked.
/// </para>
/// <para>
/// <b><c>NeedDaemonReload</c> — the copy systemd holds is the file's current content.</b> systemd
/// parses a unit once and keeps it; editing the file afterwards changes nothing until a
/// <c>daemon-reload</c>. This is the property that says the two have diverged, computed by systemd
/// from its own load time rather than by this code from a hash, and it is the one thing here the
/// Act can actually converge.
/// </para>
/// <para>
/// <b><c>MainPID</c> — the process systemd runs for this unit is this agent.</b> The strongest
/// identification available, and available only to this resource: the agent is the unit's main
/// process, so it can compare systemd's answer against its own process id and need not guess from
/// a path or a command line. That matters because the browser's equivalent check was wrong for
/// exactly that reason — <c>/usr/bin/chromium</c> is a wrapper script that <c>exec</c>s a
/// different binary, so a check for the declared path matched nothing and restarted a healthy
/// browser five times a boot. There is no wrapper here and no path to match: <c>Type=exec</c> with
/// a single Native AOT binary means systemd's <c>MainPID</c> is this process, or something is
/// genuinely wrong. What it catches is the frame where the unit is broken and somebody has started
/// the agent by hand to keep it going — which is the state in which repairing the unit is most
/// urgent and least likely to be noticed.
/// </para>
/// <para>
/// <b>One <c>systemctl show</c>, not four.</b> Asking separately would put gaps between readings
/// of a state that moves, which is the race <see cref="ChromiumKioskRunningResource"/> had to
/// close after it restarted a browser that was seconds from drawing.
/// </para>
/// <para>
/// <b>The Act is a <c>daemon-reload</c> and nothing else.</b> It converges the
/// <c>NeedDaemonReload</c> half outright. It cannot converge the other two, and must not try: a
/// <c>systemctl restart</c> would end the process performing the repair, and removing a
/// <c>/run</c> shadow somebody put there is a decision that belongs to whoever put it there. Those
/// two escalate with the file and the process ids named, which is what a person needs in order to
/// act. The honest limit, stated plainly because a resource about the agent's own survival cannot
/// be coy about it: everything here works only while the agent is running. Once a frame has
/// rebooted into a broken unit, none of these three can help it, and nothing else on the frame
/// will.
/// </para>
/// </remarks>
public sealed class AgentUnitRunningResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.fl-agent.running-matches-content";

    /// <summary>The unit property carrying the pid of the process systemd started.</summary>
    public const string MainPidProperty = "MainPID";

    /// <summary>The unit property carrying the coarse lifecycle state.</summary>
    public const string ActiveStateProperty = "ActiveState";

    /// <summary>The unit property carrying the fine-grained lifecycle state.</summary>
    public const string SubStateProperty = "SubState";

    /// <summary>The unit property that says the file has changed since systemd read it.</summary>
    public const string NeedDaemonReloadProperty = "NeedDaemonReload";

    /// <summary>The unit property naming the file systemd actually loaded.</summary>
    public const string FragmentPathProperty = "FragmentPath";

    /// <summary>systemd's word for "the loaded copy is current".</summary>
    public const string NoReloadNeeded = "no";

    private readonly ISystemControl _systemControl;
    private readonly Func<int> _processId;

    /// <summary>Creates the resource.</summary>
    /// <param name="systemControl">The window onto systemd.</param>
    /// <param name="processId">
    /// This agent's own process id. A seam rather than a direct
    /// <see cref="Environment.ProcessId"/> read so a test can put the resource on either side of
    /// the comparison; the default is the real thing, which is the only correct value on a frame.
    /// </param>
    public AgentUnitRunningResource(ISystemControl systemControl, Func<int>? processId = null)
    {
        ArgumentNullException.ThrowIfNull(systemControl);

        _systemControl = systemControl;
        _processId = processId ?? (static () => Environment.ProcessId);
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
        [AgentUnitResource.ResourceName, AgentUnitEnabledResource.ResourceName];

    /// <inheritdoc/>
    public string Detected =>
        "The program looking after this frame is not running the way the written instruction describes.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "What the frame would start next time is not what is running now, and only what is running now can put it right.";

    /// <summary>What this resource is asserting, in the form a delta prints.</summary>
    public static string DesiredState() =>
        $"systemd running {AgentUnitResource.UnitPath} as loaded, with this agent as its main process";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var shown = await _systemControl
            .RunAsync(
                [
                    "show",
                    AgentUnitResource.UnitName,
                    "-p",
                    MainPidProperty,
                    "-p",
                    ActiveStateProperty,
                    "-p",
                    SubStateProperty,
                    "-p",
                    NeedDaemonReloadProperty,
                    "-p",
                    FragmentPathProperty,
                ],
                cancellationToken)
            .ConfigureAwait(false);

        var fragment = SystemdUnits.PropertyIn(shown.Output, FragmentPathProperty);
        var reload = SystemdUnits.PropertyIn(shown.Output, NeedDaemonReloadProperty);
        var pid = PidIn(shown.Output);

        if (fragment is null && reload is null && pid is null)
        {
            // Not "the unit is wrong" — nothing was asked successfully. The two have to stay apart,
            // or a systemd that would not answer reads as a broken unit and the Act reloads a
            // manager that was never observed.
            return new ResourceObservation(
                false,
                DesiredState(),
                $"systemd said nothing about {AgentUnitResource.UnitName}{Refusal(shown)}");
        }

        var wrong = new List<string>(3);

        if (fragment is null)
        {
            wrong.Add($"systemd has no unit file loaded for {AgentUnitResource.UnitName}");
        }
        else if (!string.Equals(fragment, AgentUnitResource.UnitPath, StringComparison.Ordinal))
        {
            // Named without claiming why it won. A shadow under /run outranks /etc, and a unit left
            // in /usr/lib is what systemd falls back to when /etc has none — different causes, the
            // same fact, and the fact is what a person needs. Saying "outranks" of the second would
            // be a guess printed as a finding.
            wrong.Add(
                $"systemd loaded {fragment}, which is not {AgentUnitResource.UnitPath} — the file this build "
                + "writes and the only one it can repair");
        }

        if (!string.Equals(reload, NoReloadNeeded, StringComparison.Ordinal))
        {
            wrong.Add(
                $"{NeedDaemonReloadProperty}={reload ?? "unreported"}, so {AgentUnitResource.UnitName} has been "
                + "written since systemd read it and the service running now is the previous version of the file");
        }

        var self = _processId();

        if (pid is null)
        {
            wrong.Add($"systemd did not say which process runs {AgentUnitResource.UnitName}");
        }
        else if (pid == 0)
        {
            wrong.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"systemd is running no process for {AgentUnitResource.UnitName} "
                + $"({SystemdUnits.PropertyIn(shown.Output, ActiveStateProperty) ?? "state unreported"}, "
                + $"{SystemdUnits.PropertyIn(shown.Output, SubStateProperty) ?? "sub-state unreported"}), "
                + $"so this agent is process {self} and nothing systemd owns"));
        }
        else if (pid.Value != self)
        {
            wrong.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"systemd runs {AgentUnitResource.UnitName} as process {pid.Value}, and the agent reconciling "
                + $"this frame is process {self}"));
        }

        return new ResourceObservation(
            wrong.Count == 0,
            DesiredState(),
            wrong.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{fragment}, loaded, running as process {self}")
                : string.Join("; ", wrong));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var reloaded = await _systemControl.RunAsync(["daemon-reload"], cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            "systemctl daemon-reload"
                + (reloaded.Succeeded ? string.Empty : $" (refused: {reloaded.Output})"),
            "Telling this frame to re-read how the program that looks after it should be started. The restart "
                + "that follows is what puts it in force.");
    }

    /// <summary>
    /// The pid in a <c>systemctl show -p MainPID</c> answer, or null when it carried none.
    /// </summary>
    /// <remarks>
    /// <b>Zero is a real reading and must not collapse into null</b>, on
    /// <see cref="ChromiumKioskRunningResource.MainPidIn"/>'s precedent: systemd reports
    /// <c>MainPID=0</c> for a unit with no main process, which is a fact about the frame, where a
    /// systemd that could not be asked is not evidence about anything.
    /// </remarks>
    public static int? PidIn(string shown)
    {
        ArgumentNullException.ThrowIfNull(shown);

        return SystemdUnits.PropertyIn(shown, MainPidProperty) is { } value
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            && pid >= 0
            ? pid
            : null;
    }

    /// <summary>What a refusing <c>systemctl</c> said, parenthesised, or nothing.</summary>
    private static string Refusal(SystemControlResult result) =>
        result.Output.Trim() is { Length: > 0 } text
            ? $" ({text.Split('\n')[0].Trim()})"
            : string.Empty;
}
