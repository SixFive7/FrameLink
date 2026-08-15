using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FrameLink.Upstream;

/// <summary>What a probe found, compared with what the ledger last recorded.</summary>
public enum UpstreamState
{
    /// <summary>Upstream is serving exactly what the last review saw.</summary>
    Current,

    /// <summary>Upstream has published something since the last review.</summary>
    Moved,

    /// <summary>The probe could not be completed, so nothing is known either way.</summary>
    Unreachable,
}

/// <summary>One entry's verdict for this run.</summary>
/// <param name="Entry">The ledger entry.</param>
/// <param name="State">Current, moved, or unknown.</param>
/// <param name="Latest">What upstream answered, when it answered.</param>
/// <param name="Detail">Context worth printing beside the version — support phase, and such.</param>
/// <param name="Failure">Why the probe failed, when it did.</param>
public sealed record UpstreamFinding(
    UpstreamEntry Entry,
    UpstreamState State,
    string? Latest,
    string? Detail,
    string? Failure);

/// <summary>The answer to one probe.</summary>
/// <param name="Latest">The version upstream is serving, or null when the probe failed.</param>
/// <param name="Detail">Anything else worth printing.</param>
/// <param name="Failure">Why it failed, or null.</param>
public sealed record ProbeAnswer(string? Latest, string? Detail = null, string? Failure = null);

