namespace FrameLink.Parity;

/// <summary>
/// The three kinds of difference, and the three verdicts one can carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three kinds, because a flat diff proves nothing.</b> Something the v1 frame had and this
/// frame does not is a gap. Something this frame has and v1 never did may be an improvement —
/// v2 deliberately does things v1 did not — or a regression, and only a recorded reason tells
/// those apart. A value that moved is a third thing again, and for a package it splits once more
/// into forward (a security update, the one drift this project tolerates) and backward.
/// </para>
/// <para>
/// <b>The verdict is separate from the kind</b> and is assigned afterwards, by the expected
/// difference ledger. A difference nobody has explained is a <see cref="ParityVerdicts.Finding"/>; the same
/// difference with a recorded reason is <see cref="ParityVerdicts.Expected"/> and does not fail parity. That
/// separation is what lets the diff converge to empty and stay there.
/// </para>
/// </remarks>
public static class ParityDifferenceKinds
{
    /// <summary>Present in the v1 reference, absent on this frame.</summary>
    public const string Missing = "missing";

    /// <summary>Present on this frame, absent from the v1 reference.</summary>
    public const string Extra = "extra";

    /// <summary>Present in both, and the values are not equal.</summary>
    public const string Changed = "changed";

    /// <summary>A version that moved forward. What a security update looks like.</summary>
    public const string Ahead = "ahead";

    /// <summary>A version that moved backward. Nothing legitimate does this.</summary>
    public const string Behind = "behind";

    /// <summary>Every kind, for validating a ledger entry.</summary>
    public static IReadOnlyList<string> All { get; } = [Missing, Extra, Changed, Ahead, Behind];
}

/// <summary>What a single difference means once the ledger has been consulted.</summary>
public static class ParityVerdicts
{
    /// <summary>Nothing explains it. Parity fails while any of these exist.</summary>
    public const string Finding = "finding";

    /// <summary>A ledger entry explains it, and the reason is carried on the difference.</summary>
    public const string Expected = "expected";

    /// <summary>
    /// A package version that moved forward — the only drift the operator accepts (decision 55).
    /// </summary>
    public const string Tolerated = "tolerated";
}

/// <summary>The verdict for the whole run.</summary>
public static class ParityOutcomes
{
    /// <summary>Every difference is explained or tolerated, and coverage was established.</summary>
    public const string Parity = "parity";

    /// <summary>At least one unexplained difference.</summary>
    public const string Differs = "differs";

    /// <summary>
    /// The comparison could not be completed — a probe failed, or a facet could not be read.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same as <see cref="Differs"/>. "I looked and they disagree" and "I
    /// could not look" are different answers, and collapsing them is how a harness starts
    /// reporting silence as success.
    /// </remarks>
    public const string Incomplete = "incomplete";
}

/// <summary>How a facet's text is turned into the key/value map everything else compares.</summary>
public static class FacetKinds
{
    /// <summary>Explicit <c>key value</c> pairs, one per line or parsed from a known shape.</summary>
    public const string KeyValue = "key-value";

    /// <summary>Whole lines, counted. A line that appears twice is not the same as once.</summary>
    public const string LineMultiset = "line-multiset";

    /// <summary>Whitespace-separated tokens, counted. The kernel command line.</summary>
    public const string TokenMultiset = "token-multiset";

    /// <summary><c>/boot/firmware/config.txt</c>: directives, carrying their <c>[section]</c>.</summary>
    public const string ConfigDirectives = "config-directives";

    /// <summary><c>##### path</c> then content: a map of path to file body.</summary>
    public const string FileSet = "file-set";

    /// <summary>A flat JSON object.</summary>
    public const string Json = "json";

    /// <summary><c>amixer scontents</c>: one key per control, channel and capability line.</summary>
    public const string AlsaMixer = "alsa-mixer";

    /// <summary><c>id</c> plus matching <c>getent group</c> lines, reduced to membership by name.</summary>
    public const string UsersGroups = "users-groups";

    /// <summary><c>hostname</c> plus <c>ip -br addr</c>, reduced to interface link states.</summary>
    public const string Network = "network";

