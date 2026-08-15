using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>identity.hostname</c> — the frame's name, owned by cloud-init.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the resource the reboot rule exists for.</b> Observed on the mule 2026-08-15 and
/// recorded in version2.md Appendix B item 1: this image's hostname is managed by cloud-init's
/// NoCloud datasource, seeded from the boot partition by Raspberry Pi Imager
/// (<c>ds=nocloud;i=rpi-imager-…</c> in the kernel command line). <c>hostnamectl
/// set-hostname</c> appears to succeed, survives for the rest of the session, and is
/// <i>silently reverted at the next boot</i>. A <c>preserve_hostname: true</c> drop-in was not
/// enough.
/// </para>
/// <para>
/// A write-only check would therefore have marked this <c>InSync</c> while it was quietly
/// wrong — which is the whole argument of §2.4, stated as a fact about one setting rather than
/// as a principle. The Act writes cloud-init's seed; the Verify is the same Observe, run after
/// the machine has actually booted.
/// </para>
/// <para>
/// <b>Four owners, one resource.</b> Observe reads all four places the name lives — the live
/// hostname, the NoCloud <c>meta-data</c>, the cloud-config <c>user-data</c> and
/// <c>/etc/hosts</c> — and the delta names whichever disagree. Splitting them into four
/// resources would break §2.2's granularity rule, because they cannot be acted on
/// independently: writing one and not the others is the half-applied state the catalog warns
/// about under <c>/etc/hosts</c>.
/// </para>
/// <para>
/// <b>On risk.</b> The catalog calls this brick-adjacent because the write lands in
/// <c>/boot/firmware</c>. It is not brick-<i>capable</i>: neither <c>meta-data</c> nor
/// <c>user-data</c> is read by the bootloader or the kernel — only by cloud-init, after the
/// system is already up — so a malformed one costs a wrong hostname, not an unbootable frame.
/// <c>config.txt</c>, <c>cmdline.txt</c> and the EEPROM are the files that can, and none of them
/// is touched here.
/// </para>
/// </remarks>
public sealed class HostnameResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "identity.hostname";

    /// <summary>Fleet setting carrying the desired name (§3.4).</summary>
    public const string SettingKey = "device.hostname";

    /// <summary>The NoCloud datasource's metadata, seeded by Raspberry Pi Imager.</summary>
    public const string MetaDataPath = "/boot/firmware/meta-data";

    /// <summary>The cloud-config document, seeded alongside it.</summary>
    public const string UserDataPath = "/boot/firmware/user-data";

    /// <summary>The name-to-loopback mapping every Debian system carries.</summary>
    public const string HostsPath = "/etc/hosts";

    /// <summary>The address <c>/etc/hosts</c> maps the hostname onto.</summary>
    public const string LoopbackAddress = "127.0.1.1";

    private const string MetaDataKey = "local-hostname";
    private const string UserDataKey = "hostname";
    private const string CloudConfigHeader = "#cloud-config";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly FleetValues _values;
    private readonly string _fallback;

    /// <summary>Creates the resource.</summary>
    /// <param name="files">The boot partition and <c>/etc</c>.</param>
    /// <param name="processes">How <c>hostnamectl</c> is invoked.</param>
    /// <param name="values">Where the desired name comes from.</param>
    /// <param name="fallback">
    /// The name to converge on when the Fleet Manager has not set one. Defaults to whatever the
    /// frame is already called, so an unconfigured fleet does not rename every frame to a
    /// constant — which would be a genuine outage rather than a missing setting.
    /// </param>
    public HostnameResource(
        ISystemFiles files,
        IProcessRunner processes,
        FleetValues values,
        string? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _processes = processes;
        _values = values;
        _fallback = fallback ?? string.Empty;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [AdoptionResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame is not using the name it was given.";

    /// <inheritdoc/>
    public string WhyItMatters => "The name is how this frame is found on your network and told apart from the others.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var desired = Desired();
        if (desired.Length == 0)
        {
            // Nothing has said what this frame should be called, and inventing a name would be
            // worse than leaving it alone. Converging on "no opinion" keeps the frame green
            // instead of permanently repairing a field nobody filled in.
            return new ResourceObservation(true, "no name set by the Fleet Manager", "no name set");
        }

        var live = await ReadLiveHostnameAsync(cancellationToken).ConfigureAwait(false);
        var seedMeta = ReadYamlScalar(_files.ReadText(MetaDataPath), MetaDataKey);
        var seedUser = ReadYamlScalar(_files.ReadText(UserDataPath), UserDataKey);
        var hosts = ReadHostsEntry(_files.ReadText(HostsPath));

        var wrong = new List<string>(4);
        Check(wrong, "live", live, desired);
        Check(wrong, $"{MetaDataPath}:{MetaDataKey}", seedMeta, desired);
        Check(wrong, $"{UserDataPath}:{UserDataKey}", seedUser, desired);
        Check(wrong, $"{HostsPath}:{LoopbackAddress}", hosts, desired);

        return new ResourceObservation(
            wrong.Count == 0,
            desired,
            wrong.Count == 0 ? desired : string.Join("; ", wrong));

        static void Check(List<string> wrong, string label, string? actual, string desired)
        {
            if (!string.Equals(actual, desired, StringComparison.Ordinal))
            {
                wrong.Add($"{label}={actual ?? "absent"}");
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var desired = Desired();
        var changes = new List<string>(4);

        // The seed first, because it is the owner. Everything below it is the running system
        // catching up, and would be undone at the next boot on its own.
        _files.WriteText(MetaDataPath, WriteYamlScalar(_files.ReadText(MetaDataPath), MetaDataKey, desired));
        changes.Add($"{MetaDataPath}: {MetaDataKey}: {desired}");

        _files.WriteText(UserDataPath, WriteCloudConfig(_files.ReadText(UserDataPath), desired));
        changes.Add($"{UserDataPath}: {UserDataKey}: {desired}");

        _files.WriteText(HostsPath, WriteHostsEntry(_files.ReadText(HostsPath), desired));
        changes.Add($"{HostsPath}: {LoopbackAddress}\t{desired}");

        // Last, and deliberately not trusted. It makes the running session agree immediately so
        // the frame is reachable under its new name before the reboot; it is not what makes the
        // change stick, and believing it was is the trap this resource exists to document.
        var result = await _processes
            .RunAsync("hostnamectl", ["set-hostname", desired], cancellationToken)
            .ConfigureAwait(false);

        changes.Add(result.Succeeded
            ? $"hostnamectl set-hostname {desired}"
            : $"hostnamectl set-hostname {desired} (refused: {result.Combined})");

        return new ResourceAction(
            string.Join(" · ", changes),
            $"Telling this frame, and the settings it reads when it starts up, that it is called '{desired}'.");
    }

    private string Desired() => _values.Get(SettingKey, _fallback).Trim();

    private async Task<string?> ReadLiveHostnameAsync(CancellationToken cancellationToken)
    {
        var result = await _processes
            .RunAsync("hostnamectl", ["--static"], cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded && result.StandardOutput.Length > 0 ? result.StandardOutput.Trim() : null;
    }

    /// <summary>Reads a top-level scalar out of a small YAML document.</summary>
    /// <remarks>
    /// Line-based rather than a YAML parser, and that is a deliberate limit rather than a
    /// shortcut: pulling a YAML library into a Native AOT binary that §2.1 requires to be one
    /// self-contained ELF is a large cost for two scalars. It handles what Imager writes —
    /// <c>key: value</c> at column zero, optionally quoted — and returns null for anything
    /// else, which reports drift rather than guessing.
    /// </remarks>
    public static string? ReadYamlScalar(string? document, string key)
    {
        if (string.IsNullOrEmpty(document))
        {
            return null;
        }

        foreach (var raw in document.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] is ' ' or '\t' or '#')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !line.AsSpan(0, colon).Trim().SequenceEqual(key))
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            return value.Length == 0 ? null : value;
        }

        return null;
    }

    /// <summary>Replaces a top-level scalar, or appends it.</summary>
    public static string WriteYamlScalar(string? document, string key, string value)
    {
        var lines = Lines(document);
        var written = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] is ' ' or '\t' or '#' || colon <= 0)
            {
                continue;
            }

            if (line.AsSpan(0, colon).Trim().SequenceEqual(key))
            {
                lines[index] = $"{key}: {value}";
                written = true;
            }
        }

        if (!written)
        {
            lines.Add($"{key}: {value}");
        }

        return Join(lines);
    }

    /// <summary>
    /// Sets the hostname in a cloud-config document, preserving everything else.
    /// </summary>
    /// <remarks>
    /// <c>preserve_hostname</c> is forced to <c>false</c> alongside it. Appendix B records that
    /// setting it to <c>true</c> was <i>not</i> sufficient to stop the revert, and leaving it
    /// true would be worse than useless here: it tells cloud-init to leave the hostname alone,
    /// which on this image means leaving it at whatever Imager seeded rather than at what the
    /// Fleet Manager asked for.
    /// </remarks>
    public static string WriteCloudConfig(string? document, string hostname)
    {
        var body = WriteYamlScalar(document, UserDataKey, hostname);
        body = WriteYamlScalar(body, "preserve_hostname", "false");

        var lines = Lines(body);
        if (lines.Count == 0 || !lines[0].StartsWith(CloudConfigHeader, StringComparison.Ordinal))
        {
            // cloud-init ignores a user-data document that does not open with this line. A file
            // that is silently ignored is exactly the failure mode this resource is about.
            lines.Insert(0, CloudConfigHeader);
        }

        return Join(lines);
    }

    /// <summary>Reads the name mapped to <see cref="LoopbackAddress"/>.</summary>
    public static string? ReadHostsEntry(string? document)
    {
        if (string.IsNullOrEmpty(document))
        {
            return null;
        }

        foreach (var raw in document.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length >= 2 && string.Equals(fields[0], LoopbackAddress, StringComparison.Ordinal))
            {
                return fields[1];
            }
        }

        return null;
    }

    /// <summary>Rewrites the loopback mapping, adding it if it was missing.</summary>
    public static string WriteHostsEntry(string? document, string hostname)
    {
        var lines = Lines(document);
        var written = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length >= 1 && string.Equals(fields[0], LoopbackAddress, StringComparison.Ordinal))
            {
                lines[index] = $"{LoopbackAddress}\t{hostname}";
                written = true;
            }
        }

        if (!written)
        {
            lines.Add($"{LoopbackAddress}\t{hostname}");
        }

        return Join(lines);
    }

    private static List<string> Lines(string? document) =>
        string.IsNullOrEmpty(document)
            ? []
            : [.. document.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')];

    private static string Join(List<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }
}
