using System.Text.Json.Serialization;

namespace FrameLink.Upstream;

/// <summary>
/// Source-generated serialisation for the ledger and for the four upstream documents it reads.
/// </summary>
/// <remarks>
/// <c>Directory.Build.props</c> holds every project in this repository to the trim and AOT
/// analysers at error severity, so a reflection-based <c>JsonSerializer</c> call fails the build
/// (IL2026/IL3050). That applies here too, even though nothing publishes this tool — the point of
/// keeping it inside the same build is that it is written the same way as the code it gates.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(UpstreamLedger))]
[JsonSerializable(typeof(GithubRelease))]
[JsonSerializable(typeof(NugetVersionIndex))]
[JsonSerializable(typeof(DotnetReleasesIndex))]
public sealed partial class UpstreamJson : JsonSerializerContext;

/// <summary>The one field this tool wants from GitHub's latest-release document.</summary>
public sealed record GithubRelease
{
    /// <summary>The tag, usually with a leading <c>v</c> this tool strips.</summary>
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }
}

/// <summary>The NuGet flat-container version index for one package.</summary>
public sealed record NugetVersionIndex
{
    /// <summary>Every published version, oldest first, prereleases included.</summary>
    [JsonPropertyName("versions")]
    public IReadOnlyList<string> Versions { get; init; } = [];
}

/// <summary>The .NET releases index — every channel, one row each.</summary>
public sealed record DotnetReleasesIndex
{
    /// <summary>The channels.</summary>
    [JsonPropertyName("releases-index")]
    public IReadOnlyList<DotnetChannelRow> Channels { get; init; } = [];
}

/// <summary>One .NET release channel.</summary>
public sealed record DotnetChannelRow
{
    /// <summary>Which band, e.g. <c>10.0</c>.</summary>
    [JsonPropertyName("channel-version")]
    public string? ChannelVersion { get; init; }

    /// <summary>The newest SDK in this channel — what the ledger records as the seen version.</summary>
    [JsonPropertyName("latest-sdk")]
    public string? LatestSdk { get; init; }

    /// <summary>The newest runtime in this channel.</summary>
    [JsonPropertyName("latest-runtime")]
    public string? LatestRuntime { get; init; }

    /// <summary><c>active</c>, <c>maintenance</c>, <c>eol</c>, <c>preview</c>.</summary>
    [JsonPropertyName("support-phase")]
    public string? SupportPhase { get; init; }

    /// <summary>When support ends, when upstream has published a date.</summary>
    [JsonPropertyName("eol-date")]
    public string? EolDate { get; init; }
}
