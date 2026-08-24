using System.Net;
using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>identity.hostname</c> — the frame's name, <b>and that the name resolves to this machine</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trap this resource used to implement was measured and disproved.</b> It was written
/// against version2.md Appendix B item 1, which recorded the hostname as cloud-init managed and
/// silently reverted at the next boot, and it acted on that by writing cloud-init's NoCloud seed
/// on the boot partition. Measured on the mule 2026-08-15: <c>hostnamectl set-hostname</c>
/// <i>does</i> persist — <c>raspberrypi</c> → <c>framelink-mule</c>, a real reboot with
/// <c>boot_id</c> moving, and the name held — and cloud-init logged nothing about hostnames.
/// There is also nothing to re-apply: <c>/boot/firmware/user-data</c> carries <c>#hostname:</c>
/// commented out and <c>/boot/firmware/meta-data</c> has no <c>local-hostname</c> at all, and
/// cloud-init's <c>update_hostname</c> stands down once the running name differs from its
/// recorded <c>previous-hostname</c>, treating the value as human-maintained. The corrected
/// <c>identity.hostname</c> entry in <c>reference/resource-catalog.md</c> is the specification
/// this file now follows.
/// </para>
/// <para>
/// <b>The real defect was underneath it, and it is worse than a wrong name.</b> <c>hostnamectl</c>
/// maintains <c>/etc/hostname</c> and not <c>/etc/hosts</c>, so after the rename
/// <c>127.0.1.1</c> still named the old host, resolution fell through to DNS, and the search
/// domain answered <c>getent hosts framelink-mule</c> with <c>217.61.253.65
/// framelink-mule.huisman.io</c> — <b>the frame resolved its own name to a public internet
/// address</b>. Anything that binds to, advertises or certifies its own name was pointed at a
/// machine that is not this one, and the only warning was <c>sudo</c>'s <c>unable to resolve
/// host</c>, which reads as cosmetic noise.
/// </para>
/// <para>
/// <b>So Observe asks two questions, not one.</b> The name, and whether the name resolves to
/// loopback. Half-applied is the dangerous state here and a merely wrong name is the mild one, and
/// a check that compares the hostname string alone passes happily while the frame is in the
/// dangerous one. Both come from the running system rather than from the file that is supposed to
/// produce it, because what matters is the answer <c>getent</c> gives, not the bytes that were
/// written towards it.
/// </para>
/// <para>
/// <b>On risk.</b> Not brick-capable, and <b>not brick-adjacent either</b>: nothing under
/// <c>/boot/firmware</c> is written any more, so none of the boot-partition write discipline
/// applies. <c>config.txt</c>, <c>cmdline.txt</c> and the EEPROM are the files that can brick a
/// frame, and this resource touches none of them.
/// </para>
/// <para>
/// <b>Decision 26 survives the correction intact.</b> A write-only check would still have been
/// wrong here, only about a different thing: <c>hostnamectl</c> returns success at the exact
/// instant the resource is half-applied. The reboot proves the whole state rather than the half
/// the tool owns.
/// </para>
/// </remarks>
public sealed class HostnameResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "identity.hostname";

    /// <summary>Fleet setting carrying the desired name (§3.4).</summary>
    public const string SettingKey = "device.hostname";

    /// <summary>The name-to-loopback mapping every Debian system carries.</summary>
    public const string HostsPath = "/etc/hosts";

    /// <summary>The address <c>/etc/hosts</c> maps the hostname onto.</summary>
    public const string LoopbackAddress = "127.0.1.1";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;
    private readonly FleetValues _values;
    private readonly string _fallback;

    /// <summary>Creates the resource.</summary>
    /// <param name="files"><c>/etc/hosts</c>.</param>
    /// <param name="processes">How <c>hostnamectl</c> and <c>getent</c> are invoked.</param>
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
    public string Detected => "This frame is not using the name it was given, or that name does not point at this frame.";

    /// <inheritdoc/>
    public string WhyItMatters => "The name is how this frame is found on your network, and it has to lead back here.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var live = await ReadLiveHostnameAsync(cancellationToken).ConfigureAwait(false);
        var desired = Desired(live);

        if (desired.Length == 0)
        {
            // Nothing has said what this frame should be called and it does not currently have a
            // name to keep, so there is no value to converge on. Inventing one would be worse
            // than leaving it alone.
            return new ResourceObservation(true, "no name set by the Fleet Manager", "no name set");
        }

        var resolved = await ResolveAsync(desired, cancellationToken).ConfigureAwait(false);

        var wrong = new List<string>(2);

        if (!string.Equals(live, desired, StringComparison.Ordinal))
        {
            wrong.Add($"live={live ?? "absent"}");
        }

        if (!IsLoopback(resolved))
        {
            // The half that catches the measured fault. On the mule this read
            // "framelink-mule resolves to 217.61.253.65", which is a machine on the internet.
            wrong.Add($"{desired} resolves to {resolved ?? "nothing"}");
        }

        return new ResourceObservation(
            wrong.Count == 0,
            desired,
            wrong.Count == 0 ? desired : string.Join("; ", wrong));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var live = await ReadLiveHostnameAsync(cancellationToken).ConfigureAwait(false);
        var desired = Desired(live);
        var changes = new List<string>(2);

        // The mapping first, and the order is the fix rather than a preference. Renaming before
        // the file is written leaves the frame holding a name that resolves off-box — the exact
        // measured state — for however long the second half takes, and forever if it fails. This
        // way round the worst partial outcome is a file naming a host this machine is about to
        // become, which resolves nothing anywhere and repairs itself on the next attempt.
        _files.WriteText(HostsPath, WriteHostsEntry(_files.ReadText(HostsPath), desired));
        changes.Add($"{HostsPath}: {LoopbackAddress}\t{desired}");

        var result = await _processes
            .RunAsync("hostnamectl", ["set-hostname", desired], ProcessDeadline.Service, cancellationToken)
            .ConfigureAwait(false);

        changes.Add(result.Succeeded
            ? $"hostnamectl set-hostname {desired}"
            : $"hostnamectl set-hostname {desired} (refused: {result.Combined})");

        return new ResourceAction(
            string.Join(" · ", changes),
            $"Naming this frame '{desired}', and making that name lead back to the frame itself.");
    }

    /// <summary>Rewrites the loopback mapping, adding it if it was missing.</summary>
    /// <remarks>
    /// Idempotent by construction: the line is replaced rather than appended, so running it twice
    /// leaves one mapping. Every other line — <c>127.0.0.1 localhost</c>, the IPv6 block, anything
    /// an operator added — is preserved exactly, because this file is not owned by the agent.
    /// </remarks>
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

    /// <summary>Whether an address answered by <c>getent</c> points back at this machine.</summary>
    /// <remarks>
    /// Parsed rather than string-matched, so <c>127.0.1.1</c>, <c>127.0.0.1</c>, <c>::1</c> and
    /// the fully written-out IPv6 loopback all pass and a hostname that merely <i>starts</i> with
    /// digits does not.
    /// </remarks>
    public static bool IsLoopback(string? address) =>
        IPAddress.TryParse(address, out var parsed) && IPAddress.IsLoopback(parsed);

    /// <summary>
    /// The name this frame should have: the fleet setting, else the catalog fallback, else
    /// whatever it is already called.
    /// </summary>
    /// <remarks>
    /// The last step is what makes the resource useful on a fleet that has never set
    /// <c>device.hostname</c>: the value of this resource is the <c>/etc/hosts</c> half, and that
    /// half is worth enforcing for the name the frame already has. It is a catalog default in
    /// §1.2.2's sense — a value the agent can name from the device itself — and not a guess about
    /// what an unreachable Fleet Manager would have said.
    /// </remarks>
    private string Desired(string? live) => _values.Get(SettingKey, _fallback).Trim() is { Length: > 0 } set
        ? set
        : live?.Trim() ?? string.Empty;

    private async Task<string?> ReadLiveHostnameAsync(CancellationToken cancellationToken)
    {
        var result = await _processes
            .RunAsync("hostnamectl", ["--static"], ProcessDeadline.Service, cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded && result.StandardOutput.Length > 0 ? result.StandardOutput.Trim() : null;
    }

    /// <summary>What this machine answers when asked where its own name lives.</summary>
    /// <remarks>
    /// <c>getent</c> rather than a read of <c>/etc/hosts</c>, deliberately. It goes through
    /// <c>nsswitch.conf</c> exactly as every other consumer on the frame does, so it sees the DNS
    /// fall-through that produced the public address — which a file compare cannot, because in
    /// that state the file was not wrong about anything it contained, it was simply missing the
    /// line. A non-zero exit means the name resolves nowhere at all, which is drift of the same
    /// kind and reported as such.
    /// </remarks>
    private async Task<string?> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        var result = await _processes
            .RunAsync("getent", ["hosts", hostname], ProcessDeadline.Resolver, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return null;
        }

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var fields = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length >= 1)
            {
                return fields[0];
            }
        }

        return null;
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