    /// <summary>Delegated whole to <see cref="Control.PackageDrift"/>.</summary>
    public const string Packages = "packages";

    /// <summary>Declared, never compared, with the reason recorded.</summary>
    public const string Uncovered = "uncovered";
}

/// <summary>How far a facet's comparison reaches.</summary>
public static class FacetCoverage
{
    /// <summary>Compared in full.</summary>
    public const string Full = "full";

    /// <summary>Compared, but something is deliberately out of scope. See the limitation.</summary>
    public const string Partial = "partial";

    /// <summary>Not compared at all. See the limitation.</summary>
    public const string None = "none";
}

/// <summary>What happened to one facet on this run.</summary>
public static class FacetStates
{
    /// <summary>Both sides parsed and were compared.</summary>
    public const string Compared = "compared";

    /// <summary>Declared uncovered; nothing was attempted.</summary>
    public const string Uncovered = "uncovered";

    /// <summary>The probe was not run — not requested, or it needs an elevation nobody granted.</summary>
    public const string NotCollected = "not-collected";

    /// <summary>The probe ran and failed. This is what makes a run incomplete.</summary>
    public const string ProbeFailed = "probe-failed";
}

/// <summary>
/// One thing the harness knows how to compare, and the command that observes it on a frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>One facet per <c>== SECTION</c> of the v1 inventory, and the mapping is total.</b>
/// <c>reference/v1-state-inventory.txt</c> is a captured artifact rather than a designed schema,
/// so the only defensible relationship to it is one that accounts for every block it contains —
/// including the blocks nothing can usefully compare, which are declared
/// <see cref="FacetKinds.Uncovered"/> with a reason rather than quietly dropped. A test asserts
/// the two sets are equal, so a section added to the inventory turns the suite red instead of
/// disappearing.
/// </para>
/// <para>
/// <b>The probe is written to produce the same shape as the block it is compared against</b>, so
/// one parser reads both sides. That is not tidiness: a v1 side and a v2 side going through
/// different parsers is a difference generator of its own, and it would be invisible.
/// </para>
/// </remarks>
public sealed record ParityFacet
{
    /// <summary>Stable identifier, used in the ledger and the artifacts.</summary>
    public required string Id { get; init; }

    /// <summary>The <c>== SECTION</c> header of the v1 inventory block this reads.</summary>
    public required string Section { get; init; }

    /// <summary>One line a person can read.</summary>
    public required string Title { get; init; }

    /// <summary>How the text becomes a key/value map. One of <see cref="FacetKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The read-only shell command that observes this on a frame; null when uncovered.</summary>
    public string? Probe { get; init; }

    /// <summary>How far the comparison reaches. One of <see cref="FacetCoverage"/>.</summary>
    public string Coverage { get; init; } = FacetCoverage.Full;

    /// <summary>Why the coverage is not full. Required whenever it is not.</summary>
    public string? Limitation { get; init; }

    /// <summary>
    /// Keys whose difference is ordered with <see cref="Protocol.DebianVersion"/> rather than
    /// compared for equality, so forward movement can be told from backward.
    /// </summary>
    public IReadOnlyList<string> VersionKeys { get; init; } = [];

