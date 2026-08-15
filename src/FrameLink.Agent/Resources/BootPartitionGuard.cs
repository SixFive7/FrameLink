using System.Text.Json;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>One protected boot-partition file, mid-trial.</summary>
public sealed record BootTrial
{
    /// <summary>The file being changed.</summary>
    public required string Path { get; init; }

    /// <summary>Where its pre-change content was copied.</summary>
    public required string BackupPath { get; init; }

    /// <summary>The boot the change was made in.</summary>
    public required string StartedBootId { get; init; }

    /// <summary>When the trial opened.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>The most recent boot this trial has counted.</summary>
    public required string LastCountedBootId { get; init; }

    /// <summary>How many boots have happened since the change without it being confirmed.</summary>
    public int Boots { get; init; }

    /// <summary>How many unconfirmed boots are tolerated before the backup is restored.</summary>
    public required int Limit { get; init; }

    /// <summary>Set once the backup has been put back.</summary>
    public bool RolledBack { get; init; }
}

/// <summary>Every open trial.</summary>
public sealed record BootTrialState
{
    private static readonly IReadOnlyList<BootTrial> NoTrials = [];

    /// <summary>Trials, one per protected file.</summary>
    public IReadOnlyList<BootTrial> Trials { get; init; } = NoTrials;
}

/// <summary>What the guard did when it was asked to check itself.</summary>
public enum GuardVerdict
{
    /// <summary>No trial is open.</summary>
    Idle,

    /// <summary>A trial is open and within its boot budget.</summary>
    Trialling,

    /// <summary>The budget ran out and the backup has been put back.</summary>
    RolledBack,

    /// <summary>A rollback already happened; this file will not be written again unattended.</summary>
    Locked,
}

/// <summary>
/// §5.5's three mitigations for a brick-capable write, applied to one file.
/// </summary>
/// <remarks>
/// <para>
/// §5.5 names the residual risk plainly — "a malformed <c>config.txt</c> … can produce a device
/// nothing remote can reach" — and lists what makes it affordable: <b>validate before writing,
/// keep and restore backups, boot-count self-repair</b>, and schedule brick-capable resources
/// last. The user has overridden the last of those for the display, because §2.7's narration is
/// the product's primary honesty mechanism and a frame that provisions with a dark panel has
/// none. This class is what has to be true instead.
/// </para>
/// <para>
/// <b>Backups go on the boot partition</b>, beside the file they protect, as
/// <c>&lt;name&gt;.fl-backup</c>. That is deliberate and it is the only choice that helps: the
/// boot partition is FAT32, so a person who pulls the card and puts it in any laptop can see
/// the backup and rename it. A backup under <c>/var/lib/fl-agent</c> would sit on an ext4
/// root filesystem that Windows and macOS will not mount, which is precisely the situation
/// somebody is in when they need it.
/// </para>
/// <para>
/// <b>What boot-count self-repair does and does not cover.</b> It counts boots that happen
/// while a change is unconfirmed, and puts the backup back when the count runs out. That covers
/// a change which boots but never converges, and a boot loop the agent survives. It does
/// <i>not</i> cover a device that never reaches userspace, because nothing on the frame runs to
/// notice — §5.5 is explicit that the residual risk there is covered by a pre-flashed spare
/// card, "a swap, not a flashing session". Claiming otherwise would be the exact kind of
/// write-only optimism §2.4 exists to refuse.
/// </para>
/// </remarks>
public sealed class BootPartitionGuard
{
    /// <summary>File name of the trial ledger inside the state store.</summary>
    public const string StateFileName = "boot-trials.json";

    /// <summary>Suffix given to a backup, beside the file it protects.</summary>
    public const string BackupSuffix = ".fl-backup";

    private readonly ISystemFiles _files;
    private readonly IStateStore _store;
    private readonly IBootIdentity _boot;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;

