using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// §2.2's explicit lightweight DAG, and §5.1's requirement that dependents be marked
/// <c>Blocked(dependency)</c> rather than left to fail confusingly on their own.
/// </summary>
public sealed class AgentResourceGraphTests
{
    [Fact]
    public void Resources_are_ordered_with_their_dependencies_first()
    {
        var graph = new ResourceGraph(
        [
            new ScriptedResource("governor", "x", "x", "enabled"),
            new ScriptedResource("enabled", "x", "x", "unit"),
            new ScriptedResource("unit", "x", "x"),
        ]);

        Assert.Equal(["unit", "enabled", "governor"], graph.Ordered.Select(resource => resource.Name));
    }

    [Fact]
    public void Independent_resources_keep_the_order_the_catalog_declared_them_in()
    {
        // §2.2 asks for sequential and single-threaded execution because "determinism beats
        // throughput on a 2 GB appliance". A sort that could return two different valid orders on
        // two boots would give half of that back, and the order is what puts the display first.
        var graph = new ResourceGraph(
        [
            new ScriptedResource("display", "x", "x"),
            new ScriptedResource("journal", "x", "x"),
            new ScriptedResource("adoption", "x", "x"),
        ]);

        Assert.Equal(["display", "journal", "adoption"], graph.Ordered.Select(resource => resource.Name));
    }