    /// <summary>Keys parsed and then deliberately dropped, each with the reason.</summary>
    /// <remarks>
    /// A volatile field — a per-boot journal filename, a DHCP address — has to be dropped or
    /// every run reports it. Dropping it silently is the failure mode; this is the same drop
    /// with the reason attached and the coverage report carrying it.
    /// </remarks>
    public IReadOnlyDictionary<string, string> IgnoredKeys { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A line at which both sides stop being read, because the capture past it is not comparable.
    /// </summary>
    /// <remarks>
    /// One facet needs it. <c>ALSA_CARDS</c> holds the card list and then, under
    /// <c>--- state file ---</c>, an <c>alsactl</c> dump that the capture cut off mid-control.
    /// Reading to the end would compare a complete v2 dump against a truncated v1 one and report
    /// every control below the cut as missing — noise generated by the capture rather than by the
    /// frame.
    /// </remarks>
    public string? TruncateAt { get; init; }

    /// <summary>Whether the probe needs root, and is therefore skipped unless asked for.</summary>
    /// <remarks>
    /// The collector is unprivileged by default and says so. Two probes genuinely cannot be:
    /// the array's firmware version is a privileged USB control transfer, and the agent's state
    /// directory is <c>0700 root</c>. Both are opt-in rather than silently escalating.
    /// </remarks>
    public bool Elevated { get; init; }

    /// <summary>True when this facet is compared at all.</summary>
    public bool IsCovered => !string.Equals(Kind, FacetKinds.Uncovered, StringComparison.Ordinal);
}

/// <summary>One probe's raw result, as the collector recorded it.</summary>
public sealed record ParityObservation
{
    /// <summary>The facet this answers.</summary>
    public required string Facet { get; init; }

    /// <summary>The command that was actually run.</summary>
    public string? Command { get; init; }

    /// <summary>Its exit status.</summary>
    public int ExitStatus { get; init; }