/// <summary>
/// Asks each upstream what it is serving right now (§7.1's detection half).
/// </summary>
/// <remarks>
/// <para>
/// Four kinds because there are four classes of chosen version in this repository, and each
/// publishes its truth in a different shape: an Apache directory listing, a GitHub release, a
/// NuGet version index and the .NET release metadata. None of them is guessed from a pattern in a
/// URL — a probe either reads the document upstream publishes for the purpose or it reports that
/// it could not.
/// </para>
/// <para>
/// <b>A failed probe is never a pass.</b> "The ledger is current" is a claim about what upstream
/// is serving, and an unreachable upstream leaves that unknown rather than fine. So
/// <see cref="UpstreamState.Unreachable"/> is its own state and it blocks a release exactly like
/// a move does, while saying something different about why.
/// </para>
/// </remarks>
public sealed partial class UpstreamProbes(HttpClient http)
{
    /// <summary>A client configured the way every probe here needs.</summary>
    /// <remarks>
    /// The user agent is not decoration: api.github.com answers 403 to a request without one.
    /// </remarks>
    public static HttpClient CreateClient() =>
        new()
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                UserAgent = { new ProductInfoHeaderValue("FrameLink-upstream-review", "1.0") },
            },
        };

    /// <summary>Probes one entry and classifies the answer.</summary>
    public async Task<UpstreamFinding> CheckAsync(UpstreamEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var answer = await ProbeAsync(entry.Probe, cancellationToken).ConfigureAwait(false);
        return Classify(entry, answer);
    }

    /// <summary>Compares one answer with one ledger entry. Pure, and therefore testable offline.</summary>
    public static UpstreamFinding Classify(UpstreamEntry entry, ProbeAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(answer);

        if (answer.Failure is not null || string.IsNullOrWhiteSpace(answer.Latest))
        {
            return new UpstreamFinding(
                entry,
                UpstreamState.Unreachable,
                null,
                answer.Detail,
                answer.Failure ?? "the probe answered nothing.");
        }

        var state = string.Equals(answer.Latest, entry.Reviewed.Upstream, StringComparison.Ordinal)
            ? UpstreamState.Current
            : UpstreamState.Moved;

        return new UpstreamFinding(entry, state, answer.Latest, answer.Detail, null);
    }

    /// <summary>Performs one probe.</summary>
    public async Task<ProbeAnswer> ProbeAsync(UpstreamProbe probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.Url is null)
        {
            return new ProbeAnswer(null, Failure: "the probe has no URL.");
        }

        try
        {
            return probe.Kind switch
            {
                UpstreamProbe.RaspiosImages => ReadRaspios(
                    await http.GetStringAsync(probe.Url, cancellationToken).ConfigureAwait(false)),
                UpstreamProbe.GithubRelease => ReadGithub(
                    await GetAsync(probe.Url, UpstreamJson.Default.GithubRelease, cancellationToken)
                        .ConfigureAwait(false)),
                UpstreamProbe.NugetPackage => ReadNuget(
                    await GetAsync(probe.Url, UpstreamJson.Default.NugetVersionIndex, cancellationToken)
                        .ConfigureAwait(false)),
                UpstreamProbe.DotnetChannel => ReadDotnet(
                    await GetAsync(probe.Url, UpstreamJson.Default.DotnetReleasesIndex, cancellationToken)
                        .ConfigureAwait(false),
                    probe.Channel),
                _ => new ProbeAnswer(null, Failure: $"there is no probe of kind '{probe.Kind}'."),
            };
        }
        catch (HttpRequestException exception)
        {
            return new ProbeAnswer(null, Failure: exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeAnswer(null, Failure: $"{probe.Url} did not answer within the timeout.");
        }
        catch (JsonException exception)
        {
            return new ProbeAnswer(null, Failure: $"{probe.Url} answered something unparseable: {exception.Message}");
        }
    }

    private async Task<T?> GetAsync<T>(
        Uri url,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body, typeInfo);
    }

    /// <summary>
    /// The newest published image directory, e.g. <c>2026-06-19</c> from
    /// <c>raspios_lite_arm64-2026-06-19/</c>.
    /// </summary>
    /// <remarks>
    /// The directory date and the image date differ by a day — the 2026-06-19 directory holds the
    /// 2026-06-18 image — so this deliberately answers with the directory, which is the thing the
    /// pin's URL names and the only one visible without downloading 2.8 GB.
    /// </remarks>
    private static ProbeAnswer ReadRaspios(string listing)
    {
        var dates = ImageDirectory().Matches(listing)
            .Select(match => match.Groups["date"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(date => date, StringComparer.Ordinal)
            .ToList();

        return dates.Count == 0
            ? new ProbeAnswer(null, Failure: "the image listing named no dated directories.")
            : new ProbeAnswer(dates[0], $"{dates.Count} published images");
    }

    private static ProbeAnswer ReadGithub(GithubRelease? release) =>
        string.IsNullOrWhiteSpace(release?.TagName)
            ? new ProbeAnswer(null, Failure: "the latest release has no tag.")
            : new ProbeAnswer(release.TagName.TrimStart('v'));

    /// <summary>
    /// The newest stable version. Prereleases are skipped — nothing here would take one.
    /// </summary>
    private static ProbeAnswer ReadNuget(NugetVersionIndex? index)
    {
        var stable = index?.Versions
            .Where(version => !version.Contains('-', StringComparison.Ordinal))
            .LastOrDefault();

        return string.IsNullOrWhiteSpace(stable)
            ? new ProbeAnswer(null, Failure: "the package index lists no stable version.")
            : new ProbeAnswer(stable);
    }

    /// <summary>
    /// The channel's newest SDK, with its support phase alongside.
    /// </summary>
    /// <remarks>
    /// The phase is detail rather than the compared value on purpose: a band leaving active
    /// support is exactly the news this entry exists to deliver, and it must not be able to hide
    /// behind an SDK number that happens to be unchanged.
    /// </remarks>
    private static ProbeAnswer ReadDotnet(DotnetReleasesIndex? index, string? channel)
    {
        var row = index?.Channels.FirstOrDefault(
            candidate => string.Equals(candidate.ChannelVersion, channel, StringComparison.Ordinal));

        if (row is null)
        {
            return new ProbeAnswer(null, Failure: $"the releases index has no channel {channel}.");
        }

        var detail = $"{row.SupportPhase} support, runtime {row.LatestRuntime}"
            + (string.IsNullOrWhiteSpace(row.EolDate) ? string.Empty : $", EOL {row.EolDate}");

        return string.IsNullOrWhiteSpace(row.LatestSdk)
            ? new ProbeAnswer(null, detail, $"channel {channel} names no SDK.")
            : new ProbeAnswer(row.LatestSdk, detail);
    }

    [GeneratedRegex(@"[A-Za-z0-9_]+-(?<date>\d{4}-\d{2}-\d{2})/", RegexOptions.ExplicitCapture)]
    private static partial Regex ImageDirectory();
}
