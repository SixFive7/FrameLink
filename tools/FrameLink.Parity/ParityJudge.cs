using System.Globalization;
using FrameLink.Control;
using FrameLink.Protocol;

namespace FrameLink.Parity;

/// <summary>
/// The comparison: one frame's observation against the frozen v1 reference, facet by facet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Packages are not compared here.</b> They are handed to <see cref="PackageDrift"/>, which is
/// the code the Fleet Manager already runs against the same 929-package v1 baseline on every
/// inventory report (decision 55). Reimplementing Debian version ordering for the parity harness
/// would produce a second answer to "is this newer", and the two would eventually disagree about
/// some frame in a way nothing would catch — which is precisely the situation decision 55 exists
/// to prevent.
/// </para>
/// <para>
/// <b>Everything else runs through one differ over key/value maps</b>, so there is exactly one
/// place where missing, extra and changed are decided, and one place the ledger is consulted.
/// </para>
/// </remarks>
public static class ParityJudge
{
    /// <summary>How many line-level details a changed file carries into the artifact.</summary>
    /// <remarks>
    /// A rewritten unit file would otherwise attach eighty lines to one difference and turn the
    /// report into the data dump §7.4 refuses. The uncapped total is always stated beside the
    /// capped list, which is the difference between a summary and a truncation.
    /// </remarks>
    public const int MaxDetailLines = 12;

    /// <summary>Judges one collection against the reference and the ledger.</summary>
    public static ParityReport Judge(
        IReadOnlyDictionary<string, string> reference,
        ParityObservationSet collection,
        ExpectedDifferenceLedger ledger,
        IReadOnlyList<string> catalogResources,
        DateTimeOffset generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(catalogResources);

        var observations = collection.Observations.ToDictionary(
            observation => observation.Facet,
            StringComparer.Ordinal);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<ParityFacetResult>();

        foreach (var facet in ParityFacets.All)
        {
            results.Add(JudgeFacet(facet, reference, observations, ledger, used));
        }

        var findings = results.Sum(result => result.Findings);
        var expected = results.Sum(result => result.Expected);
        var tolerated = results.Sum(result => result.Tolerated);
        var unresolved = results.Count(result =>
            string.Equals(result.State, FacetStates.ProbeFailed, StringComparison.Ordinal)
            || string.Equals(result.State, FacetStates.NotCollected, StringComparison.Ordinal));

        var outcome = findings > 0
            ? ParityOutcomes.Differs
            : unresolved > 0 ? ParityOutcomes.Incomplete : ParityOutcomes.Parity;

        var evidence = CatalogEvidenceMap.For(catalogResources);

        return new ParityReport
        {
            GeneratedUtc = generatedUtc,
            Collector = collection.Collector,
            Host = collection.Host,
            CollectedUtc = collection.CollectedUtc,
            Outcome = outcome,
            Summary = Summarise(outcome, findings, expected, tolerated, results),
            Findings = findings,
            Expected = expected,
            Tolerated = tolerated,
            Unresolved = unresolved,
            Facets = results,
            Coverage = new ParityCoverageReport
            {
                UncoveredSections = [.. results.Where(result =>
                    string.Equals(result.Coverage, FacetCoverage.None, StringComparison.Ordinal))],
                PartialSections = [.. results.Where(result =>
                    string.Equals(result.Coverage, FacetCoverage.Partial, StringComparison.Ordinal))],
                IgnoredKeys = IgnoredKeys(),
                ResourcesWithoutReference = [.. evidence.Where(item => item.Facet is null)],
                ResourcesWithReference = [.. evidence.Where(item => item.Facet is not null)],
                CatalogResources = evidence.Count,
            },
            StaleLedgerEntries =
                [.. ledger.Entries.Select(entry => entry.Id).Where(id => !used.Contains(id)).Order(StringComparer.Ordinal)],
            LedgerEntries = ledger.Entries.Count,
            LedgerEntriesUsed = used.Count,
        };
    }

