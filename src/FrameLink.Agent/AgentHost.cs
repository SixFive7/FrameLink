using System.Security.Cryptography;
using FrameLink.Agent.Discovery;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Agent.Link;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;
using FrameLink.Agent.Telemetry;
using FrameLink.Agent.Update;
using FrameLink.Protocol;

namespace FrameLink.Agent;

/// <summary>
/// Wires the agent together and runs it — M1's walking skeleton end to end.
/// </summary>
/// <remarks>
/// <para>
/// §5.1's M1 is <i>connect → appear pending → adopted → reconcile one trivial resource →
/// self-update</i>, and that sentence is the shape of this class. Everything here exists to retire
/// an <b>integration</b> risk: AOT on arm64, the update path, the frozen handshake, adoption,
/// socket liveness. None of them is hard once proven and all of them are miserable to discover
/// late underneath a finished reconciler.
/// </para>
/// <para>
/// Three loops run concurrently and none of them depends on another finishing: the console stage
/// repaints, the control link reconnects forever, and the update service converges hourly out of
/// band. That independence <i>is</i> the design — §2.8 makes the hourly tick the mechanism and the
/// socket merely an optimisation, so a broken socket can never prevent the frame repairing itself.
/// </para>
/// </remarks>
public sealed class AgentHost
{
    /// <summary>Environment variable overriding the state directory.</summary>
    public const string StateDirectoryVariable = "FL_STATE_DIR";

    /// <summary>Environment variable overriding the console device.</summary>
    public const string TerminalVariable = "FL_TTY";

    private readonly IReadOnlyList<string> _arguments;
    private readonly IAgentLog _log;
    private readonly IAgentClock _clock;

    /// <summary>
    /// The name the Fleet Manager last assigned, written by the link loop and read by the
    /// reconciler. Volatile because those are two different threads.
    /// </summary>
    /// <remarks>
    /// Seeded from <see cref="AgentMemory"/> at startup, so a reboot during an outage does not
    /// take a frame that has been named back to "the Fleet Manager has not answered, so the name
    /// it assigned is not known" (<see cref="DeviceNameResource"/>).
    /// </remarks>
    private string? _desiredDeviceName;

    /// <summary>
    /// The effective settings the Fleet Manager last pushed (§3.4), read by every resource that
    /// takes a value. Swapped wholesale so a resource never sees half a settings revision.
    /// </summary>
    /// <remarks>
    /// Seeded from <see cref="AgentMemory"/> at startup. Empty here means "never been told", which
    /// leaves every resource on its catalog default (§1.2.2) — the state a frame used to fall back
    /// into on every reboot during an outage, at the cost of an apply-and-reboot each way.
    /// </remarks>
    private IReadOnlyDictionary<string, string> _settings = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// What the Fleet Manager last <i>authoritatively</i> said, or that it has said nothing
    /// (§2.6, §3.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It starts at <see cref="ServerAnswer.Silence"/> on every process start and never returns
    /// there. A completed handshake is knowledge this process keeps: §2.6 has the last
    /// authoritative answer standing through an outage, so a link that drops after an <c>ok</c>
    /// leaves the frame adopted rather than unknown. What resets it is a reboot, and that is
    /// exactly right — a frame that comes back up while its server is down genuinely does not
    /// know anything yet, and the resources that need an answer say so instead of guessing.
    /// </para>
    /// <para>
    /// <b>This one is deliberately <i>not</i> seeded from <see cref="AgentMemory"/></b>, and the
    /// reason is worth stating because it looks like an oversight. This value does not mean "is
    /// this frame adopted" — it means "has the server spoken <i>to this process</i>", which is the
    /// only reading under which <see cref="ServerAnswer.Silence"/> is distinguishable from an
    /// answer at all. Seeding it from disk would make silence indistinguishable from speech again:
    /// a remembered <see cref="ServerAnswer.Rejected"/> would have
    /// <see cref="AdoptionResource"/> <i>act</i> during an outage, write "waiting for adoption"
    /// over a good record and burn attempts on a server that has not said anything — the mule
    /// failure that <see cref="ServerAnswer"/> exists to make unrepresentable. The durable half of
    /// adoption already exists and is already correct: the record
    /// <see cref="AdoptionResource"/> keeps on disk, which is what carries a frame's adoption
    /// through an outage.
    /// </para>
    /// </remarks>
    private volatile ServerAnswer _fleetAnswer;

    /// <summary>Creates a host for the given command line.</summary>
    public AgentHost(IReadOnlyList<string> arguments, IAgentLog log, IAgentClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(log);

        _arguments = arguments;
        _log = log;
        _clock = clock ?? new SystemAgentClock();
    }

    /// <summary>Runs the agent until it is asked to stop.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var stateRoot = Environment.GetEnvironmentVariable(StateDirectoryVariable) ?? FileStateStore.DefaultRoot;
        var store = new FileStateStore(stateRoot, PosixFilePermissions.Instance);

