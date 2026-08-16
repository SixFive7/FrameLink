using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FrameLink.Agent.Discovery;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Agent.Kiosk;
using FrameLink.Agent.Link;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;
using FrameLink.Agent.Supervise;
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

            // §2.7 item 8, seeded off disk before anything has connected — which is the whole
            // point of it. A frame that comes back from the reboot a failed resource just took
            // must be able to name who to ask before, and whether or not, it ever reaches a Fleet
            // Manager again (decision 71).
            Contact = memory.Contact,
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

        var terminalPath = Environment.GetEnvironmentVariable(TerminalVariable) ?? TtyTerminal.DefaultPath;

        using var stage = new ConsoleStage(
            TtyTerminal.Open(terminalPath, _log),
            hub,
            _clock,
            display,
            _log);

        // The console stage's terminal is not the product's any more (§2.7), so something has to
        // decide which of the two the panel is showing. It reads §2.7's second stage to decide, and
        // that stage does not exist for another two hundred lines — hence the forward reference
        // rather than a constructor argument. Console is the honest answer until then: no graphical
        // stack is up, which is exactly what a null browser stage means.
        using var terminals = new LinuxVirtualTerminals(systemFiles);
        BrowserStage? browserStage = null;
        using var screen = new ScreenHandover(
            terminals,
            HostProcessRunner.Instance,
            _clock,
            _log,
            () => browserStage?.Phase ?? BrowserStagePhase.Console,
            TtyTerminal.NumberOf(terminalPath) ?? TtyTerminal.AgentTerminal,
            TtyTerminal.ProductTerminal);

        // Painted before anything slow happens. Endpoint discovery can spend a couple of seconds
        // listening for mDNS, and §2.7's hard rule against blank screens covers those seconds too —
        // which since the move to a terminal of its own also means the panel has to be showing the
        // one being painted, four lines below as soon as there is a token to pass it.
        stage.Paint();

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var restart = new ProcessRestartSignal(shutdown, _log);

        // Reconciled rather than taken. On a cold boot there is no compositor and this is the take
        // that puts the narration in front of a person watching a frame provision. On an agent
        // restart — every update, every crash — the product is already on the panel, and grabbing
        // it for a couple of seconds because a service restarted would be a fault of its own.
        await screen.ReconcileAsync(shutdown.Token).ConfigureAwait(false);

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

        var values = new FleetValues(key => Volatile.Read(ref _settings).GetValueOrDefault(key));

        // Beside the loop, not inside it. The package set is an observation nothing converges,
        // and §2.4's reboot-per-resource is what makes a startup read plus a slow tick enough to
        // catch every change: everything the agent installs is followed by a process start, and
        // the only thing that moves a package on a converged frame is the overnight security
        // update the tick is sized for.
        var packages = new PackageInventoryReporter(
            new AptPackages(HostProcessRunner.Instance),
            outbox,
            store,
            _clock,
            _log,
            values)
        {
            DeviceId = identity.DeviceId,
        };

        // The kiosk stack lives in one unprivileged user's session while the agent is root in the
        // system manager (§6.1). One seam covers both the home directory and that user's systemd.
        // No fallback passed: an unset device.user falls through to the account the image was
        // flashed with, which the frame reads off itself. The catalog requires the autologin
        // drop-in to converge before adoption, and it can only do that if it has a value then.
        var session = new LoginUserSession(
            HostProcessRunner.Instance,
            () => values.Get(LoginUserSession.SettingKey, string.Empty));

        // §2.1: the app is inside this binary, and §2.7 wants the repair screen on the same
        // origin. One server answers both, plus the local channel the page checks in over.
        var channel = new LocalChannel();
        await using var origin = new LocalOrigin(
            channel,
            _clock,
            _log,
            () => AppConfigCatalog.Issued(store),
            () => BrowserStage.Compose(hub.Current, _clock.UtcNow));

        // Started here and not only by its resource, because the server lives in this process and
        // therefore cannot survive the reboot every resource takes (§2.4). If starting it were
        // left to the Act, the resource would find it down on every boot, act, reboot, and find it
        // down again — a loop that never converges. Started at every process start, the resource
        // becomes what it should be: an assertion that the origin is answering, with an Act that
        // retries the bind for the one case that can actually fail, a port somebody else holds.
        origin.Start();

        // §2.10's interlock, shared by the two responsibilities. Created here because both sides
        // need the same instance and neither owns the other.
        var interlock = new SupervisionInterlock();

        // A local debugging switch, not a configuration channel (decision 48). Read once, because
        // the command line cannot change under a running process.
        var development = CountdownDuration.IsDevelopmentRun(_arguments);

        // §2.7 item 4: a "Reboot now" button to skip the countdown once read, "a tap on the
        // touchscreen". The tap arrives over the local channel, which is why the repair screen and
        // the product share an origin — the button is on the agent's own page.
        var countdown = new RebootCountdown(_clock);
        channel.RebootRequested += countdown.SkipNow;

        // §2.5 rung 5: the same retry the Fleet Manager sends, pressed by whoever is standing in
        // front of the frame. It is deliberately the *device-wide* form — the person pressing it
        // has not chosen a resource and should not have to — and it goes through
        // ResetExhaustedBudgets, the one reset in the agent, so a press here and a press in a
        // browser two hundred kilometres away cannot come to mean different things (decision 72).
        //
        // Assigned later, beside the loop it needs; declared here so the ordering reads with the
        // other local-channel handlers.
        Action? retryFromFrame = null;
        channel.RetryRequested += () => retryFromFrame?.Invoke();

        // Guide 11's daemon, inside the agent (§2.1). It holds the GPIO line for as long as the
        // agent runs and turns a press into the toggle that used to arrive over a WebSocket server
        // on 127.0.0.1:8889 — the port the catalog retires, since daemon and app are now one
        // binary. Created before the catalog because `gpio.button.line` observes this claim.
        var button = new ButtonWatch(new ButtonWatchServices
        {
            Channel = channel,
            Lines = GpioMonLines.Instance,
            Stage = () => BrowserStage.Compose(hub.Current, _clock.UtcNow),
            Clock = _clock,
            Log = _log,
            Values = values,
        });

        // Guide 11 step 4, kept working with a different address: `systemctl kill -s SIGUSR1
        // fl-agent.service` where v1 signalled `framelink-gpio.service`. The catalog requires the
        // simulated press to be reimplemented rather than dropped, and it is the only way to
        // exercise the whole path on a frame whose button is not fitted yet.
        using var simulatedPress = SimulatedPress(button);

        // §2.1, decision 41: Immich Kiosk stays upstream and runs as a child of this process, which
        // is what takes Docker off the frame. Created before the catalog because seven of guide 9's
        // eight resources read this object — the paths it owns and the pid only it holds — and a
        // second instance would be a second child nobody is supervising. Its settings are read from
        // what the reconciler has *recorded*, not from what the Fleet Manager most recently said,
        // so a settings push reaches the child through the resource that owns it (§2.6).
        var kiosk = new KioskProcess(new KioskProcessServices
        {
            Store = store,
            Clock = _clock,
            Log = _log,
            Interlock = interlock,
            RecoveryDeadline = () => new SupervisionSettings(values).RecoveryDeadline,
            Settings = () => KioskCatalog.SettingsFrom(
                store,
                Path.Combine(store.Root, KioskProcess.DirectoryName)),
        });

        var loop = new ReconcileLoop(new ReconcileServices
        {
            Graph = DeviceCatalog.BuildGraph(new DeviceCatalogContext
            {
                Files = systemFiles,
                Store = store,
                Processes = HostProcessRunner.Instance,
                SystemControl = new SystemdControl(),
                Display = display,
                Session = session,
                Origin = origin,
                Channel = channel,
                Boot = boot,
                Clock = _clock,
                Log = _log,
                Values = values,
                FleetAnswer = () => _fleetAnswer,
                DesiredDeviceName = () => Volatile.Read(ref _desiredDeviceName),
                Button = button,
                Kiosk = kiosk,
                KioskDownload = new HttpKioskDownload(http, _log),
                XvfHostDownload = new HttpXvfHostDownload(http, _log),
                Permissions = PosixFilePermissions.Instance,

                // §2.8's root. The served version is whatever the out-of-band check last learned,
                // which the hub already holds, and converging is that same check brought forward —
                // the resource asks, the hourly loop does, and correctness never depends on the ask
                // arriving.
                RunningVersion = AgentBuild.Version,
                ServedVersion = () => hub.Current.ServedAgentVersion,
                ConvergeVersion = updates.TriggerNow,

                // §2.9. Read live rather than captured, so `agent.keypair` compares against what
                // this process is running as at the moment it looks.
                DeviceId = () => identity.DeviceId,
            }),
            Interlock = interlock,
            Journal = journal,
            Boot = boot,
            Reboots = new SystemRebootBoundary(new SystemdControl(), _log),
            Countdown = countdown,
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

                // Decision 53's sibling of the line above, read the same way and for a sharper
                // version of the same reason: the frame this one paces is mid-provision, so its
                // settings are arriving for the first time while the loop is already running.
                ProvisioningPaceSource = () => ProvisioningPace.Resolve(
                    Volatile.Read(ref _settings).GetValueOrDefault(ProvisioningPace.SettingKey),
                    development),
            },
        })
        {
            DeviceId = identity.DeviceId,
        };

        // The other end of the local channel's retry, now that there is a loop to reset. Nothing
        // is forced: the budget is cleared and the walk picks it up on its next pass, which is
        // exactly what the Fleet Manager's retry does, for the reason RetryRequest records — a
        // transient needing two attempts would defeat a single forced one.
        retryFromFrame = () =>
        {
            var reset = loop.ResetExhaustedBudgets();
            _log.Info(reset.Count > 0
                ? $"Somebody at the frame asked it to try again: {string.Join(", ", reset)}."
                : "Somebody at the frame asked it to try again; nothing had given up.");
        };

        // §2.7 item 9 on the console stage (decision 77). The browser stage's retry is a button on
        // a page and arrives over the local channel; this one is the same reset reached by holding
        // the panel, for the hour of a frame's life when there is no browser to put a button in.
        // Offered exactly when the screen says it is, from the same predicate the screen renders,
        // so a hold can never do nothing while the frame invites one.
        var touch = new TouchRetry(new TouchRetryServices
        {
            Input = new EvdevTouchInput(HostTextFileReader.Instance, _log),
            Hub = hub,
            Clock = _clock,
            Log = _log,
            Offered = () => ReconcileVoice.HasStopped(hub.Current),
            Retry = () => retryFromFrame(),
        });

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

            // §2.7 item 8. Persisted first and published second, in that order deliberately: the
            // screen can be repainted from the hub at any time, but the file is what answers after
            // the next reboot — and the next reboot is where the person who needs this sentence is
            // standing (decision 71).
            OnOperatorContact = contact =>
            {
                memory.RememberContact(contact);
                hub.Publish(status => status with { Contact = contact });
            },

            // §2.5 rung 3, and the only inbound message that changes what the reconciler is
            // allowed to do rather than what it converges on. It runs on the receive loop and
            // touches nothing but the journal, which is already the lock the loop's own writes go
            // through — so a retry arriving mid-pass moves the ledger under a walk that reads it
            // per resource, and the worst case is one extra pass before it takes effect.
            OnRetry = request =>
            {
                if (!string.Equals(request.DeviceId, identity.DeviceId, StringComparison.Ordinal))
                {
                    // Impossible over a socket the server addresses by connection, which is why it
                    // is worth asserting: the cost of being wrong is an unrelated frame rebooting
                    // five more times for a setting nobody asked it to retry.
                    _log.Warn($"Ignoring a retry addressed to {request.DeviceId}; this frame is {identity.DeviceId}.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(request.Resource))
                {
                    loop.ResetBudget(request.Resource);
                    return;
                }

                var reset = loop.ResetExhaustedBudgets();
                if (reset.Count > 0)
                {
                    _log.Info($"The Fleet Manager asked this frame to try again: {string.Join(", ", reset)}.");
                }
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

        var supervisor = new Supervisor(new SupervisionServices
        {
            Channel = channel,
            Session = session,
            Memory = new ProcMemoryProbe(systemFiles, HostProcessRunner.Instance),
            Interlock = interlock,
            Hub = hub,
            Telemetry = outbox,
            Clock = _clock,
            Log = _log,
            Settings = new SupervisionSettings(values),
            Store = store,
            DeviceId = identity.DeviceId,
        });

        var browser = new BrowserStage(new BrowserStageServices
        {
            Channel = channel,
            Session = session,
            SystemControl = new SystemdControl(),
            Hub = hub,
            Telemetry = outbox,
            Clock = _clock,
            Log = _log,
            Interlock = interlock,
            Screen = screen,
            Values = values,
            DeviceId = identity.DeviceId,
        });

        // Closes the forward reference opened above. From here the handover reads a real phase.
        browserStage = browser;

        _log.Info($"FrameLink Agent {AgentBuild.Version} ({AgentBuild.RuntimeIdentifier}) starting as {identity.DeviceId}.");

        // Nine loops now, and none is gated on another finishing. §1.2.2: a frame must provision
        // and self-heal with the server unreachable, so the reconciler runs from the first second
        // whether or not anything ever answers. What adoption gates is the resources that need
        // issued values, and it gates them through the DAG (§2.2) rather than by not running.
        // Four of them are §2.10's second responsibility, §2.7's browser stage, the package
        // inventory and the call button: all keep running through an outage, because that is
        // exactly when no help is coming — and the inventory buffers on disk like everything else
        // on that channel (§4.1). The button in particular has to work with nothing reachable,
        // because pressing it is how somebody in this room starts a call.
        var running = new List<Task>(10)
        {
            stage.RunAsync(shutdown.Token),
            link.RunAsync(shutdown.Token),
            updates.RunAsync(shutdown.Token),
            loop.RunAsync(shutdown.Token),
            supervisor.RunAsync(shutdown.Token),
            BrowserStageLoopAsync(browser, shutdown.Token),

            // §2.7's two stages now sit on two terminals, so which one the panel shows is state
            // that has to be reconciled like everything else (§2.2) rather than set once and
            // trusted. It returns on its own the moment it learns this machine has no consoles to
            // switch between, which is every run off a frame.
            screen.RunAsync(shutdown.Token),
            packages.RunAsync(shutdown.Token),
            button.RunAsync(shutdown.Token),

            // §2.7 item 9's console half. It polls a character device twenty times a second and
            // does nothing else, and it keeps running through an outage for the same reason the
            // button does: holding the screen is how somebody standing in front of a stopped frame
            // asks it to try again, and that is exactly the moment no help is coming.
            touch.RunAsync(shutdown.Token),

            // Guide 9's `restart: always`, without Docker underneath it. It is not one of §2.10's
            // four behaviours and deliberately not a fifth: the agent is this child's *parent*, so
            // "it exited" is an event it is told about rather than a health symptom it has to
            // infer. What it shares with §2.10 is the interlock, because the collision is identical
            // — a reconcile pass landing between an exit and the relaunch would otherwise read a
            // blink as drift and reboot the frame for it.
            kiosk.RunAsync(shutdown.Token),
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

    /// <summary>
    /// Drives §2.7's browser stage on the same cadence supervision uses.
    /// </summary>
    /// <remarks>
    /// Its own loop rather than a call inside the supervisor, because the two answer different
    /// questions about the same browser and §2.10 is explicit that they are different
    /// responsibilities: supervision restarts a page that <i>was</i> rendering and stopped; the
    /// stage tears the session down for a page that <i>never</i> rendered. Sharing a tick interval
    /// is convenience, not coupling.
    /// </remarks>
    private async Task BrowserStageLoopAsync(BrowserStage browser, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await browser.TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // §2.7's hard rule is against blank screens, and a stage that died would be the
                // one thing left to notice one. Recording and carrying on is the only response
                // that keeps the rule enforceable.
                _log.Warn($"A browser-stage tick failed and was skipped: {exception.Message}");
            }

            try
            {
                await _clock.DelayAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Listens for <c>SIGUSR1</c> and treats it as a press of the call button.
    /// </summary>
    /// <remarks>
    /// <para>
    /// v1's GPIO daemon did exactly this, and guide 11 step 4 is built on it: the signal handler
    /// and the wire ran the <i>same</i> broadcast, so a simulated press exercised everything except
    /// the two wires. The catalog keeps that behaviour when the daemon disappears, and this is
    /// where it lands — the signal now goes to <c>fl-agent.service</c> rather than to
    /// <c>framelink-gpio.service</c>.
    /// </para>
    /// <para>
    /// <c>PosixSignal</c> has no name for <c>SIGUSR1</c>; the API takes a raw platform signal
    /// number for precisely this case, and on Linux that number is 10. Anywhere it cannot be
    /// registered — Windows, where the whole suite runs — the agent says so once and carries on
    /// without it, because a missing test affordance must never be a reason for a frame not to
    /// start.
    /// </para>
    /// </remarks>
    private PosixSignalRegistration? SimulatedPress(ButtonWatch button)
    {
        const int Sigusr1 = 10;

        try
        {
            return PosixSignalRegistration.Create((PosixSignal)Sigusr1, context =>
            {
                context.Cancel = true;
                _ = button.SimulateAsync(CancellationToken.None);
            });
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException
            or ArgumentOutOfRangeException
            or ArgumentException)
        {
            _log.Warn($"SIGUSR1 could not be registered, so there is no simulated button press on this machine: {exception.Message}");
            return null;
        }
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