    private static ParityFacetResult JudgeFacet(
        ParityFacet facet,
        IReadOnlyDictionary<string, string> reference,
        Dictionary<string, ParityObservation> observations,
        ExpectedDifferenceLedger ledger,
        HashSet<string> used)
    {
        if (!facet.IsCovered)
        {
            return Bare(facet, FacetStates.Uncovered, facet.Limitation);
        }

        if (!observations.TryGetValue(facet.Id, out var observation))
        {
            return Bare(
                facet,
                FacetStates.NotCollected,
                "The collection carries no observation for this facet. "
                + (facet.Elevated
                    ? "It needs root, so an unprivileged run skips it — pass --elevate to collect it."
                    : "Whatever ran did not attempt it."));
        }

        if (!string.IsNullOrWhiteSpace(observation.Skipped))
        {
            return Bare(facet, FacetStates.NotCollected, observation.Skipped);
        }

        if (observation.ExitStatus != 0)
        {
            var complaint = (observation.Stderr.Length > 0 ? observation.Stderr : observation.Stdout).Trim();
            return Bare(
                facet,
                FacetStates.ProbeFailed,
                $"The probe exited {observation.ExitStatus.ToString(CultureInfo.InvariantCulture)}: "
                + (complaint.Length > 0 ? complaint : "(no output)"));
        }

        var referenceText = reference.TryGetValue(facet.Section, out var block) ? block : null;
        var left = FacetParser.Parse(facet, referenceText);
        var right = FacetParser.Parse(facet, observation.Stdout);

        var differences = string.Equals(facet.Kind, FacetKinds.Packages, StringComparison.Ordinal)
            ? PackageDifferences(right)
            : Differences(facet, left, right);

        differences = [.. differences.Select(difference => Explain(difference, ledger, used))];
        differences = [.. differences.OrderBy(Severity).ThenBy(difference => difference.Key, StringComparer.Ordinal)];

        return new ParityFacetResult
        {
            Facet = facet.Id,
            Title = facet.Title,
            State = FacetStates.Compared,
            Coverage = facet.Coverage,
            Limitation = facet.Limitation,
            ReferenceKeys = left.Count,
            ObservedKeys = right.Count,
            Findings = differences.Count(difference =>
                string.Equals(difference.Verdict, ParityVerdicts.Finding, StringComparison.Ordinal)),
            Expected = differences.Count(difference =>
                string.Equals(difference.Verdict, ParityVerdicts.Expected, StringComparison.Ordinal)),
            Tolerated = differences.Count(difference =>
                string.Equals(difference.Verdict, ParityVerdicts.Tolerated, StringComparison.Ordinal)),
            Differences = differences,
        };
    }

    private static ParityFacetResult Bare(ParityFacet facet, string state, string? limitation) => new()
    {
        Facet = facet.Id,
        Title = facet.Title,
        State = state,
        Coverage = facet.Coverage,
        Limitation = limitation,
    };

    /// <summary>The generic differ: two maps in, three kinds of difference out.</summary>
    private static List<ParityDifference> Differences(
        ParityFacet facet,
        IReadOnlyDictionary<string, string> reference,
        IReadOnlyDictionary<string, string> observed)
    {
        var differences = new List<ParityDifference>();

        foreach (var (key, expected) in reference)
        {
            if (!observed.TryGetValue(key, out var actual))
            {
                differences.Add(new ParityDifference
                {
                    Facet = facet.Id,
                    Kind = ParityDifferenceKinds.Missing,
                    Key = key,
                    Reference = expected,
                });

                continue;
            }

            if (string.Equals(expected, actual, StringComparison.Ordinal))
            {
                continue;
            }

            var version = facet.VersionKeys.Contains(key, StringComparer.Ordinal);
            var order = version ? DebianVersion.Compare(actual, expected) : 0;

            differences.Add(new ParityDifference
            {
                Facet = facet.Id,
                Kind = version
                    ? order > 0 ? ParityDifferenceKinds.Ahead : ParityDifferenceKinds.Behind
                    : ParityDifferenceKinds.Changed,
                Key = key,
                Reference = expected,
                Observed = actual,
                Detail = string.Equals(facet.Kind, FacetKinds.FileSet, StringComparison.Ordinal)
                    ? LineDelta(expected, actual)
                    : [],
            });
        }

        foreach (var (key, actual) in observed)
        {
            if (!reference.ContainsKey(key))
            {
                differences.Add(new ParityDifference
                {
                    Facet = facet.Id,
                    Kind = ParityDifferenceKinds.Extra,
                    Key = key,
                    Observed = actual,
                });
            }
        }

        return differences;
    }

    /// <summary>Packages, straight through the Fleet Manager's own drift computation.</summary>
    private static List<ParityDifference> PackageDifferences(IReadOnlyDictionary<string, string> installed) =>
    [
        .. PackageDrift.AgainstBaseline(installed, limit: 0).Select(delta => new ParityDifference
        {
            Facet = "packages",
            Kind = delta.Status switch
            {
                PackageDrift.StatusMissing => ParityDifferenceKinds.Missing,
                PackageDrift.StatusExtra => ParityDifferenceKinds.Extra,
                PackageDrift.StatusAhead => ParityDifferenceKinds.Ahead,
                _ => ParityDifferenceKinds.Behind,
            },
            Key = delta.Package,
            Reference = delta.Baseline,
            Observed = delta.Installed,
        }),
    ];

