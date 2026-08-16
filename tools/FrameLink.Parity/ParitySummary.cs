using System.Globalization;
using System.Text;

namespace FrameLink.Parity;

/// <summary>
/// The report as a person reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written against one failure mode.</b> A parity diff that prints four hundred undifferentiated
/// lines proves nothing and will never be read a second time, so this puts the verdict and the
/// unexplained differences first, states every count beside its cap, and pushes the explained and
/// tolerated differences into totals that name their reasons rather than repeating them per line.
/// </para>
/// <para>
/// <b>Coverage is part of the summary, not an appendix.</b> Where the harness cannot see is a
/// property of the answer it just gave, and a reader who takes "no findings" away without also
/// taking "and these eleven resources have no v1 state to compare against" has been misled by an
/// accurate report.
/// </para>
/// </remarks>
public static class ParitySummary
{
    /// <summary>How many differences of one verdict a facet prints before it starts counting.</summary>
    public const int MaxPerFacet = 25;

    /// <summary>Renders the whole report.</summary>
    public static string Render(ParityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();

        text.Append("FrameLink parity report\n");
        text.Append("=======================\n\n");
        text.Append("verdict     ").Append(report.Outcome.ToUpperInvariant()).Append('\n');
        text.Append("frame       ").Append(report.Host ?? "(not recorded)").Append('\n');
        text.Append("observed    ").Append(Stamp(report.CollectedUtc)).Append('\n');
        text.Append("judged      ").Append(Stamp(report.GeneratedUtc)).Append('\n');
        text.Append("collector   ").Append(report.Collector ?? "(not recorded)").Append("\n\n");
        text.Append(Wrap(report.Summary, 96)).Append("\n\n");

        text.Append("counts\n------\n");
        text.Append("  findings           ").Append(Number(report.Findings))
            .Append("   unexplained differences; parity fails while any exist\n");
        text.Append("  expected           ").Append(Number(report.Expected))
            .Append("   explained by the ledger, with a reason each\n");
        text.Append("  tolerated          ").Append(Number(report.Tolerated))
            .Append("   package versions ahead of the v1 baseline\n");
        text.Append("  unresolved facets  ").Append(Number(report.Unresolved))
            .Append("   a probe failed or was never run\n");
        text.Append("  ledger entries     ").Append(Number(report.LedgerEntries))
            .Append("   of which ").Append(Number(report.LedgerEntriesUsed)).Append(" matched something\n\n");

        Findings(text, report);
        Facets(text, report);
        Coverage(text, report);
        Stale(text, report);

        return text.ToString();
    }

    private static void Findings(StringBuilder text, ParityReport report)
    {
        text.Append("findings\n--------\n");

        var any = false;
        foreach (var facet in report.Facets.Where(facet => facet.Findings > 0))
        {
            any = true;
            text.Append("  ").Append(facet.Facet).Append("  (").Append(Number(facet.Findings)).Append(")\n");

            var shown = 0;
            foreach (var difference in facet.Differences.Where(difference =>
                string.Equals(difference.Verdict, ParityVerdicts.Finding, StringComparison.Ordinal)))
            {
                if (shown++ == MaxPerFacet)
                {
                    text.Append("      ... ")
                        .Append(Number(facet.Findings - MaxPerFacet))
                        .Append(" more, in the JSON artifact\n");
                    break;
                }

                text.Append("      ").Append(Describe(difference)).Append('\n');
                foreach (var line in difference.Detail)
                {
                    text.Append("          ").Append(line).Append('\n');
                }
            }
        }

        if (!any)
        {
            text.Append("  none. Every difference is explained or tolerated.\n");
        }

        text.Append('\n');
    }

    private static void Facets(StringBuilder text, ParityReport report)
    {
        text.Append("facets\n------\n");

        foreach (var facet in report.Facets)
        {
            var state = string.Equals(facet.State, FacetStates.Compared, StringComparison.Ordinal)
                ? $"{Number(facet.Findings)} findings, {Number(facet.Expected)} expected, "
                  + $"{Number(facet.Tolerated)} tolerated  ({Number(facet.ReferenceKeys)} reference keys)"
                : facet.State;

            text.Append("  ").Append(facet.Facet.PadRight(28)).Append(state).Append('\n');

            if (!string.IsNullOrWhiteSpace(facet.Limitation)
                && !string.Equals(facet.State, FacetStates.Compared, StringComparison.Ordinal))
            {
                text.Append("      ").Append(Wrap(facet.Limitation, 88, "      ")).Append('\n');
            }
        }

        text.Append('\n');
    }

