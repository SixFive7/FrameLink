using System.Text.Json.Serialization;

namespace FrameLink.Parity;

/// <summary>
/// Source-generated serialisation for everything this tool reads and writes.
/// </summary>
/// <remarks>
/// <c>Directory.Build.props</c> holds every project in this repository to the trim and AOT
/// analysers at error severity, so a reflection-based <c>JsonSerializer</c> call fails the build
/// (IL2026/IL3050). That applies here too, even though nothing publishes this tool: the point of
/// keeping a development tool inside the same build is that it is written the same way as the
/// code it is used to judge.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ExpectedDifferenceLedger))]
[JsonSerializable(typeof(ParityObservationSet))]
[JsonSerializable(typeof(ParityReport))]
[JsonSerializable(typeof(ParityProbeList))]
public sealed partial class ParityJson : JsonSerializerContext;

/// <summary>
/// The read-only commands a collector runs on a frame, and which facet each one answers.
/// </summary>
/// <remarks>
/// <b>The collector asks for this rather than holding its own copy.</b> Two lists of probes — one
/// in the Python that reaches the frame and one in the judge that reads the answers — is the
/// shape where a facet gets renamed on one side and silently stops being collected on the other.
/// The judge owns the facet set; the collector runs whatever it is handed and labels the output
/// with the facet id it was given.
/// </remarks>
public sealed record ParityProbeList
{
    /// <summary>Schema marker.</summary>
    public string Schema { get; init; } = "framelink-parity-probes-1";

    /// <summary>Every probe, in the inventory's own section order.</summary>
    public IReadOnlyList<ParityProbe> Probes { get; init; } = [];
}

/// <summary>One read-only observation command.</summary>
public sealed record ParityProbe
{
    /// <summary>The facet this answers.</summary>
    public required string Facet { get; init; }

    /// <summary>The <c>== SECTION</c> of the frozen capture this will be compared against.</summary>
    /// <remarks>
    /// Carried so the probe list is self-describing: a reader can see which captured block each
    /// command reproduces without opening the facet table, and a replay of the reference can be
    /// built from the two files alone.
    /// </remarks>
    public required string Section { get; init; }

    /// <summary>One line a person can read.</summary>
    public required string Title { get; init; }

    /// <summary>The command, to be run through a shell on the frame exactly as it stands.</summary>
    public required string Command { get; init; }

    /// <summary>Whether it needs root, and is therefore skipped unless the operator asks.</summary>
    public bool Elevated { get; init; }
}
