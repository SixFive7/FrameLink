using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Diagram;

/// <summary>
/// The catalog as declared and the catalog as walked, taken from one build of it.
/// </summary>
/// <param name="Declared">Resource ids in the order <c>DeviceCatalog.Build</c> lists them.</param>
/// <param name="Graph">The validated graph built from exactly those resources.</param>
/// <remarks>
/// Both halves come from a single <c>DeviceCatalog.Build</c> call on purpose. The document states
/// whether the topological sort returned the declaration order verbatim, and a claim like that is
/// only worth printing if the two sides are the same catalog rather than two builds of it.
/// </remarks>
public sealed record CatalogSnapshot(IReadOnlyList<string> Declared, ResourceGraph Graph);

/// <summary>
/// The shipped catalog, built somewhere that is not a frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>The real <see cref="DeviceCatalog.Build(DeviceCatalogContext)"/>, not a copy of it.</b> The
/// whole value of a generated diagram is that the picture and the running order come from one
/// object, so this constructs the catalog exactly as <c>AgentHost</c> does and then reads
/// <see cref="ResourceGraph.Ordered"/> off it. Parsing the catalog out of the C# source would be a
/// second implementation of the thing being documented, and a second implementation is what
/// drifts.
/// </para>
/// <para>
/// <b>Nothing here touches the machine it runs on.</b> Building a resource stores its seams; it
/// never observes, acts or reads. The filesystem and state seams are still pointed at a throwaway
/// directory rather than at <c>/</c> and <c>/var/lib/fl-agent</c>, because "it does not read
/// anything today" is a property of the resource constructors rather than a promise they made,
/// and a generator that could reach <c>/boot/firmware</c> on a developer's laptop would be a poor
/// trade for one line.
/// </para>
/// <para>
/// <b>The resource set and its edges are static.</b> No sub-builder branches on a fleet value, a
/// file or a process result — <c>PackageCatalog</c>, <c>KioskCatalog</c>, <c>AudioCatalog</c> and
/// <c>AppConfigCatalog</c> are all <c>foreach</c> loops over compiled spec lists — so the catalog
/// this produces is the catalog a frame produces. The suite asserts that by re-rendering the
/// committed document through this same code path on every run.
/// </para>
/// </remarks>
public static class CatalogGraph
{
    /// <summary>Builds the shipped catalog and its validated order.</summary>
    /// <param name="scratchRoot">
    /// A directory the filesystem and state seams may be pointed at. Nothing is written to it by
    /// construction; it exists so that neither seam is rooted at the real filesystem.
    /// </param>
    public static CatalogSnapshot Snapshot(string scratchRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);

        var files = new HostSystemFiles(scratchRoot);
        var channel = new LocalChannel();
        var clock = new SystemAgentClock();
        var log = new NullLog();

        var declared = DeviceCatalog.Build(new DeviceCatalogContext
        {
            Files = files,
            Store = new FileStateStore(Path.Combine(scratchRoot, "state"), PosixFilePermissions.Instance),
            Processes = HostProcessRunner.Instance,
            SystemControl = new SystemdControl(),
            Session = new LoginUserSession(HostProcessRunner.Instance, () => string.Empty, files),
            Origin = new LocalOrigin(channel, clock, log, port: 0),
            Channel = channel,
            Display = new SysfsDisplayProbe(files),
            Boot = new KernelBootIdentity(HostTextFileReader.Instance),
            Clock = clock,
            Log = log,
        });

        return new CatalogSnapshot(
            [.. declared.Select(resource => resource.Name)],
            new ResourceGraph(declared));
    }

    /// <summary>Builds the catalog against a fresh temporary directory, and removes it again.</summary>
    /// <remarks>
    /// The graph outlives the directory on purpose: it holds seams pointed at a path that no
    /// longer exists, which is harmless because nothing downstream of here does anything but read
    /// <see cref="IResource.Name"/> and <see cref="IResource.DependsOn"/>.
    /// </remarks>
    public static CatalogSnapshot Snapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "fl-diagram", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            return Snapshot(root);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A scratch directory that outlives the process is not worth failing a render for.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
