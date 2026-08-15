using System.Runtime.InteropServices;
using FrameLink.Agent.Discovery;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Systemd;

namespace FrameLink.Agent;

/// <summary>Entry point of <c>fl-agent</c>.</summary>
/// <remarks>
/// Three verbs and no framework. A dependency-injection container, a configuration binder and a
/// generic host would each pull reflection into a program whose delivery format is one Native AOT
/// binary (§2.1) — and the composition they would manage is one page long (<see cref="AgentHost"/>),
/// written out where it can be read.
/// </remarks>
public static class Program
{
    /// <summary>Parses the verb and runs it.</summary>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var log = new StandardOutputLog();
        var verb = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "run";

        using var shutdown = new CancellationTokenSource();
        using var terminate = CreateSignalHandler(shutdown);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Cancel(shutdown);
        };

        switch (verb)
        {
            case "run":
                return await new AgentHost(args, log).RunAsync(shutdown.Token).ConfigureAwait(false);

            case "install":
                return await InstallAsync(args, log, shutdown.Token).ConfigureAwait(false);

            case "version":
                Console.Out.WriteLine($"fl-agent {AgentBuild.Version} ({AgentBuild.RuntimeIdentifier})");
                return ExitCodes.Success;

            default:
                Console.Error.WriteLine($"Unknown command '{verb}'. Expected: run, install, version.");
                return ExitCodes.Unrecoverable;
        }
    }

    /// <summary>
    /// Installs the unit and records where this frame belongs.
    /// </summary>
    /// <remarks>
    /// The endpoint is persisted here rather than being left for the first run to rediscover,
    /// because §4.3's "never rediscover" is only meaningful if the authoritative answer is written
    /// before anything else gets a chance to answer.
    /// </remarks>
    private static async Task<int> InstallAsync(
        IReadOnlyList<string> args,
        IAgentLog log,
        CancellationToken cancellationToken)
    {
        var stateRoot = Environment.GetEnvironmentVariable(AgentHost.StateDirectoryVariable)
            ?? FileStateStore.DefaultRoot;
        var store = new FileStateStore(stateRoot, PosixFilePermissions.Instance);
        store.EnsureReady();

        var clock = new SystemAgentClock();
        var resolver = new EndpointResolver(store, [new InstallFlagEndpointSource(args)], clock, log);

        if (resolver.Persisted() is { } already)
        {
            log.Info($"This frame is already pointed at {already.Endpoints[0]} (found by {already.DiscoveredBy}).");
        }
        else if (await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            log.Fail($"Pass {InstallFlagEndpointSource.ControlUrlFlag} <https://your-fleet-manager> so the frame knows where to report.");
            return ExitCodes.Unrecoverable;
        }

        var installed = await UnitInstaller
            .InstallAsync(new SystemdControl(), log, UnitInstaller.DefaultUnitPath, cancellationToken)
            .ConfigureAwait(false);

        return installed ? ExitCodes.Success : ExitCodes.Unrecoverable;
    }

    private static PosixSignalRegistration? CreateSignalHandler(CancellationTokenSource shutdown)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Cancel(shutdown);
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static void Cancel(CancellationTokenSource shutdown)
    {
        try
        {
            shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already shutting down.
        }
    }
}
