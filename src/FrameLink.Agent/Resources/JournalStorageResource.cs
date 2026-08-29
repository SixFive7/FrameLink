using System.Security.Cryptography;
using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>journal.storage-persistent</c> — the systemd journal survives a reboot.
/// </summary>
/// <remarks>
/// <para>
/// From guide 12 step 5. This is the setting that made the August 2026 leak-and-watchdog-reset
/// chain diagnosable at all: a volatile journal meant days of failures left no evidence behind,
/// so the incident was invisible until somebody looked at the frame. <b>Losing the journal loses
/// the evidence for every other failure on this frame</b>, which is why this one resource going
/// quietly green while nothing is being written is worse than it first reads: it hides all the
/// others.
/// </para>
/// <para>
/// <b>Observed through <c>systemd-analyze cat-config</c>, never through the drop-in.</b> This
/// resource used to read the file it had itself written, plus whether
/// <see cref="JournalDirectory"/> existed, and concluded from that pair that the journal was
/// persistent. It never asked what was actually in force.
/// <c>/etc/systemd/journald.conf.d/</c> is a <i>merge</i> directory exactly as
/// <c>apt.conf.d</c> is: systemd applies <c>journald.conf</c> and then every drop-in it finds, in
/// name order, and for a scalar setting <b>the last assignment wins</b>. So a
/// <c>zz-local.conf</c> sitting beside <see cref="DropInPath"/> could set <c>Storage=volatile</c>
/// or clear the cap, and the old reading reported a perfectly converged, fully green journal
/// resource on a frame that was writing nothing to the card. That is the same fault
/// <see cref="AptAutoUpgradesResource"/> already avoids by reading <c>apt-config dump</c> rather
/// than its own file, and the fix here is the same shape.
/// </para>
/// <para>
/// <b>Why <c>cat-config</c> is the authoritative reading and not merely a better one.</b> It is
/// systemd's own resolver: it walks the same search path <c>journald.conf</c> is loaded from —
/// <c>/etc</c>, <c>/run</c>, <c>/usr/lib</c>, with a drop-in in a higher-priority directory
/// masking a same-named one below it — and prints the fragments in the order the parser applies
/// them, each under a <c># /path</c> header. Nothing else on the frame can produce that ordering
/// without re-implementing it, and a re-implementation that disagreed with systemd by one rule
/// would be a checker reporting a value nobody is running. The parity harness already probes a
/// frame with this exact command for this exact pair of settings
/// (<c>tools/FrameLink.Parity/Facets.cs</c>, facet <c>journald</c>), so the resource and the
/// parity capture now read a frame the same way.
/// </para>
/// <para>
/// <b>What it is honestly not.</b> <c>cat-config</c> resolves the <i>files</i>, so it answers
/// "what will journald apply", not "what has the running journald applied since it last started"
/// — the same limit <c>apt-config dump</c> has, and the reason §2.4's reboot is what turns this
/// Act into behaviour. Two runtime readings were considered and rejected:
/// <c>journalctl --header</c>, which names the journal files actually open but says nothing about
/// the cap and costs a walk of every archived file; and requiring a machine-id directory to have
/// appeared under <see cref="JournalDirectory"/>, which is a real confirmation that journald
/// flushed there but turns a boot-timing race into drift. Named rather than dropped.
/// </para>
/// <para>
/// <b>A masked <c>systemd-journald</c> is the second way this went green while nothing was
/// logged</b>, and it is the more complete failure of the two: the configuration can be perfect
/// and the daemon pointed at <c>/dev/null</c>. It is read the same way
/// <see cref="AptDailyTimersResource"/> reads its timers and treated the same way — named in the
/// delta with the <c>systemctl unmask</c> a person has to run, never enabled around, because
/// <c>systemctl enable</c> refuses against a mask and three attempts at it would buy three reboots
/// and an escalation whose delta said only that something was wrong. Note what is deliberately
/// <i>not</i> asserted: <c>systemd-journald.service</c> is a static unit with no <c>[Install]</c>
/// section, so <c>is-enabled</c> answers <c>static</c> on a healthy frame. Demanding
/// <c>enabled</c> here would be permanent false drift on every frame ever built.
/// </para>
/// <para>
/// <b>The <see cref="JournalDirectory"/> check stays.</b> <c>Storage=persistent</c> with the
/// directory missing silently stays volatile — the setting is right, it is not in force, and
/// nothing says so — which is why the catalog folds the directory into Observe rather than leaving
/// it to a second resource.
/// </para>
/// <para>
/// The catalog also lists <c>journalctl --disk-usage</c> under the cap as part of Observe. It is
/// not implemented here: disk usage lags a cap change by however long it takes the journal to
/// rotate, so a freshly lowered cap would report drift the agent could not act on, and every
/// reconcile pass would rewrite an already-correct file. Named rather than dropped.
/// </para>
/// </remarks>
public sealed class JournalStorageResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "journal.storage-persistent";

    /// <summary>Fleet setting carrying the cap (§3.4).</summary>
    public const string SettingKey = "logging.journalMaxUse";

    /// <summary>Guide 12's default: one to two weeks of this frame's logs.</summary>
    public const string DefaultMaxUse = "64M";

    /// <summary>Where the drop-in goes.</summary>
    public const string DropInPath = "/etc/systemd/journald.conf.d/persistent.conf";

    /// <summary>Where systemd keeps a persistent journal.</summary>
    public const string JournalDirectory = "/var/log/journal";

    /// <summary>The setting that decides whether the journal reaches the card at all.</summary>
    public const string StorageKey = "Storage";

    /// <summary>The setting that bounds what the journal is allowed to take.</summary>
    public const string MaxUseKey = "SystemMaxUse";

    /// <summary>The one <see cref="StorageKey"/> value this resource accepts.</summary>
    /// <remarks>
    /// <c>auto</c> is not accepted, and it is the near miss worth naming: it means "persistent if
    /// <see cref="JournalDirectory"/> exists", which is true right up until somebody removes the
    /// directory, at which point the journal moves back into memory with no setting having
    /// changed. The explicit word is what makes the intent legible on the frame.
    /// </remarks>
    public const string PersistentStorage = "persistent";

    /// <summary>The daemon that has to be alive for any of this to mean anything.</summary>
    public const string DaemonUnit = "systemd-journald.service";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly ISystemControl _systemControl;
    private readonly FleetValues _values;

    /// <summary>Creates the resource.</summary>
    public JournalStorageResource(
        ISystemFiles files,
        IProcessRunner processes,
        ISystemControl systemControl,
        FleetValues values)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(systemControl);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _processes = processes;
        _systemControl = systemControl;
        _values = values;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame is not keeping a record of what it does.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it, a problem that happens overnight leaves nothing behind to look at.";

    /// <summary>The cap the Fleet Manager asked for, or guide 12's.</summary>
    public string MaxUse => _values.Get(SettingKey, DefaultMaxUse);

    /// <summary>The exact file content this resource converges on.</summary>
    public string DesiredContent() =>
        "[Journal]\n"
        + StorageKey + "=" + PersistentStorage + "\n"
        + MaxUseKey + "=" + MaxUse + "\n";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var cap = MaxUse;
        var expected =
            $"{StorageKey}={PersistentStorage} and {MaxUseKey}={cap} in force, "
            + $"{JournalDirectory} present, {DaemonUnit} not masked";

        var merged = await JournaldConfig.CatAsync(_processes, cancellationToken).ConfigureAwait(false);
        var enablement = await SystemdUnits
            .AnswerAsync(_systemControl, "is-enabled", DaemonUnit, cancellationToken)
            .ConfigureAwait(false);

        var wrong = new List<string>(4);

        if (merged.Length == 0)
        {
            // Not "the settings are wrong" — nothing was read. Reporting drift is still the right
            // direction, on AptConfig's precedent: a resource that cannot see the configuration
            // escalates to a person rather than reporting a frame it could not inspect as correct.
            wrong.Add($"'{JournaldConfig.Command}' answered nothing, so what journald has in force could not be read");
        }
        else
        {
            Complain(wrong, StorageKey, JournaldConfig.Effective(merged, StorageKey), PersistentStorage);
            Complain(wrong, MaxUseKey, JournaldConfig.Effective(merged, MaxUseKey), cap);
        }

        if (!_files.DirectoryExists(JournalDirectory))
        {
            wrong.Add($"{JournalDirectory} missing, so the journal is still in memory only");
        }

        if (SystemdUnits.IsMasked(enablement))
        {
            wrong.Add(MaskNote(enablement));
        }

        return new ResourceObservation(
            wrong.Count == 0,
            expected,
            wrong.Count == 0 ? expected : string.Join("; ", wrong));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var cap = MaxUse;

        _files.WriteText(DropInPath, DesiredContent());
        _files.EnsureDirectory(JournalDirectory);

        var changes = new List<string>(2)
        {
            $"write {DropInPath} ({StorageKey}={PersistentStorage}, {MaxUseKey}={cap}) and create {JournalDirectory}",
        };

        // Read after the write rather than before it: the enablement is the half this Act cannot
        // repair, and what it says has to reach the change record whichever way the file went.
        var enablement = await SystemdUnits
            .AnswerAsync(_systemControl, "is-enabled", DaemonUnit, cancellationToken)
            .ConfigureAwait(false);
        var masked = SystemdUnits.IsMasked(enablement);

        if (masked)
        {
            changes.Add(
                $"{DaemonUnit} left {enablement}: reversing a mask is the decision of whoever made it, not this agent's");
        }

        return new ResourceAction(
            string.Join(" · ", changes),
            masked
                ? "Telling this frame to keep its own log on the memory card. Somebody with access to this frame switched the logging off altogether, and only a person can put that back."
                : "Telling this frame to keep its own log on the memory card instead of forgetting it at every restart.");
    }

    /// <summary>A short content hash, so a delta names the difference without printing a file.</summary>
    internal static string ShortHash(string content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(content))))[..12];

    /// <summary>
    /// Line-ending and trailing-whitespace normalisation before comparing.
    /// </summary>
    /// <remarks>
    /// Without it a file written on a workstation and a file written on a frame hash
    /// differently, and the resource would report permanent false drift — the same mistake the
    /// catalog flags for the Chromium command line, in a different disguise.
    /// </remarks>
    private static string Normalise(string? content) =>
        content is null
            ? string.Empty
            : content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n', ' ', '\t') + "\n";

    /// <summary>
    /// One effective setting against what it has to be, named the way an operator can act on.
    /// </summary>
    /// <remarks>
    /// <b>The file the value came from is in the sentence, and that is the whole point of the
    /// change.</b> "SystemMaxUse=1G" tells an operator that something is wrong and not where to
    /// go; "the value in force comes from /etc/systemd/journald.conf.d/zz-local.conf" tells them
    /// the file to open. The Act cannot win against that file by rewriting its own — which is why
    /// the resource has to say so rather than retry in silence.
    /// </remarks>
    private static void Complain(List<string> wrong, string key, JournaldSetting? effective, string want)
    {
        if (effective is not { } setting)
        {
            wrong.Add($"{key} is unset, so journald applies its own default rather than {want}");
            return;
        }

        if (string.Equals(setting.Value, want, StringComparison.Ordinal))
        {
            return;
        }

        // An empty value is a real assignment in systemd's parser and means "reset to the built-in
        // default", so a later drop-in carrying a bare `SystemMaxUse=` neutralises the cap without
        // ever naming a wrong number. Reported as the clearing it is rather than as an empty "=".
        var observed = setting.Value.Length == 0 ? $"{key} is cleared" : $"{key}={setting.Value}";

        wrong.Add(string.Equals(setting.Source, DropInPath, StringComparison.Ordinal)
            ? $"{observed} in {DropInPath}"
            : $"{observed} — the value in force comes from {setting.Source}, not from {DropInPath}");
    }

    /// <summary>The sentence that turns a masked daemon into something a person can act on.</summary>
    private static string MaskNote(string enablement) =>
        $"{DaemonUnit} is {enablement}, which is a deliberate override that 'systemctl enable' cannot undo; "
        + $"only 'systemctl unmask {DaemonUnit}' run by a person puts it back";
}

