using System.Globalization;
using System.Text.Json;

namespace FrameLink.Parity;

/// <summary>
/// The parity harness's command line — <c>probes</c>, <c>judge</c>, <c>coverage</c>, <c>ledger</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here touches a frame.</b> Reaching the frame is <c>tools/harness/fl.py parity</c>'s
/// job, over paramiko, per CLAUDE.md §1.3 — this reads an observation somebody already collected
/// and says what it means. The split is what lets the entire verdict path run inside the ordinary
/// test suite with no hardware, and it is the same split
/// <c>tools/FrameLink.Upstream</c> uses for the same reason.
/// </para>
/// <para>
/// <b>Exit codes are the verdict.</b> 0 at parity, 2 differences found, 3 the comparison could not
/// be completed, 1 this tool was used wrongly. A harness that returned 0 for "I could not look"
/// would be worse than no harness.
/// </para>
/// </remarks>
internal static class Program
{
    private const int Ok = 0;
    private const int Usage = 1;
    private const int Differs = 2;
    private const int Incomplete = 3;

    private static int Main(string[] arguments)
    {
        string root;
        try
        {
            root = LocateRepositoryRoot(AppContext.BaseDirectory);
        }
        catch (DirectoryNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return Usage;
        }

        return (arguments.Length == 0 ? string.Empty : arguments[0]) switch
        {
            "probes" => Probes(),
            "judge" => Judge(root, arguments),
            "coverage" => Coverage(root),
            "ledger" => Ledger(root),
            _ => Help(),
        };
    }