    [Fact]
    public void A_dependency_cycle_is_refused_at_construction_and_names_everyone_in_it()
    {
        // Detected at construction, not at run time: a cycle is a mistake in the compiled catalog
        // and can never be a condition a frame finds itself in.
        var thrown = Assert.Throws<ResourceGraphException>(() => new ResourceGraph(
        [
            new ScriptedResource("a", "x", "x", "c"),
            new ScriptedResource("b", "x", "x", "a"),
            new ScriptedResource("c", "x", "x", "b"),
        ]));

        Assert.Contains("cycle", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a, b, c", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dependency_that_is_not_in_the_catalog_is_refused_at_construction()
    {
        var thrown = Assert.Throws<ResourceGraphException>(() => new ResourceGraph(
        [
            new ScriptedResource("a", "x", "x", "nowhere"),
        ]));

        Assert.Contains("nowhere", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_resources_with_the_same_id_are_refused_at_construction()
    {
        var thrown = Assert.Throws<ResourceGraphException>(() => new ResourceGraph(
        [
            new ScriptedResource("same", "x", "x"),
            new ScriptedResource("same", "x", "x"),
        ]));

        Assert.Contains("same", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dependent_of_a_drifted_resource_is_blocked_and_never_attempted()
    {
        var root = new ScriptedResource("root", "want", "have-not") { ActHasNoEffect = true };
        var dependent = new ScriptedResource("dependent", "want", "have-not", "root");
        using var harness = new ReconcileHarness(root, dependent);

        var outcome = await harness.PassAsync();
        var status = ReconcileHarness.StatusOf(outcome, "dependent");

        Assert.Equal(ResourceStatusKind.Blocked, status.Kind);
        Assert.Equal("root", status.BlockedBy);
        Assert.Equal(0, dependent.Acts);

        // Not observed either. §2.2's point is that a dependent must not "fail confusingly on its
        // own", and observing it would put a meaningless value in the delta the operator reads.
        Assert.Equal(0, dependent.Observations);
    }

    [Fact]
    public async Task A_blocked_resource_becomes_reachable_once_its_dependency_converges()
    {
        var root = new ScriptedResource("root", "want", "have-not");
        var dependent = new ScriptedResource("dependent", "want", "have-not", "root");
        using var harness = new ReconcileHarness(root, dependent);

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(1, root.Acts);
        Assert.Equal(1, dependent.Acts);
        Assert.All(outcome.Statuses, status => Assert.Equal(ResourceStatusKind.InSync, status.Kind));

        // One reboot each, no exceptions (§2.4).
        Assert.Equal(2, harness.Boundary.Crossings.Count);
    }

    [Fact]
    public void The_shipped_catalog_orders_the_display_first_and_needs_nothing_to_do_it()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(Context(files));

        // Positions 1–3 of the catalog's own ordering table: the agent-version root, then the
        // display carve-out, ahead of the keypair and adoption. `agent.version` is first in
        // declaration order and nothing else — it holds no edges, so it cannot block anything.
        Assert.Equal(AgentVersionResource.ResourceName, graph.Ordered[0].Name);
        Assert.Equal(DisplayPanelOverlayResource.ResourceName, graph.Ordered[1].Name);
        Assert.Equal(ConsoleRotationResource.ResourceName, graph.Ordered[2].Name);

        // The whole point of moving it: it is gated by nothing. A pending frame has to be able to
        // show its own fingerprint (§3.3), so an adoption dependency here would defeat the change.
        Assert.Empty(graph.Ordered[0].DependsOn);
        Assert.Empty(graph.Ordered[1].DependsOn);
        Assert.Equal([DisplayPanelOverlayResource.ResourceName], graph.Ordered[2].DependsOn);
    }

    [Fact]
    public void The_three_agent_roots_hold_no_edges_so_an_unreachable_server_blocks_nothing()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(Context(files));

        // The catalog writes this chain as agent.version → agent.keypair → agent.adoption, and its
        // own convention is that `—` *means* "agent.version and nothing else". Materialising those
        // edges would be the one change that breaks §1.2.2: `agent.version` is unevaluable on a
        // frame whose Fleet Manager has never answered, so an edge on it would mark every other
        // resource Blocked and the frame would provision nothing at all.
        foreach (var root in new[]
        {
            AgentVersionResource.ResourceName,
            AgentKeypairResource.ResourceName,
            AdoptionResource.ResourceName,
        })
        {
            Assert.Empty(graph.Find(root)!.DependsOn);
        }

        Assert.DoesNotContain(
            graph.Ordered,
            resource => resource.DependsOn.Contains(AgentVersionResource.ResourceName));
    }

    [Fact]
    public async Task The_version_root_says_it_does_not_know_rather_than_claiming_a_mismatch()
    {
        string? served = null;
        var converged = 0;
        var resource = new AgentVersionResource("0.1.0+abc", () => served, () => converged++);

        // Silence is not an answer (§2.6). A frame that cannot ask what version it should be
        // running must not report "expected x, observed y" of itself.
        var silent = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ObservationOutcome.Unevaluable, silent.Outcome);

        served = "0.1.0+abc";
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        // It matches; it never compares. An *older* served version is ordinary drift, because
        // reverting the container tag has to revert the fleet (§2.8).
        served = "0.0.9+old";
        var drifted = await resource.ObserveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ObservationOutcome.Drifted, drifted.Outcome);

        await resource.ActAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, converged);
    }

    [Fact]
    public void The_shipped_catalog_is_a_valid_dag_with_the_dependencies_the_catalog_document_states()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(Context(files));

        // <b>The whole catalog.</b> `reference/resource-catalog.md` enumerates 80 resources, one of
        // which — `pkg.git` — open question 3's adopted reading deletes, because `xvf_host` arrives
        // as a pinned checksum-verified artifact rather than a clone and guide 10's other use of
        // git went with the embedded app. That leaves 79 implementable entries, all of them here.
        // Two shipped resources are *not* in the catalog: `agent.device-name`, the display name the
        // Fleet Manager assigns at adoption, which the cross-guide section never enumerated; and
        // `kiosk.config.albums`, which scopes what the slideshow selects from and which neither
        // guide 9's Compose file nor the catalog ever had — a gap the frame proved by finding no
        // photos at all. 79 catalog entries plus those two is the arithmetic.
        //
        // It was 80 until decision 90 removed `firmware.xvf3800.version`: a firmware version has no
        // Act that can succeed, because the only one is a DFU write, and a resource that cannot act
        // spends three attempts, three reboots and an escalation instead of reporting. Decision 91
        // reversed the product decision without touching that reasoning — the agent writes firmware
        // again, but from `ArrayFirmwareFlash` beside the loop, and what came back into the graph is
        // `firmware.xvf3800.image`, which converges the pinned images on the card and never the
        // array.
        Assert.Equal(81, graph.Count);

        var names = graph.Ordered.Select(resource => resource.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(PackageResource.Prefix + "git", names);
        Assert.Contains("agent.device-name", names);
        Assert.Contains("kiosk.config.albums", names);
        Assert.Equal([AdoptionResource.ResourceName], graph.Find(HostnameResource.ResourceName)!.DependsOn);
        Assert.Equal(
            [CpuGovernorUnitResource.ResourceName],
            graph.Find(CpuGovernorUnitEnabledResource.ResourceName)!.DependsOn);
        Assert.Equal(
            [CpuGovernorUnitEnabledResource.ResourceName],
            graph.Find(CpuGovernorResource.ResourceName)!.DependsOn);

        // Ordering is a property of the graph, so the governor cannot precede its unit whatever
        // order the catalog happens to declare them in.
        var order = graph.Ordered.Select(resource => resource.Name).ToList();
        Assert.True(order.IndexOf(CpuGovernorUnitResource.ResourceName)
            < order.IndexOf(CpuGovernorUnitEnabledResource.ResourceName));
        Assert.True(order.IndexOf(CpuGovernorUnitEnabledResource.ResourceName)
            < order.IndexOf(CpuGovernorResource.ResourceName));
    }

    [Fact]
    public void The_session_and_kiosk_block_depends_on_what_the_catalog_document_says_it_does()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(Context(files));
        var order = graph.Ordered.Select(resource => resource.Name).ToList();

        // The whole user-unit layer hangs off the autologin drop-in, because there is no
        // `loginctl enable-linger` anywhere in this build.
        Assert.Contains(
            ConsoleAutologinResource.ResourceName,
            graph.Find(BashProfileLabwcResource.ResourceName)!.DependsOn);
        Assert.Contains(
            ConsoleAutologinResource.ResourceName,
            graph.Find(ChromiumKioskUnitResource.ResourceName)!.DependsOn);

        // The mode bit is its own resource and comes after the content it applies to; the running
        // browser comes after both the unit and its enablement.
        Assert.True(order.IndexOf(LabwcAutostartResource.ResourceName)
            < order.IndexOf(LabwcAutostartExecutableResource.ResourceName));
        Assert.True(order.IndexOf(ChromiumKioskEnabledResource.ResourceName)
            < order.IndexOf(ChromiumKioskRunningResource.ResourceName));

        // The kiosk unit's readiness guard polls the local origin, so the origin has to be up
        // first — the catalog puts app.http.local-origin ahead of the unit for exactly that.
        Assert.True(order.IndexOf(LocalOriginResource.ResourceName)
            < order.IndexOf(ChromiumKioskUnitResource.ResourceName));

        // The catalog's dependsOn rule: a resource declares agent.adoption when its desired value
        // is issued by the Fleet Manager and no catalog default is correct before adoption. The
        // four call values have no possible default — the address of somebody's LiveKit server is
        // not something this document can hold — so they are gated.
        foreach (var name in new[] { "app.config.identity", "app.config.room", "app.config.livekit-url" })
        {
            Assert.Contains(AdoptionResource.ResourceName, graph.Find(name)!.DependsOn);
        }

        Assert.Equal(
            ["app.config.identity", "app.config.room", "app.config.livekit-url"],
            graph.Find("app.config.livekit-token")!.DependsOn);

        // And the slideshow URL is not, because its base is fixed and slideshow.interval has a
        // catalog default that is correct on an unadopted frame. Nor is the kiosk stack itself:
        // §2.7's browser stage has to be able to render the "adopt me" screen on exactly the frame
        // that has not been adopted. What it does depend on is the address it names, which is the
        // one edge the catalog gives it.
        Assert.Equal(
            [KioskListenAddressResource.ResourceName],
            graph.Find("app.config.immich-kiosk-url")!.DependsOn);
        Assert.DoesNotContain(
            AdoptionResource.ResourceName,
            graph.Find("app.config.immich-kiosk-url")!.DependsOn);
        Assert.DoesNotContain(AdoptionResource.ResourceName, graph.Find(ChromiumKioskUnitResource.ResourceName)!.DependsOn);
        Assert.DoesNotContain(AdoptionResource.ResourceName, graph.Find(ConsoleAutologinResource.ResourceName)!.DependsOn);
    }

