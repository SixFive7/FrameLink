namespace FrameLink.Diagram;

/// <summary>
/// The diagram generator's command line — <c>write</c>, <c>check</c>, <c>order</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here reaches a frame or the network.</b> It builds the compiled catalog in-process
/// and renders text from it, which is why it can run on any checkout with no hardware and why the
/// suite can call the same renderer.
/// </para>
/// <para>
/// <b>Exit codes are the verdict</b>, matching <c>tools/FrameLink.Parity</c>: 0 the committed
/// document is current, 1 this tool was used wrongly, 2 the document is stale, 3 the document is
/// missing. A generator that returned 0 for "there was nothing to compare against" would be worse
/// than no generator.
/// </para>
/// </remarks>
internal static class Program
{
    private const int Ok = 0;
    private const int Usage = 1;
    private const int Stale = 2;
    private const int Missing = 3;

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

        return (arguments.Length == 0 ? "write" : arguments[0]) switch
        {
            "write" => Write(root),
            "check" => Check(root),
            "order" => Order(),
            _ => Help(),
        };
    }

    /// <summary>Renders the document and writes it, reporting whether anything changed.</summary>
    private static int Write(string root)
    {
        var path = Path.Combine(root, DiagramDocument.RelativePath);
        var rendered = DiagramDocument.Render();
        var existing = File.Exists(path) ? DiagramDocument.Normalise(File.ReadAllText(path)) : null;

        if (string.Equals(existing, rendered, StringComparison.Ordinal))
        {
            Console.WriteLine($"{DiagramDocument.RelativePath} is already current.");
            return Ok;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // LF, explicitly. .gitattributes normalises the repository to LF on every platform, and a
        // generator that emitted CRLF on Windows would make the freshness check fail for a reason
        // that has nothing to do with the catalog.
        File.WriteAllText(path, rendered, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine(existing is null
            ? $"{DiagramDocument.RelativePath} written."
            : $"{DiagramDocument.RelativePath} updated.");

        return Ok;
    }

    /// <summary>Compares the committed document against a fresh render.</summary>
    private static int Check(string root)
    {
        var path = Path.Combine(root, DiagramDocument.RelativePath);

        if (!File.Exists(path))
        {
            Console.Error.WriteLine(
                $"{DiagramDocument.RelativePath} does not exist. "
                + "Run `dotnet run --project tools/FrameLink.Diagram -- write`.");
            return Missing;
        }

        var rendered = DiagramDocument.Render();
        var committed = DiagramDocument.Normalise(File.ReadAllText(path));

        if (string.Equals(committed, rendered, StringComparison.Ordinal))
        {
            Console.WriteLine($"{DiagramDocument.RelativePath} is current.");
            return Ok;
        }

        Console.Error.WriteLine(
            $"{DiagramDocument.RelativePath} is stale: {DiagramDocument.FirstDifference(committed, rendered)}");
        Console.Error.WriteLine("Run `dotnet run --project tools/FrameLink.Diagram -- write` and commit the result.");
        return Stale;
    }

    /// <summary>Prints the execution order and nothing else, for eyes rather than for a file.</summary>
    private static int Order()
    {
        var model = new CatalogModel(CatalogGraph.Snapshot());

        foreach (var node in model.Nodes)
        {
            Console.WriteLine(node.DependsOn.Count == 0
                ? $"{node.Position,3}  {node.Name}"
                : $"{node.Position,3}  {node.Name}  <- {string.Join(", ", node.DependsOn)}");
        }

        return Ok;
    }

    private static int Help()
    {
        Console.Error.WriteLine("usage: FrameLink.Diagram [write|check|order]");
        Console.Error.WriteLine("  write  render reference/reconcile-dag.md from the compiled catalog (default)");
        Console.Error.WriteLine("  check  compare the committed document against a fresh render; 2 if stale");
        Console.Error.WriteLine("  order  print the execution order to stdout");
        return Usage;
    }

    /// <summary>Walks up from <paramref name="start"/> to the directory holding the solution.</summary>
    internal static string LocateRepositoryRoot(string start)
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

        throw new DirectoryNotFoundException(
            $"No FrameLink.slnx above {start}; this tool renders the repository, not the build output.");
    }
}