    /// <summary>Walks up from <paramref name="start"/> to the directory holding the solution.</summary>
    public static string LocateRepositoryRoot(string start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(start);

        var probe = new DirectoryInfo(start);
        for (var depth = 0; depth < 10 && probe is not null; depth++, probe = probe.Parent)
        {
            if (File.Exists(Path.Combine(probe.FullName, "FrameLink.slnx")))
            {
                return probe.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No FrameLink.slnx at or above {start}.");
    }

    /// <summary>The probe list, for whoever is about to reach a frame.</summary>
    private static int Probes()
    {
        var list = new ParityProbeList
        {
            Probes =
            [
                .. ParityFacets.All
                    .Where(facet => facet.Probe is not null)
                    .Select(facet => new ParityProbe
                    {
                        Facet = facet.Id,
                        Section = facet.Section,
                        Title = facet.Title,
                        Command = facet.Probe!,
                        Elevated = facet.Elevated,
                    }),
            ],
        };

        Console.WriteLine(JsonSerializer.Serialize(list, ParityJson.Default.ParityProbeList));
        return Ok;
    }

    private static int Judge(string root, string[] arguments)
    {
        var options = ParseOptions(arguments.AsSpan(1));
        if (options is null)
        {
            Console.Error.WriteLine("Every option takes a value: --observed, --out.");
            return Usage;
        }

        if (!options.TryGetValue("observed", out var observedPath) || string.IsNullOrWhiteSpace(observedPath))
        {
            Console.Error.WriteLine("judge needs --observed <path to the collector's JSON>.");
            return Usage;
        }

        ExpectedDifferenceLedger ledger;
        ParityObservationSet collection;
        IReadOnlyDictionary<string, string> reference;
        IReadOnlyList<string> resources;

        try
        {
            ledger = ExpectedDifferenceLedger.Load(root);
            reference = ReferenceInventory.Load(root);
            resources = CatalogDocument.Ids(root);
            collection = JsonSerializer.Deserialize(
                File.ReadAllText(observedPath), ParityJson.Default.ParityObservationSet)
                ?? throw new JsonException($"{observedPath} deserialised to nothing at all.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"Cannot judge: {exception.Message}");
            return Usage;
        }

        var problems = ledger.Problems();
        if (problems.Count > 0)
        {
            Console.Error.WriteLine($"{ExpectedDifferenceLedger.RelativePath} is not sound:");
            foreach (var problem in problems)
            {
                Console.Error.WriteLine($"  {problem}");
            }

            return Usage;
        }

        var report = ParityJudge.Judge(reference, collection, ledger, resources, DateTimeOffset.UtcNow);
        var text = ParitySummary.Render(report);
        Console.Write(text);

        if (options.TryGetValue("out", out var directory) && !string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            var json = Path.Combine(directory, "parity.json");
            var summary = Path.Combine(directory, "parity.txt");
            File.WriteAllText(json, JsonSerializer.Serialize(report, ParityJson.Default.ParityReport) + "\n");
            File.WriteAllText(summary, text);
            Console.WriteLine();
            Console.WriteLine($"wrote {json}");
            Console.WriteLine($"wrote {summary}");
        }

        return report.Outcome switch
        {
            ParityOutcomes.Parity => Ok,
            ParityOutcomes.Differs => Differs,
            _ => Incomplete,
        };
    }

    /// <summary>What a state diff can and cannot answer, with no frame involved.</summary>
    private static int Coverage(string root)
    {
        IReadOnlyDictionary<string, string> reference;
        IReadOnlyList<string> resources;

        try
        {
            reference = ReferenceInventory.Load(root);
            resources = CatalogDocument.Ids(root);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"Cannot read the reference: {exception.Message}");
            return Usage;
        }

        var evidence = CatalogEvidenceMap.For(resources);
        var without = evidence.Where(item => item.Facet is null).ToList();

        Console.WriteLine("What the parity state diff covers");
        Console.WriteLine("=================================");
        Console.WriteLine();
        Console.WriteLine(
            $"inventory sections   {Count(reference.Count)} captured, "
            + $"{Count(ParityFacets.All.Count(facet => facet.IsCovered))} compared, "
            + $"{Count(ParityFacets.All.Count(facet => !facet.IsCovered))} declared uncovered");
        Console.WriteLine(
            $"catalog resources    {Count(resources.Count)} enumerated, "
            + $"{Count(evidence.Count - without.Count)} with v1 state to compare, "
            + $"{Count(without.Count)} without");
        Console.WriteLine();

        Console.WriteLine("Sections declared uncovered");
        Console.WriteLine("---------------------------");
        foreach (var facet in ParityFacets.All.Where(facet => !facet.IsCovered))
        {
            Console.WriteLine($"  {facet.Id}  ({facet.Section})");
            Console.WriteLine($"      {facet.Limitation}");
        }

        Console.WriteLine();
        Console.WriteLine("Sections compared with something out of scope");
        Console.WriteLine("---------------------------------------------");
        foreach (var facet in ParityFacets.All.Where(facet =>
            facet.IsCovered && string.Equals(facet.Coverage, FacetCoverage.Partial, StringComparison.Ordinal)))
        {
            Console.WriteLine($"  {facet.Id}  ({facet.Section})");
            Console.WriteLine($"      {facet.Limitation}");
        }

        Console.WriteLine();
        Console.WriteLine("Catalog resources a state diff can never verify");
        Console.WriteLine("-----------------------------------------------");
        foreach (var resource in without)
        {
            Console.WriteLine($"  {resource.Resource}");
            Console.WriteLine($"      {resource.Note}");
        }

        return Ok;
    }

    private static int Ledger(string root)
    {
        ExpectedDifferenceLedger ledger;
        try
        {
            ledger = ExpectedDifferenceLedger.Load(root);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Console.Error.WriteLine($"Cannot read the ledger: {exception.Message}");
            return Usage;
        }

        var problems = ledger.Problems();
        Console.WriteLine($"{Count(ledger.Entries.Count)} recorded expected differences.");
        Console.WriteLine();

        foreach (var entry in ledger.Entries)
        {
            Console.WriteLine($"  {entry.Id}   [{entry.Facet}]  {string.Join('/', entry.Kinds)}");
            Console.WriteLine($"      {entry.Reason}");
            Console.WriteLine($"      authority: {entry.Authority}");
        }

        if (problems.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("The ledger is sound.");
            return Ok;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("The ledger is not sound:");
        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"  {problem}");
        }

        return Usage;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

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
        Console.WriteLine("The FrameLink parity harness (version2.md milestone Mn+3).");
        Console.WriteLine();
        Console.WriteLine("  probes");
        Console.WriteLine("      Emit the read-only observation commands as JSON. This is what");
        Console.WriteLine("      `tools/harness/fl.py parity` runs on the frame.");
        Console.WriteLine();
        Console.WriteLine("  judge --observed <path> [--out <directory>]");
        Console.WriteLine("      Compare a collected observation against reference/v1-state-inventory.txt");
        Console.WriteLine("      and the expected-difference ledger. Exit 0 at parity, 2 differences,");
        Console.WriteLine("      3 the comparison could not be completed.");
        Console.WriteLine();
        Console.WriteLine("  coverage");
        Console.WriteLine("      What a state diff can and cannot answer. Needs no frame.");
        Console.WriteLine();
        Console.WriteLine("  ledger");
        Console.WriteLine("      List the recorded expected differences and check the file is sound.");
        Console.WriteLine();
        Console.WriteLine("Nothing here reaches a frame, and no build invokes any of it.");
        return Usage;
    }
}
