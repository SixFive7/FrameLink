using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameLink.Parity;

/// <summary>
/// <c>expected-differences.json</c> — every way a v2 frame is allowed to differ from the frozen
/// v1 reference, each with the reason it is allowed to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an explicit ledger and not a filter.</b> v2 legitimately differs from v1 in ways that
/// have nothing to do with regression: it runs an agent v1 never had, it has no Docker because
/// decision 40 took it out, it carries none of the packages a guide told a human to install by
/// hand. Those differences will be in every diff, for the life of the fleet. Without somewhere to
/// record why, the diff stabilises at a few hundred lines nobody reads, and the one line that
/// matters arrives in the middle of them.
/// </para>
/// <para>
/// <b>An entry is an explanation, not a suppression.</b> Every explained difference is still
/// reported, still counted, and still carries its reason and its authority into the artifact — it
/// simply does not fail parity. That is what lets the finding count converge to zero and stay
/// there while the diff itself stays honest about how big it is.
/// </para>
/// <para>
/// <b>An entry that matches nothing is reported.</b> A stale entry is an excuse sitting ready for
/// a real regression that happens to look the same, and the only moment anybody would notice is
/// the run where it stops matching — which is this one.
/// </para>
/// </remarks>
public sealed record ExpectedDifferenceLedger
{
    /// <summary>The file, beside the tool that reads it.</summary>
    public const string RelativePath = "tools/FrameLink.Parity/expected-differences.json";

    /// <summary>The prose header, carried verbatim so the file explains itself where it lives.</summary>
    [JsonPropertyName("$comment")]
    public IReadOnlyList<string> Comment { get => field; init => field = value ?? []; } = [];

    /// <summary>Every recorded difference, in no significant order.</summary>
    public IReadOnlyList<ExpectedDifference> Entries { get => field; init => field = value ?? []; } = [];

    /// <summary>Reads the ledger from a repository root.</summary>
    /// <exception cref="JsonException">The file is not a ledger.</exception>
    public static ExpectedDifferenceLedger Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var path = Path.Combine(repositoryRoot, "tools", "FrameLink.Parity", "expected-differences.json");
        return JsonSerializer.Deserialize(File.ReadAllText(path), ParityJson.Default.ExpectedDifferenceLedger)
            ?? throw new JsonException($"{path} deserialised to nothing at all.");
    }

    /// <summary>Everything wrong with this ledger, in the order a person would fix it.</summary>
    /// <remarks>
    /// Checked before any comparison runs, because a ledger with a typo in a facet id would
    /// silently explain nothing and the run would look like a pile of regressions.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                problems.Add("An entry has no id.");
                continue;
            }

            if (!seen.Add(entry.Id))
            {
                problems.Add($"{entry.Id}: two entries share this id.");
            }

            problems.AddRange(entry.Problems());
        }

        return problems;
    }
}

/// <summary>One recorded reason a v2 frame may differ from v1.</summary>
public sealed record ExpectedDifference
{
    /// <summary>Stable identifier, quoted in the report beside every difference it explains.</summary>
    public required string Id { get; init; }

    /// <summary>The facet this applies to. <c>*</c> for every facet.</summary>
    public required string Facet { get; init; }

    /// <summary>Which kinds of difference it explains. One or more of <see cref="ParityDifferenceKinds"/>.</summary>
    public IReadOnlyList<string> Kinds { get => field; init => field = value ?? []; } = [];

    /// <summary>Exact keys it covers.</summary>
    public IReadOnlyList<string> Keys { get => field; init => field = value ?? []; } = [];

    /// <summary>Key prefixes it covers.</summary>
    public IReadOnlyList<string> KeyPrefixes { get => field; init => field = value ?? []; } = [];

    /// <summary>Substrings a key may contain for this entry to cover it.</summary>
    public IReadOnlyList<string> KeyContains { get => field; init => field = value ?? []; } = [];

    /// <summary>
    /// Every key of the facet, for a facet that is expected to be absent whole.
    /// </summary>
    /// <remarks>
    /// Required to be explicit rather than inferred from an entry with no key matcher, because
    /// "cover everything" is exactly the mistake a typo in a key list makes silently.
    /// </remarks>
    public bool AllKeys { get; init; }

    /// <summary>Why this difference is legitimate. The whole point of the file.</summary>
    public required string Reason { get; init; }

    /// <summary>What decided it — a decision number, a section, a measurement.</summary>
    public required string Authority { get; init; }

    /// <summary>When it was recorded.</summary>
    public DateOnly RecordedUtc { get; init; }

    /// <summary>Everything wrong with this entry.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (!string.Equals(Facet, "*", StringComparison.Ordinal) && ParityFacets.Find(Facet) is null)
        {
            problems.Add($"{Id}: there is no facet '{Facet}'.");
        }

        if (Kinds.Count == 0)
        {
            problems.Add($"{Id}: no kinds. An entry that names no kind explains nothing.");
        }

        foreach (var kind in Kinds.Where(kind => !ParityDifferenceKinds.All.Contains(kind, StringComparer.Ordinal)))
        {
            problems.Add($"{Id}: '{kind}' is not a difference kind.");
        }

        if (!AllKeys && Keys.Count == 0 && KeyPrefixes.Count == 0 && KeyContains.Count == 0)
        {
            problems.Add($"{Id}: no key matcher. Set allKeys when the whole facet is expected to differ.");
        }

        if (AllKeys && (Keys.Count > 0 || KeyPrefixes.Count > 0 || KeyContains.Count > 0))
        {
            problems.Add($"{Id}: allKeys and a key matcher together. One of them is not doing anything.");
        }

        if (Reason.Length < 40)
        {
            problems.Add($"{Id}: the reason is too short to be one. Say why this difference is correct.");
        }

        if (string.IsNullOrWhiteSpace(Authority))
        {
            problems.Add($"{Id}: no authority. Name the decision, section or measurement behind it.");
        }

        if (RecordedUtc == default)
        {
            problems.Add($"{Id}: no recordedUtc.");
        }

        return problems;
    }

    /// <summary>Whether this entry explains that difference.</summary>
    public bool Covers(ParityDifference difference)
    {
        ArgumentNullException.ThrowIfNull(difference);

        if (!string.Equals(Facet, "*", StringComparison.Ordinal)
            && !string.Equals(Facet, difference.Facet, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Kinds.Contains(difference.Kind, StringComparer.Ordinal))
        {
            return false;
        }

        return AllKeys
            || Keys.Contains(difference.Key, StringComparer.Ordinal)
            || KeyPrefixes.Any(prefix => difference.Key.StartsWith(prefix, StringComparison.Ordinal))
            || KeyContains.Any(fragment => difference.Key.Contains(fragment, StringComparison.Ordinal));
    }
}