/// <summary>One setting as journald will apply it, and the file it came from.</summary>
/// <param name="Value">The right-hand side, trimmed, exactly as the winning file wrote it.</param>
/// <param name="Source">The file <c>cat-config</c> printed it under.</param>
/// <remarks>
/// The source travels with the value because the fault this type exists to expose is an
/// <i>override</i>, and a value without its file is a complaint the operator cannot act on. It is
/// never compared against anything — it is diagnosis, not criterion — so a source this parser gets
/// wrong costs a misleading filename in one sentence and never a wrong verdict.
/// </remarks>
public readonly record struct JournaldSetting(string Value, string Source);

/// <summary>
/// Reading journald's <i>effective</i> configuration, which is the only version worth comparing.
/// </summary>
/// <remarks>
/// <para>
/// <c>systemd-analyze cat-config systemd/journald.conf</c> is systemd's own resolver for the
/// question "which files make up this configuration, and in what order". It prints each fragment
/// under a <c># /absolute/path</c> header, in the order the parser applies them, having already
/// done the part that cannot be reimplemented safely: the <c>/etc</c> → <c>/run</c> →
/// <c>/usr/lib</c> search path, and a drop-in in a higher-priority directory masking a same-named
/// one below it. This class does the small remaining part — the last assignment inside
/// <c>[Journal]</c> wins — which is systemd's rule for a scalar setting.
/// </para>
/// <para>
/// <b>A header is recognised as <c>#</c>, a space, and an absolute path</b>, which is a heuristic
/// and is chosen so that its failure is harmless. Every line beginning <c>#</c> is a comment to
/// the settings parser either way, so a comment mistaken for a header cannot change a value; the
/// worst it can do is attribute a later assignment to the wrong file in a sentence a person reads.
/// Debian's shipped <c>journald.conf</c> carries no comment whose first character after the hash
/// and space is a slash.
/// </para>
/// <para>
/// <b>Every field is trimmed, and that is what makes a CRLF capture parse identically.</b> There
/// is no line-ending normalisation pass because there is nothing left for one to do: a carriage
/// return is trailing whitespace on the source path, on the key and on the value, and all three are
/// trimmed for systemd's own reason — it strips both sides of an <c>=</c>, so
/// <c>Storage = volatile</c> is a valid assignment and has to be read as one. A compare sensitive
/// to a carriage return would report permanent false drift, and a frame that reboots for ever over
/// one is worse than a frame that never applies the setting.
/// </para>
/// <para>
/// <b>The comment skip is deliberately inert, and is kept anyway.</b> No mutation of it changes
/// any verdict, because the comment character is part of the token before the <c>=</c>: Debian's
/// <c>#Storage=auto</c> parses as a key named <c>#Storage</c>, which matches nothing. It is stated
/// here rather than dropped because a reader who knows systemd's comment syntax would otherwise
/// have to derive that for themselves before trusting the line below it — and because a key added
/// later could make it load-bearing without anybody noticing it had been.
/// </para>
/// <para>
/// Continuation lines — a trailing backslash joining two physical lines — are not handled.
/// <c>journald.conf</c> has no list-valued setting that would use one, and neither Debian's
/// shipped file nor anything this catalog writes contains one.
/// </para>
/// </remarks>
public static class JournaldConfig
{
    /// <summary>systemd's own configuration resolver.</summary>
    public const string Executable = "systemd-analyze";

