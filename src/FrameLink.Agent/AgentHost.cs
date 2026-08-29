using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FrameLink.Agent.Discovery;
using FrameLink.Agent.Firmware;
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

        // Decision 91, and it is created here — before the update service and before the loop —
        // because both of them have to be able to ask it whether they may restart this machine, and
        // it has to be able to answer that a *previous* process was writing firmware when it died.
        // It reads its durable marker exactly once, at construction, which is why construction has
        // to happen before anything else could have written one.
        // Two forward references, both closed below and both named here because the cycle is real
        // rather than incidental. The catalog's firmware chain needs the flash, and the flash needs
        // the supervisor's view of a call and the update service's view of a pending restart, which
        // are built after the catalog. The approval needs the reconcile loop, because agreeing to a
        // write is what resets the consent gate's budget — and the loop is built from the catalog
        // that the approval is part of.
        ArrayFirmwareFlash? arrayFlash = null;
        ReconcileLoop? reconciler = null;

        var flashWindow = new ArrayFlashWindow(store, _clock);

        // Decision 91's last interlock, and the only one that is a person. It owns the frame's screen
        // for the length of a firmware question and for the length of a write, so it is created
        // beside the window rather than beside the flash: both the console stage and the browser
        // stage read what it publishes, and the interrupted screen below has to be reachable on a
        // frame where nothing else about the flash ever runs.
        var flashApproval = new ArrayFlashApproval(hub, _clock, _log)
        {
            // §2.5 rung 5, on the one screen that offers something other than "try again".
            // `firmware.xvf3800.consent` is a gate, so a frame nobody has agreed to a write on has
            // escalated and stopped — and rung 2 means an escalated resource is not observed again
            // until a person resets it. Without this line the household's yes would land on a frame
            // that had stopped asking, and the write would never start. It is the same reset the
            // Fleet Manager's retry reaches, which is what decision 72 requires of every surface.
            Agreed = _ =>
            {
                _log.Info(
                    "Somebody at this frame agreed to the authorised firmware write, so the consent step's attempt "
                    + "budget is being reset and the frame will carry the write out on its next pass.");
                reconciler?.ResetBudget(ArrayFlashConsentResource.ResourceName);
            },
        };

        if (flashWindow.Interrupted)
        {
            _log.Fail(
                "A firmware write to the microphone unit was in progress when this agent last stopped — "
                + (flashWindow.InterruptedDetail ?? "no detail was recorded")
                + ". No further write will be attempted until somebody has looked at the unit and removed "
                + store.PathOf(ArrayFlashWindow.MarkerFileName) + ".");
        }

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
            AgentBuild.RuntimeIdentifier)
        {
            // Decision 91's update stand-down. A restart cancels the one shutdown token this loop
            // and the reconcile loop share, systemd brings the unit back, and the default
            // KillMode=control-group takes every child in the cgroup with it — including a
            // dfu-util part-way through writing an array's flash. An hourly tick is the single most
            // likely thing on this frame to do that, and nothing stopped it before.
            StandDown = () => flashWindow.Reason,
        };

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

        // Beside the loop for the same reason and by the same decision (90). Which firmware the
        // microphone unit runs is a fact about hardware that nothing on this frame may change on
        // its own, so it is observed and reported and never converged. Its own XvfHost, because the
        // catalog builds one inside the audio block and neither owns the other — the gate that
        // keeps the two of them off the device at the same time is on XvfHost itself.
        var arrayFirmware = new ArrayFirmwareReporter(
            new XvfHost(HostSystemFiles.Instance, HostProcessRunner.Instance, session),
            HostSystemFiles.Instance,
            outbox,
            store,
            _clock,
            _log)
        {
            DeviceId = identity.DeviceId,
        };

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

        // The operator's two buttons, which need the loop and the init system and therefore cannot
        // be built until both exist. Declared here so every local-channel handler is in one place
        // and none of them is wired from somewhere a reader would not think to look.
        FrameRecovery? recovery = null;
        channel.RestartRequested += () =>
            _ = recovery?.RestartAsync("Somebody at the frame", CancellationToken.None);
        channel.ShutdownRequested += () =>
            _ = recovery?.ShutdownAsync("Somebody at the frame", CancellationToken.None);

        // Decision 91's browser half. The same method the console's hold calls, so "yes, go ahead"
        // and "OK, put this away" mean what the screen says they mean whichever surface the panel
        // happens to be showing — and a press can never approve a write the page was no longer
        // displaying, because what it does is decided from the agent's own current screen.
        channel.ArrayFlashAnswered += () => flashApproval.Answer("the browser stage");

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

        var reconcileOptions = new ReconcileOptions
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
        };

        // Decision 79. The floor wraps the boundary rather than sitting inside the loop, so it holds
        // for every caller of the boundary and needs to know nothing about resources, attempts or
        // escalations — which is the whole requirement, because the livelock it exists for is one
        // where nothing is failing. Decision 91 wraps that again, outermost, so a firmware write in
        // progress refuses the reboot before the floor even counts it. Not an exception to §2.4: the
        // resource still has no say and still reboots for every change, and a refusal is an outcome
        // §2.4 already has a first-class answer for — the change is written, it cannot be proven, it
        // spends an attempt and reaches a person.
        //
        // A local rather than an initialiser expression because the loop is no longer the only
        // caller: a supervised loop that ends restarts the frame through this same chain, so a
        // firmware write holds it off and the floor counts it, exactly as they do for a resource.
        var rebootFloor = new RebootFloor(
            new SystemRebootBoundary(new SystemdControl(), _log),
            journal,
            _clock,
            _log,
            reconcileOptions.RebootFloorCount,
            reconcileOptions.RebootFloorWindow);

        var reboots = new RebootHold(rebootFloor, () => flashWindow.Reason, _log);

        // The backstop under the ladder, and the only protection here that does not read the
        // journal. Its size is the ladder's own budget so the two can never promise different
        // numbers of restarts, and its file is counted rather than parsed so that a card which has
        // gone read-only, been wiped, or is truncating every write can only ever cost this frame
        // restarts — never hand it fresh ones.
        var allowance = new RebootAllowance(store, _log, reconcileOptions.AttemptBudget);

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

                // Decisions 91 and 93, moved into the graph. The catalog holds the six rungs of the
                // firmware chain and this is what the last two of them act and ask through; the
                // interlocks themselves never left ArrayFirmwareFlash.
                ArrayFlash = () => arrayFlash,
                FlashApproval = flashApproval,
                FlashWindow = flashWindow,
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
            Reboots = reboots,
            Countdown = countdown,
            Telemetry = outbox,
            Hub = hub,
            Clock = _clock,
            Log = _log,
            Options = reconcileOptions,
        })
        {
            DeviceId = identity.DeviceId,
        };

        // Closes the approval's forward reference. From here a household's yes reaches the ledger.
        reconciler = loop;

        // <b>What "try again" means to everything a person can press.</b> Three callers reach it —
        // the button on the repair page, the hold on the panel, and an inbound retry from the Fleet
        // Manager — and decision 72's rule is that they must not come to mean different things, so
        // there is one function rather than three call sites.
        //
        // It clears three things, and all three are already documented as behaving this way.
        // Decision 67's reasoning covers all of them: a person has arrived. Decision 79's floor
        // grants a fresh window, which its own remarks promise and which nothing had ever called —
        // so a frame that had reached the floor could not be recovered by the button built for it.
        // The restart allowance is refilled for the same reason. And the attempt budgets are
        // cleared last, because that is the one of the three the walk reads on its next pass.
        IReadOnlyList<string> ResetEveryBudget()
        {
            rebootFloor.Forget();
            allowance.Refill();

            // A retry is a person arriving, which is what the firmware question's rest window is
            // waiting for. Without this a frame that had stopped on `firmware.xvf3800.consent` would
            // answer every press with the same screen for six hours, because the budget reset and
            // the question are two different clocks and only one of them was being touched.
            flashApproval.Wake();

            return loop.ResetExhaustedBudgets();
        }

        // The other end of the local channel's retry, now that there is a loop to reset. Nothing
        // is forced: the budget is cleared and the walk picks it up on its next pass, which is
        // exactly what the Fleet Manager's retry does, for the reason RetryRequest records — a
        // transient needing two attempts would defeat a single forced one.
        retryFromFrame = () =>
        {
            var reset = ResetEveryBudget();
            _log.Info(reset.Count > 0
                ? $"Somebody at the frame asked it to try again: {string.Join(", ", reset)}."
                : "Somebody at the frame asked it to try again; nothing had given up.");
        };

        // §2.5 rung 5's two buttons, and the Fleet Manager's remote half of the second one. One
        // object with three callers — the page, the panel hold, and an inbound retry carrying
        // Reboot — so a press at the frame and a press two hundred kilometres away cannot come to
        // mean different things (decision 72's rule, applied to the verb that grew.)
        recovery = new FrameRecovery(new FrameRecoveryServices
        {
            ResetBudgets = ResetEveryBudget,
            SystemControl = new SystemdControl(),
            Log = _log,

            // Decision 91, the same delegate RebootHold is built with above. A firmware write in
            // flight refuses both verbs: a reboot leaves the microphone unit unbootable, and a
            // power-off leaves it that way with no process left to finish the write.
            Held = () => flashWindow.Reason,

            // The refusal's half that is history. The other half — what this frame is refusing right
            // now — is FrameRecovery.Refusal, read live by the status reporter below, and both are
            // projections of the one FrameRefusal composed at the press. An operator's power verb is
            // answered 200 the instant the bytes leave a live socket, so without this pair a refused
            // shutdown and a delivered one are the same picture from a desk.
            //
            // It goes through the outbox like every other event, so a refusal on a frame whose Fleet
            // Manager is unreachable is buffered on the card and drains on reconnect rather than
            // being lost — and the delivered/buffered answer is ignored here, because unlike an
            // escalation nothing about this refusal changes depending on whether anybody has been
            // told yet. The frame has already refused.
            OnRefused = async (refusal, token) => await outbox
                .EventAsync(refusal.ToEvent(identity.DeviceId, _clock.UtcNow), token)
                .ConfigureAwait(false),
        });

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

            // Decision 94. The browser stage draws §2.5 rung 5's two buttons side by side; this
            // surface can read no coordinates, so the two verbs are two lengths of its one gesture
            // — three seconds to restart, ten to switch off, decided when the finger comes off and
            // explained in words by ReconcileVoice.TouchLines while it is still down.
            //
            // This is what the console had been missing, and it was missing it on exactly the frames
            // that have nothing else: a frame with no kiosk stack yet, which is every frame during
            // its first provisioning, could be restarted from the panel and could not be switched
            // off from anywhere a person standing in front of it could see.
            //
            // Both land on the one FrameRecovery above, so a hold here and a click in the Fleet
            // Manager cannot come to mean different things — including the refusal: a firmware
            // write in flight turns both of them down and says what to wait for.
            Restart = () => _ = recovery.RestartAsync("Somebody holding the panel", CancellationToken.None),
            Shutdown = () => _ = recovery.ShutdownAsync("Somebody holding the panel for ten seconds", CancellationToken.None),

            // Decision 91, through the reader that already exists rather than through a second one.
            // A firmware screen outranks the retry whenever one is up, brings its own five-second
            // hold, and answers whatever it is currently saying — so the bar being counted, the
            // label above it and the thing that happens at the end are one decision.
            Ask = () => flashApproval.Prompt is { Affordance: { Length: > 0 } affordance } prompt
                ? new TouchAsk(
                    $"answering the microphone screen ({affordance})",
                    prompt.Hold,
                    () => flashApproval.Answer("a hold on the panel"))
                : null,
        });

        // §2.3's vocabulary, composed from the loop's own state rather than from anything this
        // method knows at construction. The detail is what makes a broken agent legible and does
        // not move; the head in front of it is `loopState` and moves constantly, which is why the
        // reporter is passed as a delegate below and runs a loop of its own further down.
        using var reporter = new AgentStatusReporter(
            hub,
            uplink,
            _log,
            identity.DeviceId,
            $"{AgentBuild.RuntimeIdentifier}, endpoints resolved by {endpoints?.DiscoveredBy ?? "nothing yet"}",

            // Decision 94's device-row half, read live on every compose rather than published into
            // the hub. It answers null the instant the firmware write's window shuts, so the row
            // stops saying this frame is refusing without anything having to remember to clear it —
            // and the write itself is what wakes the reporter, because its progress pump publishes
            // to the hub about once a second for the whole of it and once more with the outcome.
            () => recovery?.Refusal?.Wire);

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

                if (request.Reboot)
                {
                    // The operator's "the reboot can also be triggered from the fleet manager given
                    // the agent is connected". Being here at all *is* the connection: this runs on
                    // the receive loop of a live socket, and a frame that is not holding one was
                    // answered 409 before anything was sent. A named resource is reset first so the
                    // remote form and the local one differ in nothing but their reach.
                    if (!string.IsNullOrWhiteSpace(request.Resource))
                    {
                        loop.ResetBudget(request.Resource);
                    }

                    _ = recovery.RestartAsync("The Fleet Manager", CancellationToken.None);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(request.Resource))
                {
                    loop.ResetBudget(request.Resource);
                    return;
                }

                var reset = ResetEveryBudget();
                if (reset.Count > 0)
                {
                    _log.Info($"The Fleet Manager asked this frame to try again: {string.Join(", ", reset)}.");
                }
            },

            // §2.5 rung 5's other button, pressed two hundred kilometres away (decision 94). Its own
            // kind rather than a flag on the retry, because an agent that did not understand a flag
            // would do the retry — clearing budgets and reconciling on a frame whose operator had
            // just asked for it to be off — whereas an agent that does not understand a kind does
            // nothing, which is the only safe way to misunderstand this message.
            //
            // No budget is touched: a frame that is switched off has not been told to try again, and
            // clearing the ledger here would mean a household that decided to stop found the frame
            // mid-provision the next time somebody switched it on. And it goes through the same
            // FrameRecovery as the panel hold, so a firmware write in flight refuses it and says
            // what to wait for rather than leaving the microphone unit to a power cut.
            OnShutdown = request =>
            {
                if (!string.Equals(request.DeviceId, identity.DeviceId, StringComparison.Ordinal))
                {
                    // Impossible over a socket the server addresses by connection, and the most
                    // expensive thing in this file to be wrong about: the cost is an unrelated frame
                    // switching itself off in somebody's house, with nothing able to reach it again.
                    _log.Warn($"Ignoring a shutdown addressed to {request.DeviceId}; this frame is {identity.DeviceId}.");
                    return;
                }

                _ = recovery.ShutdownAsync("The Fleet Manager", CancellationToken.None);
            },
            HardwareSerial = serial,

            // Free text with a vocabulary head, per AgentHealth. The head is what the Fleet
            // Manager classifies and renders a presence badge from; the parenthesis is for a
            // person reading the row. A delegate rather than a string, because this used to be a
            // constant `Progressing(...)` composed here and never revisited — so the fleet list
            // said every frame was part-way through applying something for as long as its agent
            // stayed up, including frames that had converged and frames that had given up.
            AgentStatusText = reporter.Hello,
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

            // Decision 84. §2.1 puts the app inside this binary and §2.8 replaces this binary
            // hourly, so every update ships a page the running browser has no reason to fetch — and
            // measured on 2026-08-16 it does not: the agent served a new stage, the headline moved
            // because it is composed here, and the half the page draws never appeared at all. This
            // is the fact that makes the two comparable.
            Freshness = new PageFreshness(store, EmbeddedApp.BuildId, _log),
            Stage = () => BrowserStage.Compose(hub.Current, _clock.UtcNow),
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

        // Decision 91. Beside the loop for decision 90's reason unchanged — a firmware write is not
        // an Act any resource may take, because a resource whose Act cannot succeed halts the pass —
        // and single-use, digest-named and interlocked because it is the one operation on this frame
        // that cannot be undone by rewriting the card. Built here, last, because it is the only
        // thing that needs both the supervisor's view of whether somebody is on a call and the
        // update service's view of whether this process is about to restart.
        arrayFlash = new ArrayFirmwareFlash(new ArrayFlashServices
        {
            Tool = new XvfHost(HostSystemFiles.Instance, HostProcessRunner.Instance, session),
            Files = HostSystemFiles.Instance,
            Processes = HostProcessRunner.Instance,
            Installer = new XvfFirmwareInstaller(
                HostSystemFiles.Instance,
                new HttpXvfHostDownload(http, _log),
                _log),
            Window = flashWindow,
            Approval = flashApproval,
            Telemetry = outbox,
            Store = store,
            Clock = _clock,
            Log = _log,
            Values = values,
            DeviceId = identity.DeviceId,
            CallActive = () => supervisor.CallActive,
            RestartPending = () => hub.Current.RestartPending,
        });

        _log.Info($"FrameLink Agent {AgentBuild.Version} ({AgentBuild.RuntimeIdentifier}) starting as {identity.DeviceId}.");

        // Fifteen loops, and none is gated on another finishing. §1.2.2: a frame must provision
        // and self-heal with the server unreachable, so the reconciler runs from the first second
        // whether or not anything ever answers. What adoption gates is the resources that need
        // issued values, and it gates them through the DAG (§2.2) rather than by not running.
        // Four of them are §2.10's second responsibility, §2.7's browser stage, the package
        // inventory and the call button: all keep running through an outage, because that is
        // exactly when no help is coming — and the inventory buffers on disk like everything else
        // on that channel (§4.1). The button in particular has to work with nothing reachable,
        // because pressing it is how somebody in this room starts a call.
        //
        // <b>Named, and the count is checked by a test rather than by this comment.</b> It said
        // twelve while the list held fourteen and was constructed with capacity thirteen, through
        // two commits — which is how a fifteenth came to be missing from it without anybody
        // noticing. The names are not decoration: a loop that ends is reported by name and its
        // purpose becomes the "expected" half of the delta on the frame's own screen.
        var running = new List<AgentLoop>(15)
        {
            new("console-stage", "paint this frame's own screen for as long as the agent runs", stage.RunAsync(shutdown.Token)),
            new("control-link", "keep this frame's connection to the Fleet Manager", link.RunAsync(shutdown.Token)),

            // The other half of the hello's self-report. §4.2 puts a handshake on every connect
            // and a converged frame never reconnects, so without this the Fleet Manager's row
            // would keep whatever the loop was doing in the seconds after the last reboot for the
            // whole of a frame's uptime. It sends nothing while nothing changes.
            new("status-reporter", "keep the Fleet Manager's picture of this frame current", reporter.RunAsync(shutdown.Token)),
            new("self-update", "check hourly for the version this fleet serves", updates.RunAsync(shutdown.Token)),
            new("reconcile", "keep every setting on this frame as it should be", loop.RunAsync(shutdown.Token)),
            new("supervision", "keep the product running once it is up", supervisor.RunAsync(shutdown.Token)),
            new("browser-stage", "make sure the panel is never blank", BrowserStageLoopAsync(browser, shutdown.Token)),

            // §2.7's two stages now sit on two terminals, so which one the panel shows is state
            // that has to be reconciled like everything else (§2.2) rather than set once and
            // trusted. It returns on its own the moment it learns this machine has no consoles to
            // switch between, which is every run off a frame.
            new("screen-handover", "keep the panel showing whichever of the two stages should own it", screen.RunAsync(shutdown.Token)),
            new("package-inventory", "tell the Fleet Manager what is installed on this frame", packages.RunAsync(shutdown.Token)),
            new("array-firmware-report", "tell the Fleet Manager which firmware the microphone unit runs", arrayFirmware.RunAsync(shutdown.Token)),

            // Decision 91's other half, and it no longer writes anything. The write is
            // `firmware.xvf3800.written`'s Act, in the graph, where §2.4's reboot then proves across
            // a boot that the array came back on the firmware it was given. What is left here is the
            // conversation with the person in the room: it reads one setting a minute, puts the
            // firmware question on the panel when everything else is ready, refreshes it, and takes
            // it away within seconds of a call starting — three things the reconcile pass cannot do,
            // because its Observe may have no side effects and it runs every five minutes.
            new("array-firmware-flash", "ask whoever is at this frame to agree to an authorised firmware write", arrayFlash.RunAsync(shutdown.Token)),
            new("call-button", "turn a press of the button on this frame into a call", button.RunAsync(shutdown.Token)),

            // §2.7 item 9's console half. It polls a character device twenty times a second and
            // does nothing else, and it keeps running through an outage for the same reason the
            // button does: holding the screen is how somebody standing in front of a stopped frame
            // asks it to try again, and that is exactly the moment no help is coming.
            new("panel-touch", "let somebody standing here restart this frame by holding the screen", touch.RunAsync(shutdown.Token)),

            // Guide 9's `restart: always`, without Docker underneath it. It is not one of §2.10's
            // behaviours and deliberately not another one: the agent is this child's *parent*, so
            // "it exited" is an event it is told about rather than a health symptom it has to
            // infer. What it shares with §2.10 is the interlock, because the collision is identical
            // — a reconcile pass landing between an exit and the relaunch would otherwise read a
            // blink as drift and reboot the frame for it.
            new("immich-kiosk", "keep the slideshow child running", kiosk.RunAsync(shutdown.Token)),

            // <b>The fifteenth, which nothing watched at all.</b> Its accept loop is started by
            // origin.Start() above on a fire-and-forget task against the origin's own token, and
            // the only thing that ever awaited it was DisposeAsync — so an accept that failed took
            // the frame's local HTTP server away permanently and silently, leaving the product app
            // and the repair screen with nothing to fetch or check in to. It is the same shape as
            // every other loop now: it runs until the agent stops, and if it ends first that is a
            // failure a person hears about.
            new("local-origin", "serve the product app and the repair screen to this frame's browser", origin.RunAsync(shutdown.Token)),
        };

        var startedUtc = _clock.UtcNow;
        AgentLoop? ended;

        try
        {
            ended = await FirstToEndAsync(running, shutdown.Token).ConfigureAwait(false);

            if (ended is null)
            {
                // The ordinary path: the agent was asked to stop and every loop is unwinding.
                await Task.WhenAll(running.Select(item => item.Task)).ConfigureAwait(false);
                return restart.Requested ? ExitCodes.RestartToApplyUpdate : ExitCodes.Success;
            }
        }
        catch (OperationCanceledException)
        {
            // Asked to stop, or standing aside for a new binary.
            return restart.Requested ? ExitCodes.RestartToApplyUpdate : ExitCodes.Success;
        }

        // A loop ended while the agent was still running. <b>Returning counts, not only
        // throwing.</b> A loop that exits cleanly — a swallowed cancellation, a break on an
        // unexpected state, a task that completed because its input closed — used to disappear with
        // nothing said anywhere, which made the watched fourteen almost as unwatched as the
        // fifteenth was.
        var why = DescribeEnd(ended);
        _log.Fail($"The '{ended.Name}' loop ended while the agent was still running: {why}");

        var ranFor = _clock.UtcNow - startedUtc;

        var verdict = AgentLoopFailures.Record(
            journal,
            reconcileOptions,
            ended.Name,
            ended.Purpose,
            why,
            ranFor,
            notified: false);

        // The same screen with the same information, through the same ledger every resource uses:
        // ReconcileLoop.HasStopped now reads this entry, decision 68 stops the pass around it, and
        // the row is rendered from the ledger by the walk's own orphan path. Published here as well
        // as journalled, because the loop that ended may be the one that would have published it.
        void Report(AgentLoopVerdict current) => hub.Publish(status => status with
        {
            Resources = WithRow(status.Resources, current.Row),
            Drifted = true,
            Reconcile = status.Reconcile with
            {
                LoopState = current.Stopped ? LoopStateNames.Escalated : LoopStateNames.Reconciling,
                Resource = current.Resource,
                Phase = null,
                Attempt = current.Attempts,
                AttemptBudget = current.Budget,
                Countdown = null,
                Escalations = current.Row.Escalations,
                AdminNotified = false,
            },
        });

        async Task AnnounceAsync(AgentLoopVerdict current)
        {
            try
            {
                await outbox.EventAsync(
                    new DeviceEvent
                    {
                        DeviceId = identity.DeviceId,
                        Kind = current.Stopped ? DeviceEventKinds.Escalation : DeviceEventKinds.Drift,
                        OccurredUtc = _clock.UtcNow,
                        Resource = current.Resource,
                        Summary = $"{AgentLoopFailures.Detected} {AgentLoopFailures.WhyItMatters}",
                        Delta = current.Row.Delta,
                        Attempts = current.Attempts,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // The buffer this writes to is on the same card the journal is on, and the failure
                // being reported may well be the reason it cannot be written. A second exception
                // raised here would replace the reason in the log with a consequence of it, which is
                // exactly how the original twenty-nine-minute stall stayed unexplained.
                _log.Fail($"The failure of '{ended.Name}' could not be reported to the Fleet Manager: {exception.Message}");
            }
        }

        // <b>Reported before the restart is asked for, never after.</b> `systemctl reboot` returns
        // as soon as the job is queued and the machine is gone a second or two later, which is not
        // long enough to be sure a write to the telemetry buffer has landed. A frame that announced
        // the failure only after asking for the restart would lose the announcement for exactly the
        // two attempts that take one.
        Report(verdict);
        await AnnounceAsync(verdict).ConfigureAwait(false);

        // <b>Attempts one and two reboot the frame; the third does not.</b> The decision is a
        // function of its own rather than a block here, because it is the one behaviour on this path
        // that a test can drive end to end — wipe the journal before every boot and assert that the
        // cascade still stops after three, which is the whole of what the operator asked for.
        var decided = await AgentLoopFailures
            .RestartOrStopAsync(
                journal,
                reconcileOptions,
                allowance,
                reboots,
                verdict,
                ranFor,
                why,
                _log,
                CancellationToken.None)
            .ConfigureAwait(false);

        if (decided.Refusal is { Length: > 0 })
        {
            // The restart was refused, so the ladder ended there instead of on the third attempt.
            // Both the row and the event changed, so both are sent again — the first pair said a
            // loop had died, and this pair says the frame is not coming back on its own.
            Report(decided.Verdict);
            await AnnounceAsync(decided.Verdict).ConfigureAwait(false);
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Somebody pressed a button, systemd stopped the unit, the machine is going down for the
            // restart asked for above, or an update is standing this process aside.
        }

        // The surviving loops each own something — a browser session, a child process, a socket, a
        // GPIO claim — and this is the one path that used to leave them to process death. It costs
        // nothing on the way to a reboot, and it is what stops a shutdown from being a kill.
        await SettleAsync(running).ConfigureAwait(false);

        return restart.Requested ? ExitCodes.RestartToApplyUpdate : ExitCodes.Unrecoverable;
    }

    /// <summary>
    /// One supervised loop: what it is called, what it is for, and the task running it.
    /// </summary>
    /// <param name="Name">
    /// A stable id, used as the ledger key <c>agent.loop.&lt;name&gt;</c> and shown in the technical
    /// block on the frame's screen.
    /// </param>
    /// <param name="Purpose">
    /// What this loop is expected to be doing, written for somebody with no computer experience. It
    /// becomes the <i>expected</i> half of the delta when the loop ends — "expected keep every
    /// setting on this frame as it should be, observed: it returned while the agent was still
    /// running" — which is the same shape §2.5 rung 2 records for every other failure.
    /// </param>
    /// <param name="Task">The running loop.</param>
    public sealed record AgentLoop(string Name, string Purpose, Task Task);

    /// <summary>
    /// Waits for the first loop to end, and says whether that ending was a failure.
    /// </summary>
    /// <returns>
    /// The loop that ended while the agent was still running, or <see langword="null"/> when the
    /// agent's own shutdown was requested first.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>A plain <see cref="Task.WhenAll(IEnumerable{Task})"/> here is a silence, and it cost a
    /// frame twenty-nine minutes.</b> Measured 2026-08-16: the reconcile loop threw on its first
    /// reboot request after an upgrade, its task went to Faulted — and <c>WhenAll</c> does not
    /// surface an exception until <i>every</i> task has finished. The other loops run for the life
    /// of the frame, so the exception sat inside a completed task with nothing waiting to read it.
    /// The process stayed up, the uplink stayed connected, the Fleet Manager went on reporting the
    /// device online, the console stage went on painting, and the one loop that converges anything
    /// was gone.
    /// </para>
    /// <para>
    /// <b>Ending is now a failure too, and that is the change.</b> The previous shape triggered on a
    /// fault and only a fault, on the reasoning that a loop which returns has finished its work.
    /// That reasoning is wrong for every loop here: each of them is supposed to run until the frame
    /// is switched off, so a return means a swallowed cancellation, a <c>break</c> on an unexpected
    /// state, or a task that completed because its input closed — a whole responsibility gone, with
    /// nothing said. It made the watched loops almost as unwatched as the accept loop nobody was
    /// watching at all.
    /// </para>
    /// <para>
    /// <b>The one legitimate ending is shutdown, and it is distinguished explicitly rather than by
    /// timing.</b> Every loop returns when the agent is stopping, so the question this asks after
    /// the first one ends is whether the agent's own shutdown token has been signalled — a fact,
    /// not a race. The other case that used to end a loop legitimately was
    /// <c>ScreenHandover</c> returning on a machine with no virtual terminals, which is every run
    /// off a frame; it now waits for cancellation instead of returning, so there is exactly one
    /// rule and no exceptions to it.
    /// </para>
    /// <para>
    /// It does not cancel anything. The caller decides what an ending means — stand down and let
    /// systemd bring the agent back, or hold the screen because the budget is gone — and cancelling
    /// here would take the surviving loops down before that decision could be made, including the
    /// ones that paint the screen the decision has to appear on.
    /// </para>
    /// </remarks>
    public static async Task<AgentLoop?> FirstToEndAsync(IReadOnlyList<AgentLoop> running, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(running);

        if (running.Count == 0)
        {
            return null;
        }

        var tasks = new List<Task>(running.Count);
        foreach (var loop in running)
        {
            tasks.Add(loop.Task);
        }

        var first = await Task.WhenAny(tasks).ConfigureAwait(false);

        if (shutdown.IsCancellationRequested)
        {
            return null;
        }

        return running[tasks.IndexOf(first)];
    }

    /// <summary>How a loop ended, in one sentence a person can read.</summary>
    /// <remarks>
    /// Reading <see cref="Task.Exception"/> is also what marks a faulted task observed, which
    /// matters here: the alternative is an <c>UnobservedTaskException</c> raised by a finaliser
    /// minutes later, attached to nothing.
    /// </remarks>
    public static string DescribeEnd(AgentLoop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);

        if (loop.Task.Exception?.InnerException is { } fault)
        {
            return $"{fault.GetType().Name}: {fault.Message}";
        }

        return loop.Task.IsCanceled
            ? "it was cancelled while the agent was still running"
            : "it returned while the agent was still running";
    }

    /// <summary>The list with <paramref name="row"/> in it, replacing any row of the same name.</summary>
    private static List<ResourceStatus> WithRow(IReadOnlyList<ResourceStatus> rows, ResourceStatus row)
    {
        var next = new List<ResourceStatus>(rows.Count + 1);
        var replaced = false;

        foreach (var existing in rows)
        {
            if (string.Equals(existing.Name, row.Name, StringComparison.Ordinal))
            {
                next.Add(row);
                replaced = true;
            }
            else
            {
                next.Add(existing);
            }
        }

        if (!replaced)
        {
            next.Add(row);
        }

        return next;
    }

    /// <summary>
    /// Gives the surviving loops their ordinary shutdown path before the process goes.
    /// </summary>
    /// <remarks>
    /// Each of them owns something — a browser session, a child process, a socket, a GPIO claim —
    /// that should be released rather than left to process death. Faults are swallowed: the agent
    /// is already standing down over a different failure, and a second one raised on the way out
    /// would replace the reason in the log with a consequence of it.
    /// </remarks>
    private static async Task SettleAsync(IReadOnlyList<AgentLoop> running)
    {
        foreach (var loop in running)
        {
            try
            {
                await loop.Task.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Recorded by whoever raised it; this is the unwind, not the diagnosis.
            }
        }
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
            // <b>A command that never answered is the exception to "carry on", and it is the
            // exception because of the rule rather than despite it.</b> Recording and carrying on
            // keeps §2.7 enforceable for a tick that failed and can be tried again in five seconds.
            // It does not for a systemctl that has stopped answering: this stage is made of those
            // calls, so retrying one forever leaves the exact state §2.7 forbids — a broken desktop,
            // no teardown, no console fallback — with nothing counting the failures. Leaving the
            // loop hands it to the supervision below, which records agent.loop.browser-stage against
            // the same budget of three, stands the agent down twice and holds the screen on the
            // third with the command and its deadline in the delta.
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException
                and not ProcessTimeoutException)
            {
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