    /// <summary>
    /// The shipped catalog against the document that specifies it, read rather than remembered.
    /// </summary>
    /// <remarks>
    /// §7.1's "never asserted from memory", applied to the resource enumeration itself. This test
    /// parses <c>reference/resource-catalog.md</c> — the same headings a reader counts — so a
    /// resource added to the document and not to the code, or dropped from the code and left in
    /// the document, fails here rather than being noticed by somebody diffing two lists by eye.
    /// The one exclusion and the one addition are named individually, so neither can widen quietly.
    /// </remarks>
    [Fact]
    public void The_shipped_catalog_is_exactly_the_catalog_document_minus_its_one_exclusion()
    {
        // Open question 3: `xvf_host` arrives as a pinned, checksum-verified upstream artifact
        // rather than a git clone, and the catalog says outright that "if it does not, this
        // resource disappears". Guide 10's other use of git went with the embedded app.
        var excluded = new[] { PackageResource.Prefix + "git" };

        // Not in the document, and both named individually so neither can widen quietly.
        //
        // `agent.device-name` — the display name the Fleet Manager assigns at adoption. The
        // cross-guide section enumerates the keypair, the version and the adoption record, and
        // never enumerated this one.
        //
        // `kiosk.config.albums` — which albums the slideshow draws from. The document's guide 9
        // block is the v2 shape of guide 9's Compose file, and that file scoped selection not at
        // all, so there was nothing to carry across. It is a gap in the document rather than a
        // decision it took: measured on the mule 2026-08-16, a frame whose Immich account owns no
        // assets and sees photos only through a shared album selected nothing, for ever, and said
        // so about seven times a second.
        var extra = new[] { "agent.device-name", "kiosk.config.albums" };

        var document = ResourceCatalogDocument.Ids();
        Assert.Equal(80, document.Count);

        using var files = new TemporaryFiles();
        var shipped = DeviceCatalog.BuildGraph(Context(files))
            .Ordered
            .Select(resource => resource.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = document.Except(excluded, StringComparer.Ordinal)
            .Where(id => !shipped.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();

        var unexpected = shipped
            .Except(document, StringComparer.Ordinal)
            .Except(extra, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
        Assert.Empty(unexpected);
        Assert.DoesNotContain(excluded[0], shipped);
        Assert.Equal(document.Count - excluded.Length + extra.Length, shipped.Count);
    }

    internal static DeviceCatalogContext Context(TemporaryFiles files)
    {
        var channel = new LocalChannel();
        var clock = new ManualClock();

        return new DeviceCatalogContext
        {
            Files = files.Files,
            Store = files.Store,
            Processes = new RecordingProcessRunner(),
            SystemControl = new RecordingSystemControl(),
            Session = new FakeUserSession(),
            Channel = channel,
            Origin = new LocalOrigin(channel, clock, new RecordingLog(), port: 0),
            Display = StaticDisplayProbe.Visible,
            Boot = new MutableBootIdentity(),
            Clock = clock,
            Log = new RecordingLog(),
        };
    }
}

/// <summary>
/// <c>reference/resource-catalog.md</c>, read as the specification it is.
/// </summary>
/// <remarks>
/// The document gives one block per resource, headed by the id in bold code — and where several
/// resources share a block, by several ids separated by <c>·</c>. Nothing else in the file is a
/// line made <i>entirely</i> of bold code spans, which is what makes the heading recognisable
/// without a markdown parser: the ordering table's cells carry the same spelling but sit inside a
/// row with pipes and prose around them.
/// </remarks>
internal static class ResourceCatalogDocument
{
    /// <summary>Every resource id the catalog enumerates, in document order.</summary>
    public static IReadOnlyList<string> Ids()
    {
        var path = Path.Combine(GuiFreshnessTests.RepositoryRoot(), "reference", "resource-catalog.md");
        var ids = new List<string>();

        foreach (var line in File.ReadAllLines(path))
        {
            if (!line.StartsWith("**`", StringComparison.Ordinal))
            {
                continue;
            }

            var heading = new List<string>();
            var rest = line;

            while (rest.StartsWith("**`", StringComparison.Ordinal))
            {
                var close = rest.IndexOf("`**", 3, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                heading.Add(rest[3..close]);
                rest = rest[(close + 3)..].TrimStart(' ', '·');
            }

            // Only a line that is *nothing but* ids is a heading. Anything left over means this
            // was prose that happened to begin with one.
            if (rest.Length == 0)
            {
                ids.AddRange(heading);
            }
        }

        return ids;
    }
}

/// <summary>
/// A real filesystem, rooted somewhere throwaway.
/// </summary>
/// <remarks>
/// The point of <see cref="HostSystemFiles"/> taking a root is that the resources are exercised
/// against the implementation that ships — real paths, real UTF-8 bytes, real directory
/// creation, real SHA-256 — rather than against an in-memory stand-in that agrees with them by
/// construction.
/// </remarks>
internal sealed class TemporaryFiles : IDisposable
{
    private readonly TemporaryStore _store = new();

    public TemporaryFiles()
    {
        Root = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Files = new HostSystemFiles(Root);
    }

    public string Root { get; }

    public HostSystemFiles Files { get; }

    public IStateStore Store => _store.Store;

    /// <summary>Writes a file through the real implementation, as a frame would already have it.</summary>
    public void Seed(string path, string content) => Files.WriteText(path, content);

    /// <summary>Reads a file back through the real implementation.</summary>
    public string? Read(string path) => Files.ReadText(path);

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}

/// <summary>Records every command and answers with a script.</summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    public List<string> Commands { get; } = [];

    public Dictionary<string, ProcessResult> Answers { get; } = new(StringComparer.Ordinal);

    public ProcessResult Default { get; set; } = new(0, string.Empty, string.Empty);

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var line = executable + " " + string.Join(' ', arguments);
        Commands.Add(line);

        return Task.FromResult(Answers.TryGetValue(line, out var scripted) ? scripted : Default);
    }
}