    /// <summary>The configuration to resolve, named the way <c>cat-config</c> takes it.</summary>
    public const string ConfigName = "systemd/journald.conf";

    /// <summary>The one section journald reads.</summary>
    public const string Section = "[Journal]";

    /// <summary>The argument vector, compiled rather than composed (§2.2).</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["cat-config", ConfigName];

    /// <summary>The command as a person would type it, for a delta that has to name it.</summary>
    public static string Command => Executable + " " + string.Join(' ', Arguments);

    /// <summary>Runs the resolver, or returns empty if it could not be run.</summary>
    /// <remarks>
    /// An empty answer yields no values, which the caller reads as drift. That is the conservative
    /// direction and the same one <see cref="AptConfig.DumpAsync"/> takes: a resource that cannot
    /// see the configuration escalates to a person rather than reporting a frame it could not
    /// inspect as correct.
    /// </remarks>
    public static async Task<string> CatAsync(IProcessRunner processes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processes);

        var result = await processes
            .RunAsync(Executable, Arguments, ProcessDeadline.Local, cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded ? result.StandardOutput : string.Empty;
    }

    /// <summary>The value of <paramref name="key"/> journald will apply, and where it came from.</summary>
    public static JournaldSetting? Effective(string merged, string key)
    {
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        JournaldSetting? found = null;
        var source = "a file cat-config did not name";
        var inSection = false;

        foreach (var raw in merged.Split('\n'))
        {
            if (raw.StartsWith("# /", StringComparison.Ordinal))
            {
                source = raw[2..].Trim();
                continue;
            }

            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';')
            {
                continue;
            }

            if (trimmed[0] == '[')
            {
                // A section this file does not read is skipped rather than ending the walk: a
                // drop-in may carry several, and journald reads whichever of them is [Journal].
                inSection = string.Equals(trimmed, Section, StringComparison.Ordinal);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0 || !string.Equals(trimmed[..separator].TrimEnd(), key, StringComparison.Ordinal))
            {
                continue;
            }

            // No early return. The last assignment is the one journald keeps, and returning the
            // first would report the value this agent wrote while the frame ran a later one —
            // which is the entire fault this reader exists to end.
            found = new JournaldSetting(trimmed[(separator + 1)..].Trim(), source);
        }

        return found;
    }
}