    /// <summary>Creates a guard.</summary>
    public BootPartitionGuard(
        ISystemFiles files,
        IStateStore store,
        IBootIdentity boot,
        IAgentClock clock,
        IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(boot);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        _files = files;
        _store = store;
        _boot = boot;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// How many unconfirmed boots are tolerated before the backup is restored.
    /// </summary>
    /// <remarks>
    /// Two. One boot is the reboot the change is being proven by, and every resource takes one
    /// (§2.4); a second boot with the change still unconfirmed means the frame has come back
    /// twice and is no better, which is the point at which retrying into the same wall stops
    /// being diligence.
    /// </remarks>
    public int BootLimit { get; init; } = 2;

    /// <summary>The backup path for <paramref name="path"/>.</summary>
    public static string BackupFor(string path) => path + BackupSuffix;

    /// <summary>Whether a trial is open on <paramref name="path"/>.</summary>
    public BootTrial? TrialFor(string path) => Read().Trials
        .FirstOrDefault(trial => string.Equals(trial.Path, path, StringComparison.Ordinal));

    /// <summary>
    /// Counts a boot, and restores the backup if the budget has run out.
    /// </summary>
    /// <remarks>
    /// Called at the top of the protected resource's Observe. It writes, which an Observe
    /// normally must not — and that is the correct place for it anyway: self-repair has to
    /// happen before anything reads the file, or the pass makes a decision against a state the
    /// agent has already decided to abandon.
    /// </remarks>
    public GuardVerdict Tick(string path)
    {
        var trial = TrialFor(path);
        if (trial is null)
        {
            return GuardVerdict.Idle;
        }

        if (trial.RolledBack)
        {
            return GuardVerdict.Locked;
        }

        if (string.Equals(trial.LastCountedBootId, _boot.Current, StringComparison.Ordinal))
        {
            return GuardVerdict.Trialling;
        }

        var boots = trial.Boots + 1;
        if (boots < trial.Limit)
        {
            Save(trial with { Boots = boots, LastCountedBootId = _boot.Current });
            return GuardVerdict.Trialling;
        }

        var backup = _files.ReadText(trial.BackupPath);
        if (backup is null)
        {
            _log.Fail($"The backup {trial.BackupPath} is gone, so {path} cannot be put back. Leaving it alone.");
            Save(trial with { Boots = boots, LastCountedBootId = _boot.Current, RolledBack = true });
            return GuardVerdict.Locked;
        }

        _files.WriteText(path, backup);
        Save(trial with { Boots = boots, LastCountedBootId = _boot.Current, RolledBack = true });

        _log.Fail(
            $"{path} has been changed for {boots} boots without the change taking effect. "
            + $"Putting {trial.BackupPath} back and refusing to write it again unattended.");

        return GuardVerdict.RolledBack;
    }

    /// <summary>
    /// Backs the file up and opens a trial, unless a rollback has already locked it.
    /// </summary>
    /// <returns>False when the file is locked and must not be written.</returns>
    public bool BeginTrial(string path)
    {
        var existing = TrialFor(path);
        if (existing is { RolledBack: true })
        {
            return false;
        }

        if (existing is not null)
        {
            // A trial is already open, so the backup already holds the pre-change content.
            // Re-copying now would back up the broken version and lose the good one.
            Save(existing with { StartedBootId = _boot.Current, LastCountedBootId = _boot.Current });
            return true;
        }

        var backupPath = BackupFor(path);
        var current = _files.ReadText(path);
        if (current is not null && !_files.FileExists(backupPath))
        {
            _files.WriteText(backupPath, current);
            _log.Info($"Backed {path} up to {backupPath} before changing it.");
        }

        Save(new BootTrial
        {
            Path = path,
            BackupPath = backupPath,
            StartedBootId = _boot.Current,
            StartedUtc = _clock.UtcNow,
            LastCountedBootId = _boot.Current,
            Boots = 0,
            Limit = BootLimit,
        });

        return true;
    }

    /// <summary>Closes the trial. The backup file is deliberately kept.</summary>
    public void Confirm(string path)
    {
        var state = Read();
        if (!state.Trials.Any(trial => string.Equals(trial.Path, path, StringComparison.Ordinal)))
        {
            return;
        }

        Write(new BootTrialState
        {
            Trials = [.. state.Trials.Where(trial => !string.Equals(trial.Path, path, StringComparison.Ordinal))],
        });

        _log.Info($"{path} survived a reboot and took effect; its trial is closed.");
    }

    /// <summary>Clears a rollback lock, so an operator's retry can try once more.</summary>
    public void Unlock(string path) => Confirm(path);

    private BootTrialState Read()
    {
        var text = _store.ReadText(StateFileName);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new BootTrialState();
        }

        try
        {
            return JsonSerializer.Deserialize(text, AgentJson.Default.BootTrialState) ?? new BootTrialState();
        }
        catch (JsonException)
        {
            // An unreadable ledger must not stop the frame. The cost is that an open trial is
            // forgotten, which means the change stands rather than being rolled back — so it is
            // said out loud rather than swallowed.
            _log.Warn($"The boot-trial ledger at {_store.PathOf(StateFileName)} could not be read; treating it as empty.");
            return new BootTrialState();
        }
    }

    private void Save(BootTrial trial)
    {
        var state = Read();
        var trials = state.Trials
            .Where(existing => !string.Equals(existing.Path, trial.Path, StringComparison.Ordinal))
            .Append(trial)
            .ToList();

        Write(new BootTrialState { Trials = trials });
    }

    private void Write(BootTrialState state) =>
        _store.WriteText(StateFileName, JsonSerializer.Serialize(state, AgentJson.Default.BootTrialState));
}
