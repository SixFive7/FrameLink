using System.Globalization;
using System.Text.Json;

namespace FrameLink.Upstream;

/// <summary>
/// The upstream review ledger's command line — <c>check</c> and <c>review</c> (§7.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two verbs, because a gate with no cheap way to satisfy it is a gate people route around.</b>
/// <c>check</c> is the detection the operator asked for: it says what has moved since the last
/// review and prints, for each one, the exact <c>review</c> command that records the decision.
/// <c>review</c> writes it. Re-pinning deliberately and upgrading after validation are the same
/// command, because in both cases what a person is recording is the upstream version they looked
/// at.
/// </para>
/// <para>
/// <b>Nothing runs this except a person cutting a release.</b> No build target invokes it, no
/// test in the suite reaches the network through it, and its exit code is meaningful only to
/// whoever typed it. That is the whole of "detection, not a build failure": an upstream that
/// publishes something overnight changes what this prints tomorrow and interrupts nobody's day.
/// </para>
/// </remarks>
internal static class Program
{
    private const int Ok = 0;
    private const int Usage = 1;
    private const int Moved = 2;
    private const int Unreachable = 3;

    private static async Task<int> Main(string[] arguments)
    {
        string ledgerPath;
        UpstreamLedger ledger;

        try
        {
            var root = UpstreamLedger.LocateRepositoryRoot(AppContext.BaseDirectory);
            ledgerPath = Path.Combine(root, UpstreamLedger.FileName);
            ledger = UpstreamLedger.Load(ledgerPath);
        }
        catch (Exception exception) when (exception is IOException or JsonException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"Cannot read the ledger: {exception.Message}");
            return Usage;
        }

        var problems = ledger.Problems();
        if (problems.Count > 0)
        {
            Console.Error.WriteLine($"{UpstreamLedger.FileName} is not sound:");
            foreach (var problem in problems)
            {
                Console.Error.WriteLine($"  {problem}");
            }

            return Usage;
        }

