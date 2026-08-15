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
/// so the incident was invisible until somebody looked at the frame.
/// </para>
/// <para>
/// <b>Two things, one resource.</b> The drop-in and the <c>/var/log/journal</c> directory are
/// observed together because <c>Storage=persistent</c> with the directory missing silently
/// stays volatile — the file is right, the setting is not in force, and nothing says so. That
/// is the same class of failure as the hostname trap in a much quieter form, and it is why the
/// catalog folds the directory check into Observe rather than leaving it to a second resource.
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

    private readonly ISystemFiles _files;
    private readonly FleetValues _values;

    /// <summary>Creates the resource.</summary>
    public JournalStorageResource(ISystemFiles files, FleetValues values)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _values = values;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame is not keeping a record of what it does.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it, a problem that happens overnight leaves nothing behind to look at.";

    /// <summary>The exact file content this resource converges on.</summary>
    public string DesiredContent() =>
        "[Journal]\n"
        + "Storage=persistent\n"
        + $"SystemMaxUse={_values.Get(SettingKey, DefaultMaxUse)}\n";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desired = DesiredContent();
        var actual = _files.ReadText(DropInPath);
        var directory = _files.DirectoryExists(JournalDirectory);

        var contentMatches = string.Equals(Normalise(actual), Normalise(desired), StringComparison.Ordinal);
        var expected = $"{DropInPath} {ShortHash(desired)} and {JournalDirectory} present";

        if (contentMatches && directory)
        {
            return ValueTask.FromResult(new ResourceObservation(true, expected, expected));
        }

        var wrong = new List<string>(2);
        if (!contentMatches)
        {
            wrong.Add(actual is null
                ? $"{DropInPath} absent"
                : $"{DropInPath} {ShortHash(actual)}");
        }

        if (!directory)
        {
            wrong.Add($"{JournalDirectory} missing, so the journal is still in memory only");
        }

        return ValueTask.FromResult(new ResourceObservation(false, expected, string.Join("; ", wrong)));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desired = DesiredContent();
        _files.WriteText(DropInPath, desired);
        _files.EnsureDirectory(JournalDirectory);

        return ValueTask.FromResult(new ResourceAction(
            $"write {DropInPath} (Storage=persistent, SystemMaxUse={_values.Get(SettingKey, DefaultMaxUse)}) "
            + $"and create {JournalDirectory}",
            "Telling this frame to keep its own log on the memory card instead of forgetting it at every restart."));
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
}