        DeviceKey identity;
        try
        {
            identity = DeviceKeyStore.LoadOrCreate(store, _log);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // §3.3 makes identity permanent, so a key that cannot be read is a stop, not a
            // regenerate. Saying so plainly is the whole response.
            _log.Fail($"This frame's identity in {store.PathOf(DeviceKeyStore.KeyFileName)} could not be read: {exception.Message}");
            _log.Fail("Refusing to generate a new identity — that would silently orphan this frame from its Fleet Manager.");
            return ExitCodes.Unrecoverable;
        }

        using var identityScope = identity;

        // Everything this process knows before it has spoken to anything. The journal is built
        // here rather than inside ReconcileServices because the memory needs to ask it one
        // question — has this frame ever been green — and two journal objects over one file would
        // be two caches of it.
        var journal = new ReconcileJournal(store, _log);
        var memory = new AgentMemory(store, _log, _clock);
        var resumed = memory.ResumeCondition(journal.Read().FirstInSyncUtc is not null);

        Volatile.Write(ref _settings, memory.Settings);
        Volatile.Write(ref _desiredDeviceName, memory.DeviceName);

        var serial = HardwareFacts.ReadSerial(HostTextFileReader.Instance);
        var hub = new AgentStatusHub(new AgentStatus
        {
            // §2.6: a frame that was fully green when contact dropped carries on. Both fields are
            // seeded, not just the second — LastAuthoritative is what makes the *next* NoContact
            // green, and Condition is what keeps the frame from spending the first half-minute of
            // every power cut showing a repair screen it will immediately replace.
            Condition = resumed ?? DeviceStateLadder.Starting,
            LastAuthoritative = resumed,
            DeviceId = identity.DeviceId,
            HardwareSerial = serial,
        });

        // The hub is the one place LastAuthoritative is decided, by either of the two paths that
        // set it, so mirroring it here catches a mid-session change of mind as well as a fresh
        // handshake — §2.6's "an authoritative answer always wins", including the answer that says
        // this frame is no longer adopted.
        using var remembering = hub.Subscribe(status =>
        {
            if (status.LastAuthoritative is { } answered)
            {
                memory.RememberAnswer(answered);
            }
        });

        var systemFiles = HostSystemFiles.Instance;
        var display = new SysfsDisplayProbe(systemFiles);

        using var stage = new ConsoleStage(
            TtyTerminal.Open(Environment.GetEnvironmentVariable(TerminalVariable) ?? TtyTerminal.DefaultPath, _log),
            hub,
            _clock,
            display,
            _log);

        // Painted before anything slow happens. Endpoint discovery can spend a couple of seconds
        // listening for mDNS, and §2.7's hard rule against blank screens covers those seconds too.
        stage.Paint();

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var restart = new ProcessRestartSignal(shutdown, _log);

        var endpoints = await ResolveEndpointsAsync(store, hub, shutdown.Token).ConfigureAwait(false);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var updates = new UpdateService(
            new HttpReleaseSource(http, _log),
            new FileBinarySwap(
                AgentBuild.ExecutablePath ?? "/usr/local/bin/fl-agent",
                PosixFilePermissions.Instance,
                _log),
            _clock,
            hub,
            restart,
            _log,
            () => hub.Current.Endpoints.Count > 0 ? hub.Current.Endpoints[0] : null,
            AgentBuild.Version,
            AgentBuild.RuntimeIdentifier);

        using var uplink = new AgentUplink();
        var outbox = new TelemetryOutbox(uplink, store, _log);
        var boot = new KernelBootIdentity(HostTextFileReader.Instance);

        // A local debugging switch, not a configuration channel (decision 48). Read once, because
        // the command line cannot change under a running process.
        var development = CountdownDuration.IsDevelopmentRun(_arguments);

        var loop = new ReconcileLoop(new ReconcileServices
        {
            Graph = DeviceCatalog.BuildGraph(new DeviceCatalogContext
            {
                Files = systemFiles,
                Store = store,
                Processes = HostProcessRunner.Instance,
                SystemControl = new SystemdControl(),
                Display = display,
                Boot = boot,
                Clock = _clock,
                Log = _log,
                Values = new FleetValues(key => Volatile.Read(ref _settings).GetValueOrDefault(key)),
                FleetAnswer = () => _fleetAnswer,
                DesiredDeviceName = () => Volatile.Read(ref _desiredDeviceName),
            }),
            Journal = journal,
            Boot = boot,
            Reboots = new SystemRebootBoundary(new SystemdControl(), _log),
            Countdown = new RebootCountdown(_clock),
            Telemetry = outbox,
            Hub = hub,
            Clock = _clock,
            Log = _log,
            Options = new ReconcileOptions
            {
                // Read at each reboot, not here. The settings map is empty until the Fleet
                // Manager has answered, and decision 48 leaves it as the only configuration
                // source there is — so resolving once at startup would pin every frame to the
                // built-in 60 s and quietly discard the operator's setting.
                CountdownSource = () => CountdownDuration.Resolve(
                    Volatile.Read(ref _settings).GetValueOrDefault(CountdownDuration.SettingKey),
                    development),
            },
        })
        {
            DeviceId = identity.DeviceId,
        };