        return (arguments.Length == 0 ? string.Empty : arguments[0]) switch
        {
            "check" => await CheckAsync(ledger).ConfigureAwait(false),
            "review" => Review(ledger, ledgerPath, arguments),
            _ => Help(),
        };
    }

    private static async Task<int> CheckAsync(UpstreamLedger ledger)
    {
        using var http = UpstreamProbes.CreateClient();
        var probes = new UpstreamProbes(http);
        var findings = new List<UpstreamFinding>();

        Console.WriteLine($"Checking {ledger.Entries.Count} reviewed upstreams.");
        Console.WriteLine();

        foreach (var entry in ledger.Entries)
        {
            var finding = await probes.CheckAsync(entry, CancellationToken.None).ConfigureAwait(false);
            findings.Add(finding);
            Console.WriteLine(Describe(finding));
        }

        Console.WriteLine();
        return Summarise(findings);
    }

    private static string Describe(UpstreamFinding finding)
    {
        var label = finding.State switch
        {
            UpstreamState.Current => "current    ",
            UpstreamState.Moved => "MOVED      ",
            _ => "unreachable",
        };

        var line = $"  {label}  {finding.Entry.Id,-28}  using {finding.Entry.Pinned,-12}"
            + $"  reviewed {finding.Entry.Reviewed.Upstream,-12}";

        line += finding.State switch
        {
            UpstreamState.Moved => $"  upstream now {finding.Latest}",
            UpstreamState.Unreachable => $"  {finding.Failure}",
            _ => string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(finding.Detail) && finding.State is not UpstreamState.Unreachable)
        {
            line += $"  ({finding.Detail})";
        }

        return line.TrimEnd();
    }

    /// <summary>"1 dependency has" / "3 dependencies have", because a report is read by a person.</summary>
    private static string Count(int many, string singular, string plural, string singularVerb, string pluralVerb) =>
        many == 1
            ? $"1 {singular} {singularVerb}"
            : $"{many.ToString(CultureInfo.InvariantCulture)} {plural} {pluralVerb}";

    /// <summary>
    /// The release verdict, and the exact commands that resolve it.
    /// </summary>
    /// <remarks>
    /// Printing the remediation command is the part that keeps this proportionate. The decision a
    /// move needs is a sentence of judgement, and everything around that sentence — which file,
    /// which field, what today's date is — is clerical work this can simply do.
    /// </remarks>
    private static int Summarise(IReadOnlyList<UpstreamFinding> findings)
    {
        var moved = findings.Where(finding => finding.State is UpstreamState.Moved).ToList();
        var unreachable = findings.Where(finding => finding.State is UpstreamState.Unreachable).ToList();

        if (moved.Count == 0 && unreachable.Count == 0)
        {
            Console.WriteLine("The ledger is current. Every reviewed upstream is still serving what was reviewed.");
            return Ok;
        }

        if (unreachable.Count > 0)
        {
            Console.WriteLine(
                Count(unreachable.Count, "upstream", "upstreams", "could not be reached", "could not be reached")
                + ", so the ledger cannot be called current.");
            Console.WriteLine("A release waits for an answer rather than assuming one.");
            Console.WriteLine();
        }

        if (moved.Count > 0)
        {
            Console.WriteLine(
                Count(moved.Count, "dependency", "dependencies", "has moved", "have moved")
                + " since the last review.");
            Console.WriteLine("Before cutting a release, each one is either re-pinned deliberately or upgraded");
            Console.WriteLine("and validated against the suite. Record the decision with:");
            Console.WriteLine();

            foreach (var finding in moved)
            {
                Console.WriteLine(
                    $"  dotnet run --project tools/FrameLink.Upstream -- review {finding.Entry.Id} "
                    + $"--seen {finding.Latest} --note \"...\"");
                Console.WriteLine(
                    $"      add --pinned {finding.Latest} when the upgrade is taken and the suite is green,");
                Console.WriteLine(
                    "      or --verdict held when the pin deliberately stays where it is.");
            }

            Console.WriteLine();
        }

        return moved.Count > 0 ? Moved : Unreachable;
    }

    private static int Review(UpstreamLedger ledger, string ledgerPath, string[] arguments)
    {
        if (arguments.Length < 2)
        {
            Console.Error.WriteLine("review needs an entry id.");
            return Usage;
        }

        var entry = ledger.Find(arguments[1]);
        if (entry is null)
        {
            Console.Error.WriteLine($"There is no entry '{arguments[1]}'. The ledger holds: "
                + string.Join(", ", ledger.Entries.Select(candidate => candidate.Id)));
            return Usage;
        }

        var options = ParseOptions(arguments.AsSpan(2));
        if (options is null)
        {
            Console.Error.WriteLine("Every option after the id takes a value: --seen, --pinned, --verdict, --note.");
            return Usage;
        }

        if (!options.TryGetValue("seen", out var seen) || string.IsNullOrWhiteSpace(seen))
        {
            Console.Error.WriteLine("review needs --seen <version>: what upstream is serving now.");
            return Usage;
        }

        if (!options.TryGetValue("note", out var note) || string.IsNullOrWhiteSpace(note))
        {
            Console.Error.WriteLine("review needs --note \"<why>\". A review with no reason recorded is a timestamp.");
            return Usage;
        }

        var pinned = options.TryGetValue("pinned", out var replacement) && !string.IsNullOrWhiteSpace(replacement)
            ? replacement
            : entry.Pinned;

        var verdict = options.TryGetValue("verdict", out var chosen) && !string.IsNullOrWhiteSpace(chosen)
            ? chosen
            : UpstreamReview.Adopted;

        var updated = entry with
        {
            Pinned = pinned,
            Reviewed = new UpstreamReview
            {
                Utc = DateOnly.FromDateTime(DateTime.UtcNow),
                Upstream = seen,
                Verdict = verdict,
                Note = note,
            },
        };

        var problems = updated.Problems();
        if (problems.Count > 0)
        {
            Console.Error.WriteLine("That review would not be sound:");
            foreach (var problem in problems)
            {
                Console.Error.WriteLine($"  {problem}");
            }

            return Usage;
        }

        ledger.With(updated).Save(ledgerPath);

        Console.WriteLine($"Recorded {updated.Id}: using {updated.Pinned}, upstream {seen}, {verdict}, "
            + updated.Reviewed.Utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".");
        Console.WriteLine($"{UpstreamLedger.FileName} is written. Commit it with whatever source change went with it.");

        return Ok;
    }

    /// <summary>Reads <c>--name value</c> pairs. Null when one has no value.</summary>
    private static Dictionary<string, string>? ParseOptions(ReadOnlySpan<string> arguments)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
            {
                return null;
            }

            options[arguments[index][2..]] = arguments[index + 1];
        }

        return options;
    }

    private static int Help()
    {
        Console.WriteLine("The FrameLink upstream review ledger (version2.md 7.1).");
        Console.WriteLine();
        Console.WriteLine("  check");
        Console.WriteLine("      Ask every reviewed upstream what it is serving now and compare.");
        Console.WriteLine("      Exit 0 current, 2 something moved, 3 an upstream could not be reached.");
        Console.WriteLine();
        Console.WriteLine("  review <id> --seen <version> [--pinned <version>] [--verdict adopted|held] --note \"<why>\"");
        Console.WriteLine("      Record that a human looked. Stamps today and rewrites the ledger.");
        Console.WriteLine();
        Console.WriteLine("Neither is run by any build. A developer build never fails because an upstream moved.");
        return Usage;
    }
}