    /// <summary>
    /// Which lines one side has and the other does not, worst first, capped and honest about it.
    /// </summary>
    /// <remarks>
    /// A set difference rather than a longest-common-subsequence diff, deliberately: what a reader
    /// needs from a changed unit file is which directive appeared and which went away, and an LCS
    /// would spend its output on where they moved to.
    /// </remarks>
    private static List<string> LineDelta(string reference, string observed)
    {
        var left = reference.Split('\n');
        var right = observed.Split('\n');
        var gone = left.Except(right, StringComparer.Ordinal).ToList();
        var added = right.Except(left, StringComparer.Ordinal).ToList();

        var detail = new List<string>();
        detail.AddRange(gone.Take(MaxDetailLines).Select(line => "- " + line));
        detail.AddRange(added.Take(MaxDetailLines).Select(line => "+ " + line));

        var hidden = Math.Max(0, gone.Count - MaxDetailLines) + Math.Max(0, added.Count - MaxDetailLines);
        if (hidden > 0)
        {
            detail.Add($"... {hidden.ToString(CultureInfo.InvariantCulture)} further changed lines not shown");
        }

        return detail;
    }

    /// <summary>Assigns the verdict: the ledger first, then the one standing tolerance.</summary>
    private static ParityDifference Explain(
        ParityDifference difference,
        ExpectedDifferenceLedger ledger,
        HashSet<string> used)
    {
        var entry = ledger.Entries.FirstOrDefault(candidate => candidate.Covers(difference));
        if (entry is not null)
        {
            used.Add(entry.Id);
            return difference with
            {
                Verdict = ParityVerdicts.Expected,
                Reason = entry.Reason,
                LedgerEntry = entry.Id,
            };
        }

        if (string.Equals(difference.Kind, ParityDifferenceKinds.Ahead, StringComparison.Ordinal))
        {
            return difference with
            {
                Verdict = ParityVerdicts.Tolerated,
                Reason =
                    "A version that moved forward. Decision 55 leaves a frame behind NAT taking Debian's "
                    + "security-only automatic updates and reports the movement rather than acting on it: "
                    + "the reviewed version is a floor, not a pin. This is the only drift this project "
                    + "tolerates.",
            };
        }

        return difference;
    }

    private static int Severity(ParityDifference difference)
    {
        var verdict = difference.Verdict switch
        {
            ParityVerdicts.Finding => 0,
            ParityVerdicts.Tolerated => 100,
            _ => 200,
        };

        return verdict + difference.Kind switch
        {
            ParityDifferenceKinds.Behind => 0,
            ParityDifferenceKinds.Missing => 1,
            ParityDifferenceKinds.Changed => 2,
            ParityDifferenceKinds.Extra => 3,
            _ => 4,
        };
    }

    private static Dictionary<string, string> IgnoredKeys()
    {
        var ignored = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var facet in ParityFacets.All)
        {
            foreach (var (key, reason) in facet.IgnoredKeys)
            {
                ignored[$"{facet.Id}.{key}"] = reason;
            }
        }

        return ignored;
    }

    private static string Summarise(
        string outcome,
        int findings,
        int expected,
        int tolerated,
        IReadOnlyList<ParityFacetResult> results)
    {
        var unresolved = results
            .Where(result =>
                string.Equals(result.State, FacetStates.ProbeFailed, StringComparison.Ordinal)
                || string.Equals(result.State, FacetStates.NotCollected, StringComparison.Ordinal))
            .Select(result => result.Facet)
            .ToList();

        var counts =
            $"{findings.ToString(CultureInfo.InvariantCulture)} unexplained, "
            + $"{expected.ToString(CultureInfo.InvariantCulture)} explained by the ledger, "
            + $"{tolerated.ToString(CultureInfo.InvariantCulture)} tolerated version drift.";

        return outcome switch
        {
            ParityOutcomes.Parity =>
                "At parity. Every difference from the frozen v1 reference is either recorded in the "
                + "expected-difference ledger with a reason, or is a package version that has moved "
                + "forward under a security update. " + counts,
            ParityOutcomes.Differs =>
                "Not at parity. " + counts + " Nothing here is a verdict on whether the frame works — "
                + "it is a statement that it does not mechanically equal the frozen v1 reference, and "
                + "each unexplained difference is either a gap to close or a reason to record.",
            _ =>
                "Incomplete. The comparison could not be finished, so no parity claim is made either "
                + "way: " + string.Join(", ", unresolved) + " was not compared. " + counts,
        };
    }
}