    private static void Coverage(StringBuilder text, ParityReport report)
    {
        text.Append("coverage — what this comparison provably cannot answer\n");
        text.Append("-----------------------------------------------------\n");

        text.Append("  inventory sections not compared at all: ")
            .Append(Number(report.Coverage.UncoveredSections.Count)).Append('\n');
        foreach (var facet in report.Coverage.UncoveredSections)
        {
            text.Append("      ").Append(facet.Facet).Append('\n');
            text.Append("      ").Append(Wrap(facet.Limitation ?? string.Empty, 88, "      ")).Append('\n');
        }

        text.Append("\n  inventory sections compared with something out of scope: ")
            .Append(Number(report.Coverage.PartialSections.Count)).Append('\n');
        foreach (var facet in report.Coverage.PartialSections)
        {
            text.Append("      ").Append(facet.Facet).Append('\n');
            text.Append("      ").Append(Wrap(facet.Limitation ?? string.Empty, 88, "      ")).Append('\n');
        }

        text.Append("\n  keys parsed and deliberately dropped: ")
            .Append(Number(report.Coverage.IgnoredKeys.Count)).Append('\n');
        foreach (var (key, reason) in report.Coverage.IgnoredKeys)
        {
            text.Append("      ").Append(key).Append('\n');
            text.Append("      ").Append(Wrap(reason, 88, "      ")).Append('\n');
        }

        text.Append("\n  catalog resources with no v1 state to compare against: ")
            .Append(Number(report.Coverage.ResourcesWithoutReference.Count))
            .Append(" of ").Append(Number(report.Coverage.CatalogResources)).Append('\n');
        text.Append("      These cannot be verified by a state diff at all. They are what the other two\n");
        text.Append("      bars of the triple bar are for: the checkpoint assertion each resource carries\n");
        text.Append("      as its own Verify, and the validation battery on the mule.\n");

        foreach (var resource in report.Coverage.ResourcesWithoutReference)
        {
            text.Append("      ").Append(resource.Resource).Append('\n');
        }

        text.Append('\n');
    }

    private static void Stale(StringBuilder text, ParityReport report)
    {
        if (report.StaleLedgerEntries.Count == 0)
        {
            return;
        }

        text.Append("stale ledger entries\n--------------------\n");
        text.Append("  These recorded an expected difference that this frame no longer has. That is the\n");
        text.Append("  direction everything here is trying to move in — but an entry nobody removes is an\n");
        text.Append("  excuse sitting ready for a regression that happens to look the same. Delete them.\n");

        foreach (var entry in report.StaleLedgerEntries)
        {
            text.Append("      ").Append(entry).Append('\n');
        }

        text.Append('\n');
    }

    private static string Describe(ParityDifference difference) => difference.Kind switch
    {
        ParityDifferenceKinds.Missing => $"missing   {difference.Key}   v1 had {Value(difference.Reference)}",
        ParityDifferenceKinds.Extra => $"extra     {difference.Key}   this frame has {Value(difference.Observed)}",
        ParityDifferenceKinds.Behind =>
            $"BEHIND    {difference.Key}   {Value(difference.Observed)} < v1 {Value(difference.Reference)}",
        ParityDifferenceKinds.Ahead =>
            $"ahead     {difference.Key}   {Value(difference.Observed)} > v1 {Value(difference.Reference)}",
        _ => $"changed   {difference.Key}   v1 {Value(difference.Reference)} -> {Value(difference.Observed)}",
    };

    /// <summary>
    /// One line of a value, with a whole file reduced to its size rather than pasted.
    /// </summary>
    private static string Value(string? value)
    {
        if (value is null)
        {
            return "(absent)";
        }

        var single = value.Replace('\n', '⏎');
        return single.Length <= 72
            ? single
            : single[..72] + $"... ({Number(value.Length)} chars)";
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Stamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        ?? "(not recorded)";

    /// <summary>Hard-wraps prose so a terminal shows it whole.</summary>
    private static string Wrap(string text, int width, string indent = "")
    {
        var lines = new List<string>();
        var line = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return string.Join("\n" + indent, lines);
    }
}