        var link = new ControlLink(
            new WebSocketControlTransportFactory(),
            hub,
            identity,
            _clock,
            _log,
            () => hub.Current.Endpoints,
            onVerdict: (verdict, token) => OnVerdictAsync(verdict, memory, updates, outbox, token))
        {
            Uplink = uplink,
            OnSettings = push =>
            {
                Volatile.Write(ref _settings, push.Values);
                memory.RememberSettings(push);
            },
            HardwareSerial = serial,

            // Free text with a vocabulary head, per AgentHealth. The head is what the Fleet
            // Manager classifies and renders a presence badge from; the parenthesis is for a
            // person reading the row. `Progressing` is the honest term at this point: the agent
            // is coming up and has verified nothing yet, so claiming InSync would be a lie the
            // console would repeat.
            AgentStatusText = AgentHealth.Describe(
                AgentResourceStatus.Progressing,
                $"{AgentBuild.RuntimeIdentifier}, endpoints resolved by {endpoints?.DiscoveredBy ?? "nothing yet"}"),
        };

        _log.Info($"FrameLink Agent {AgentBuild.Version} ({AgentBuild.RuntimeIdentifier}) starting as {identity.DeviceId}.");

        // Four loops now, and the fourth is deliberately not gated on the third. §1.2.2: a frame
        // must provision and self-heal with the server unreachable, so the reconciler runs from
        // the first second whether or not anything ever answers. What adoption gates is the
        // resources that need issued values, and it gates them through the DAG (§2.2) rather
        // than by not running.
        var running = new List<Task>(4)
        {
            stage.RunAsync(shutdown.Token),
            link.RunAsync(shutdown.Token),
            updates.RunAsync(shutdown.Token),
            loop.RunAsync(shutdown.Token),
        };

        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Asked to stop, or standing aside for a new binary.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // One of the three loops died. The unit's Restart=always brings the agent straight
            // back, so the only thing that must not happen is the reason going unrecorded (§1.2.3).
            _log.Fail($"The agent stopped unexpectedly: {exception}");
            await shutdown.CancelAsync().ConfigureAwait(false);
            return ExitCodes.Unrecoverable;
        }

        return restart.Requested ? ExitCodes.RestartToApplyUpdate : ExitCodes.Success;
    }

    private async Task<ControlEndpoints?> ResolveEndpointsAsync(
        IStateStore store,
        AgentStatusHub hub,
        CancellationToken cancellationToken)
    {
        var resolver = new EndpointResolver(
            store,
            [
                new InstallFlagEndpointSource(_arguments),
                new BootFileEndpointSource(HostTextFileReader.Instance),
                new MdnsEndpointSource(new UdpMulticastQuery(_log)),
            ],
            _clock,
            _log);

        var endpoints = await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);

        hub.Publish(status => status with
        {
            Endpoints = endpoints?.Endpoints ?? status.Endpoints,
        });

        if (endpoints is null)
        {
            _log.Warn("No Fleet Manager address is configured. Re-run the install command, or put control-url= in the boot file.");
        }

        return endpoints;
    }

    /// <summary>
    /// Reacts to one handshake verdict.
    /// </summary>
    /// <remarks>
    /// Both branches are deliberately cheap and deliberately optional. §2.8: the handshake
    /// triggers the update immediately instead of waiting for the hourly tick, and correctness
    /// never depends on it — so this method nudges, and nothing here is the reason anything
    /// converges.
    /// </remarks>
    private async Task OnVerdictAsync(
        HandshakeResult verdict,
        AgentMemory memory,
        UpdateService updates,
        TelemetryOutbox outbox,
        CancellationToken cancellationToken)
    {
        if (verdict.ServedAgentVersion is { Length: > 0 } served
            && !string.Equals(served, AgentBuild.Version, StringComparison.Ordinal))
        {
            updates.TriggerNow();
        }
        else if (string.Equals(verdict.Status, HandshakeStatus.VersionMismatch, StringComparison.Ordinal))
        {
            updates.TriggerNow();
        }

        if (!string.Equals(verdict.Status, HandshakeStatus.Ok, StringComparison.Ordinal))
        {
            // §3.3: a pending device receives nothing — no configuration, no token, no commands.
            // The reconciler keeps running regardless; its adoption resource simply observes
            // that this frame is not adopted and blocks everything that needs an issued value.
            // This branch is reached only because the server answered, which is what makes it a
            // rejection rather than silence — the two are different states here, and the only
            // way into either of them is a completed handshake.
            _fleetAnswer = ServerAnswer.Rejected;
            return;
        }

        _fleetAnswer = ServerAnswer.Adopted;

        // Null and empty are different answers here (DeviceNameResource), and both are worth
        // remembering: "you have no name" is a thing the Fleet Manager said, and a reboot during
        // an outage must not turn it back into "nothing has been said".
        var name = verdict.DeviceName ?? string.Empty;
        Volatile.Write(ref _desiredDeviceName, name);
        memory.RememberDeviceName(name);

        // §4.1: buffered telemetry drains on reconnect. Done here rather than in the loop
        // because this is the moment a link exists, and the loop deliberately does not know
        // whether one does.
        await outbox.DrainAsync(cancellationToken).ConfigureAwait(false);
    }
}
