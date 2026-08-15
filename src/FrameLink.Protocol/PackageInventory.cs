using System.Security.Cryptography;
using System.Text;

namespace FrameLink.Protocol;

/// <summary>
/// Every package a frame has installed, with its version, on the <c>telemetry</c> channel of
/// §4.1. <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the whole system and not the fifteen the catalog manages.</b> The operator asked for
/// all of them, and the reason holds up: the fifteen are the packages FrameLink installs, while
/// the ~930 are the packages a security update can move. A frame whose <c>openssl</c> is a
/// month behind its neighbour is exactly the fact this payload exists to surface, and no
/// resource in the catalog would ever have mentioned it.
/// </para>
/// <para>
/// <b>Reported on change, never on a tick.</b> Sending ~930 entries with every telemetry
/// heartbeat would be ~30 kB of unchanged text several times a minute for a set that only moves
/// when apt runs. <see cref="ContentHash"/> is what makes that avoidable: the agent hashes the
/// canonical rendering, compares it with the last hash it delivered, and stays silent when they
/// match. So the wire cost is one message per actual package change — in practice a handful a
/// month, from <c>unattended-upgrades</c> — plus one on the first connect after a wipe.
/// </para>
/// <para>
/// <b>A picture, not history</b>, which is what decides how it buffers offline (§4.1) and how it
/// is stored (§3.5). Only the newest matters, so a frame that spends a week offline while apt
/// runs twice delivers the state it ended in, not both states it passed through. That is the
/// same rule <see cref="ReconcileReport"/> follows and for the same reason.
/// </para>
/// <para>
/// <b>No <c>DeviceId</c> is believed from this payload</b> any more than from any other: the
/// server binds what it stores to the identity the socket proved. The field is here because the
/// agent buffers the serialised message on disk and a buffered message has to be self-describing.
/// </para>
/// </remarks>
public sealed record PackageInventory
{
    /// <summary>
    /// The most packages one report may carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Fleet Manager refuses a WebSocket message over 256 kB, because a route open to the
    /// internet must not let a peer decide how much the server allocates. At the ~35 bytes an
    /// entry costs once serialised, four thousand entries is ~140 kB — comfortably inside that
    /// ceiling with room for the envelope, and more than four times the ~930 a frame actually
    /// carries. A frame that somehow exceeded it would otherwise produce a message the server
    /// drops in silence, which is the worst of the available outcomes.
    /// </para>
    /// <para>
    /// This is a ceiling on the payload and never a ceiling on the observation:
    /// <see cref="ObservedCount"/> reports what dpkg said either way.
    /// </para>
    /// </remarks>
    public const int MaxPackages = 4000;

    /// <summary>The frame this is about.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Monotonic per-device counter, so a late-draining buffer can be ordered.</summary>
    public required long Sequence { get; init; }

    /// <summary>When the agent read dpkg's database, not when the server received it.</summary>
    public required DateTimeOffset GeneratedUtc { get; init; }

    /// <summary>
    /// SHA-256, lowercase hex, of the canonical <c>name version\n</c> rendering of
    /// <see cref="Packages"/> in ordinal name order.
    /// </summary>
    /// <remarks>
    /// Both ends compute it the same way, which is what lets the Fleet Manager store one row per
    /// <i>distinct set</i> across the whole fleet rather than one per device per report: ten
    /// frames converged on the same packages share a single stored blob, and their rows are the
    /// hash. It is also the agent's own change detector — see the type remarks.
    /// </remarks>
    public required string ContentHash { get; init; }

    /// <summary>
    /// How many installed packages dpkg reported, which is not always how many are in
    /// <see cref="Packages"/>.
    /// </summary>
    /// <remarks>
    /// The two differ only when the set was too large to send and was cut down to fit — see
    /// <see cref="MaxPackages"/>. Carrying the count rather than a
    /// boolean means a truncated report says <i>how much</i> is missing instead of merely that
    /// something is, and a reader that ignores the field still sees a smaller set rather than a
    /// wrong one.
    /// </remarks>
    public required int ObservedCount { get; init; }

    /// <summary>
    /// Package name to installed version, for every package dpkg reports as <c>installed</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is dpkg's <c>${binary:Package}</c>, so a foreign-architecture package carries its
    /// <c>:arch</c> qualifier and the key stays unique on a multi-arch system. On the frames this
    /// project builds there is one architecture and the names are bare.
    /// </para>
    /// <para>
    /// <b>Only <c>installed</c>.</b> dpkg also knows packages in its <c>rc</c> state — removed,
    /// configuration kept — and reports a version for them, which would make a package that is
    /// <i>gone</i> appear present in every comparison built on this. The distinction is the same
    /// one <c>PackageStatus</c> makes on the agent side, for the same reason.
    /// </para>
    /// </remarks>
    public required IReadOnlyDictionary<string, string> Packages { get; init; }

    /// <summary>
    /// The exact text <see cref="ContentHash"/> is taken over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This rendering is contract, and it lives here because both ends depend on it.</b> The
    /// agent hashes it to decide whether anything has changed since its last report; the Fleet
    /// Manager re-hashes what it received to derive the key it stores the set under, and stores
    /// this text rather than the JSON because it is smaller and is the thing the key describes.
    /// Two implementations that disagreed about a trailing newline would produce two keys for one
    /// set, and the fleet-wide deduplication that makes a month of history affordable would
    /// silently stop working.
    /// </para>
    /// <para>
    /// Ordinal name order, one space between name and version, a newline after every entry
    /// including the last, nothing else.
    /// </para>
    /// </remarks>
    public static string Canonicalise(IReadOnlyDictionary<string, string> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var builder = new StringBuilder(packages.Count * 32);
        foreach (var name in packages.Keys.Order(StringComparer.Ordinal))
        {
            builder.Append(name).Append(' ').Append(packages[name]).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>SHA-256, lowercase hex, of <see cref="Canonicalise"/>.</summary>
    public static string HashOf(IReadOnlyDictionary<string, string> packages) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalise(packages))));

    /// <summary>Reads a canonical rendering back into a package set.</summary>
    /// <remarks>
    /// The inverse of <see cref="Canonicalise"/>, for whoever stored the text and needs the set
    /// again. A line without a space is skipped rather than throwing: a stored blob that one
    /// character has damaged should cost that entry, not the whole comparison it feeds.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ParseCanonical(string? text)
    {
        var packages = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                packages[line[..space]] = line[(space + 1)..].Trim();
            }
        }

        return packages;
    }
}