    /// <summary>Its standard output, verbatim.</summary>
    public string Stdout { get => field; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Its standard error, verbatim. Kept even on success — a warning is evidence.</summary>
    public string Stderr { get => field; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Set when the collector chose not to run this probe, with the reason.</summary>
    public string? Skipped { get; init; }
}

/// <summary>Everything the collector brought back from one frame.</summary>
public sealed record ParityObservationSet
{
    /// <summary>Schema marker, so a judge can refuse a file it does not understand.</summary>
    public string Schema { get; init; } = "framelink-parity-observation-1";

    /// <summary>What produced it.</summary>
    public string? Collector { get; init; }

    /// <summary>The frame it came off. Never a credential, only an address.</summary>
    public string? Host { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset? CollectedUtc { get; init; }

    /// <summary>Whether elevated probes were requested.</summary>
    public bool Elevated { get; init; }

    /// <summary>One entry per facet the collector attempted.</summary>
    public IReadOnlyList<ParityObservation> Observations { get => field; init => field = value ?? []; } = [];
}

/// <summary>One difference, and what it means.</summary>
public sealed record ParityDifference
{
    /// <summary>Which facet.</summary>
    public required string Facet { get; init; }

    /// <summary>One of <see cref="ParityDifferenceKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The package, path, directive, token or key that differs.</summary>
    public required string Key { get; init; }

    /// <summary>What the v1 reference has, when it has anything.</summary>
    public string? Reference { get; init; }

    /// <summary>What this frame has, when it has anything.</summary>
    public string? Observed { get; init; }

    /// <summary>One of <see cref="ParityVerdicts"/>. Assigned by the ledger pass.</summary>
    public string Verdict { get; init; } = ParityVerdicts.Finding;

    /// <summary>Why it is not a finding. Null exactly when it is one.</summary>
    public string? Reason { get; init; }

    /// <summary>The ledger entry that explained it, when one did.</summary>
    public string? LedgerEntry { get; init; }

    /// <summary>Line-level detail for a changed file, worst first. Empty otherwise.</summary>
    public IReadOnlyList<string> Detail { get; init; } = [];
}

/// <summary>How one facet came out.</summary>
public sealed record ParityFacetResult
{
    /// <summary>The facet.</summary>
    public required string Facet { get; init; }

    /// <summary>Its one-line title, carried so the artifact reads on its own.</summary>
    public required string Title { get; init; }

    /// <summary>One of <see cref="FacetStates"/>.</summary>
    public required string State { get; init; }

    /// <summary>One of <see cref="FacetCoverage"/>.</summary>
    public required string Coverage { get; init; }

    /// <summary>Why coverage is not full, or why the state is not <c>compared</c>.</summary>
    public string? Limitation { get; init; }

    /// <summary>How many keys the v1 reference contributes.</summary>
    public int ReferenceKeys { get; init; }

    /// <summary>How many keys this frame contributed.</summary>
    public int ObservedKeys { get; init; }

    /// <summary>Unexplained differences.</summary>
    public int Findings { get; init; }

    /// <summary>Differences a ledger entry explains.</summary>
    public int Expected { get; init; }

    /// <summary>Package versions that moved forward.</summary>
    public int Tolerated { get; init; }

    /// <summary>Every difference, worst first.</summary>
    public IReadOnlyList<ParityDifference> Differences { get; init; } = [];
}

/// <summary>Where the harness admits it cannot see.</summary>
public sealed record ParityCoverageReport
{
    /// <summary>
    /// Inventory sections that are declared but never compared, with the reason for each.
    /// </summary>
    public IReadOnlyList<ParityFacetResult> UncoveredSections { get; init; } = [];

    /// <summary>Facets compared with something deliberately out of scope.</summary>
    public IReadOnlyList<ParityFacetResult> PartialSections { get; init; } = [];

    /// <summary>Keys parsed and dropped, and why. <c>facet.key</c> to reason.</summary>
    public IReadOnlyDictionary<string, string> IgnoredKeys { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Catalog resources whose state the v1 inventory never captured, so parity cannot be
    /// proven for them from this comparison at all.
    /// </summary>
    public IReadOnlyList<CatalogEvidence> ResourcesWithoutReference { get; init; } = [];

    /// <summary>Catalog resources whose evidence a facet does carry.</summary>
    public IReadOnlyList<CatalogEvidence> ResourcesWithReference { get; init; } = [];

    /// <summary>How many resource ids the catalog document enumerates.</summary>
    public int CatalogResources { get; init; }
}

/// <summary>Which facet, if any, carries the v1 evidence for one catalog resource.</summary>
public sealed record CatalogEvidence
{
    /// <summary>The catalog resource id.</summary>
    public required string Resource { get; init; }

    /// <summary>The facet holding its v1 state, or null when the inventory never captured it.</summary>
    public string? Facet { get; init; }

    /// <summary>Why — either what the facet carries, or why nothing does.</summary>
    public required string Note { get; init; }
}

/// <summary>The whole verdict.</summary>
public sealed record ParityReport
{
    /// <summary>Schema marker.</summary>
    public string Schema { get; init; } = "framelink-parity-1";

    /// <summary>When this judgement was made.</summary>
    public DateTimeOffset GeneratedUtc { get; init; }

    /// <summary>What collected the observation, and from where.</summary>
    public string? Collector { get; init; }

    /// <summary>The frame.</summary>
    public string? Host { get; init; }

    /// <summary>When the observation was taken, which is not when it was judged.</summary>
    public DateTimeOffset? CollectedUtc { get; init; }

    /// <summary>One of <see cref="ParityOutcomes"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The one-paragraph reason for the outcome, for a reader who reads nothing else.</summary>
    public required string Summary { get; init; }

    /// <summary>Unexplained differences across every facet.</summary>
    public int Findings { get; init; }

    /// <summary>Differences the ledger explains.</summary>
    public int Expected { get; init; }

    /// <summary>Package versions ahead of the baseline.</summary>
    public int Tolerated { get; init; }

    /// <summary>Facets whose probe failed or was never collected.</summary>
    public int Unresolved { get; init; }

    /// <summary>Every facet, in declaration order.</summary>
    public IReadOnlyList<ParityFacetResult> Facets { get; init; } = [];

    /// <summary>Where the harness cannot see.</summary>
    public required ParityCoverageReport Coverage { get; init; }

    /// <summary>
    /// Ledger entries that matched nothing on this run.
    /// </summary>
    /// <remarks>
    /// Not a parity failure — the difference they excuse has gone, which is the direction
    /// everything here is trying to move in. It is reported loudly because an entry nobody
    /// removes is an excuse waiting to cover a real regression that happens to look the same.
    /// </remarks>
    public IReadOnlyList<string> StaleLedgerEntries { get; init; } = [];

    /// <summary>How many ledger entries exist, and how many were used.</summary>
    public int LedgerEntries { get; init; }

    /// <summary>How many ledger entries matched at least one difference.</summary>
    public int LedgerEntriesUsed { get; init; }
}
