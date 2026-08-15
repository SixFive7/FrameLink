using System.Diagnostics;
using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Supervise;

namespace FrameLink.Agent.Kiosk;

/// <summary>The environment the Immich Kiosk child runs with.</summary>
/// <remarks>
/// <para>
/// Every field is one of guide 9's Compose settings, carried across unchanged in meaning. The
/// variable names are not guessed: <c>KIOSK_IMMICH_URL</c>, <c>KIOSK_IMMICH_API_KEY</c>,
/// <c>KIOSK_OFFLINE_MODE_ENABLED</c>, <c>KIOSK_OFFLINE_MODE_NUMBER_OF_ASSETS</c> and
/// <c>KIOSK_PORT</c> all appear verbatim in the v0.42.0 executable's string table, which is where
/// they were read from.
/// </para>
/// <para>
/// <b>The offline cache is a working-directory fact, not a setting.</b> The v0.42.0 binary holds
/// the literal <c>./offline-assets</c> — a relative path — so the cache lands beside whatever the
/// child's working directory is. Under Docker that came out as <c>/offline-assets</c> because the
/// image's working directory was the root and the Compose file mounted a volume there; under v2 it
/// is <see cref="WorkingDirectory"/><c>/offline-assets</c>. That is why
/// <c>kiosk.offline-cache.dir</c> is a directory resource and not another environment variable.
/// </para>
/// </remarks>
public sealed record KioskProcessSettings
{
    /// <summary>Where the child's working directory is, and therefore where its cache goes.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The Immich server, from fleet setting <c>immich.serverUrl</c>.</summary>
    public string ImmichUrl { get; init; } = string.Empty;

    /// <summary>The read-only Immich API key, from fleet setting <c>immich.apiKey</c>.</summary>
    /// <remarks>
    /// A secret (§2.9). It reaches the child through its environment block and nothing else — never
    /// a command-line argument, which any process on the frame can read out of
    /// <c>/proc/&lt;pid&gt;/cmdline</c>, and never a log line.
    /// </remarks>
    public string ImmichApiKey { get; init; } = string.Empty;

    /// <summary>Whether Kiosk downloads and caches assets.</summary>
    public bool OfflineModeEnabled { get; init; } = true;

    /// <summary>How many assets that cache holds.</summary>
    public int OfflineAssetCount { get; init; } = 200;

    /// <summary>The port Kiosk serves on.</summary>
    public int Port { get; init; } = KioskProcess.DefaultPort;

    /// <summary>Whether enough has been issued for the child to be worth starting.</summary>
    /// <remarks>
    /// §3.3 gives a pending device nothing, so on an unadopted frame there is no Immich URL and no
    /// key. Starting Kiosk anyway would produce a process that answers <c>401</c> forever and a
    /// supervision loop restarting it, which is noise standing in for the one honest statement the
    /// frame should be making — "adopt me".
    /// </remarks>
    public bool IsComplete => ImmichUrl.Length > 0 && ImmichApiKey.Length > 0;

    /// <summary>The environment block, in the order a person would read it.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Environment =>
    [
        new("KIOSK_IMMICH_URL", ImmichUrl),
        new("KIOSK_IMMICH_API_KEY", ImmichApiKey),
        new("KIOSK_OFFLINE_MODE_ENABLED", OfflineModeEnabled ? "true" : "false"),
        new("KIOSK_OFFLINE_MODE_NUMBER_OF_ASSETS", OfflineAssetCount.ToString(CultureInfo.InvariantCulture)),
        new("KIOSK_PORT", Port.ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>The block as it may be written down — the key described, never quoted.</summary>
    public string Describe() => string.Join(
        ' ',
        Environment.Select(pair => pair.Key switch
        {
            "KIOSK_IMMICH_API_KEY" => $"{pair.Key}=<{(pair.Value.Length > 0 ? "set" : "unset")}>",
            _ => $"{pair.Key}={pair.Value}",
        }));
}

/// <summary>
/// <b>Immich Kiosk as a supervised child process of the agent</b> (§2.1, decision 41).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what takes Docker off the frame.</b> Guide 9's container, its Compose file, its
/// <c>restart: always</c> policy and the Docker Engine underneath all collapse into this class: the
/// agent is the parent, so it owns the child's lifetime directly and gets its exit code the instant
/// it dies. Removing Docker deletes the corrupt-network-store failure class that began the August
/// 2026 incident chain rather than repairing it — which is why <c>docker-selfheal</c> has nothing
/// left to act on (§2.10).
/// </para>
/// <para>
/// <b>A different shape of supervision from the browser's, and the difference is structural.</b>
/// §2.10's four behaviours restart <c>chromium-kiosk.service</c>, a systemd <i>user</i> unit with
/// its own <c>Restart=always</c>: systemd owns that lifetime, the agent is not its parent, and what
/// supervision contributes is a <i>health trigger</i> — a memory ceiling, a clock, a silent local
/// channel — for a restart systemd would never take on its own. Nothing here has a health trigger.
/// The agent is the parent, "the child exited" is an event it is told about rather than a symptom
/// it has to infer, and the response is to start it again. That is §3.7's shape — the Fleet Manager
/// supervising <c>livekit-server</c> "as a child process with the same restart-and-report
/// discipline" — not §2.10's, and the fifth supervised behaviour §2.10 does not list is deliberately
/// not being added here.
/// </para>
/// <para>
/// <b>What the two shapes do share is the interlock, and they have to.</b> §2.10's collision is
/// exactly reproducible here: <c>kiosk.process.supervised</c> observes "the child is alive and
/// answering", a reconcile pass that lands in the second between an exit and the relaunch sees it
/// down, and §2.6 then stops the product and reboots a frame whose only fault was a process
/// blinking. So a relaunch opens a window over the two resources it disturbs, exactly as a browser
/// restart opens one over <c>unit.chromium-kiosk.running-matches-content</c>, and
/// <c>supervision.recoveryDeadline</c> is still the boundary: a child that has not come back inside
/// it stops being a transient and becomes ordinary drift, with everything §2.6 and §2.7 prescribe.
/// </para>
/// <para>
/// <b>Started from the host, not only from its resource</b>, for the same reason
/// <c>LocalOrigin</c> is: the child is this process's child, so it cannot survive the reboot every
/// resource takes (§2.4). If starting it were left to the Act, the resource would find it down on
/// every boot, act, reboot and find it down again — a loop that never converges. Started at every
/// process start, the resource becomes what it should be: an assertion that the slideshow is
/// answering, with an Act for the case that can actually fail.
/// </para>
/// </remarks>
public sealed class KioskProcess : IAsyncDisposable
{
    /// <summary>
    /// The behaviour id a relaunch's interlock window carries.
    /// </summary>
    /// <remarks>
    /// Deliberately not one of §2.10's four behaviour ids: those name health triggers evaluated by
    /// <c>Supervisor</c>, and this names a parent noticing its child is gone. Sharing the interlock
    /// is what they have in common; sharing the list would claim a fifth behaviour the specification
    /// does not have.
    /// </remarks>
    public const string SupervisionBehaviour = "kiosk-child";

    /// <summary>Guide 9's port, fixed by the catalog so two places cannot disagree.</summary>
    public const int DefaultPort = 3000;

    /// <summary>The directory under the state store the child lives and works in.</summary>
    public const string DirectoryName = "kiosk";

    /// <summary>The executable's name inside that directory.</summary>
    public const string BinaryName = "immich-kiosk";

    /// <summary>The cache directory Kiosk creates relative to its working directory.</summary>
    public const string OfflineCacheName = "offline-assets";

    /// <summary>How long a stop waits for the child to go before it is killed.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    private readonly KioskProcessServices _services;
    private readonly Lock _gate = new();

    private Process? _child;
    private SupervisionWindow? _window;
    private int _launches;

    /// <summary>Creates the supervisor of one Immich Kiosk child.</summary>
    public KioskProcess(KioskProcessServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>The resources a relaunch transiently disturbs (§2.10 clause 2).</summary>
    /// <remarks>
    /// Two, and both are needed. <c>kiosk.process.supervised</c> is the resource that reads whether
    /// the child is alive at all, and <c>kiosk.listen-address</c> reads the socket that goes with
    /// it — a window over the first alone would leave the second narrating a repair for the same
    /// blink. The unit-file analogues do not appear because there are none: this child has no unit.
    /// </remarks>
    public static IReadOnlyList<string> DisturbedResources { get; } =
    [
        "kiosk.process.supervised",
        "kiosk.listen-address",
    ];

    /// <summary>Where the executable lives.</summary>
    public string BinaryPath => Path.Combine(WorkingDirectory, BinaryName);

    /// <summary>The child's working directory, and therefore its cache's parent.</summary>
    public string WorkingDirectory => Path.Combine(_services.Store.Root, DirectoryName);

    /// <summary>Where Kiosk keeps the offline cache.</summary>
    public string OfflineCachePath => Path.Combine(WorkingDirectory, OfflineCacheName);

    /// <summary>The port the child is asked to serve on.</summary>
    public int Port => _services.Settings().Port;

    /// <summary>Whether a child is running right now.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _child is { HasExited: false };
            }
        }
    }

    /// <summary>The child's process id, or null when nothing is running.</summary>
    public int? Pid
    {
        get
        {
            lock (_gate)
            {
                return _child is { HasExited: false } child ? child.Id : null;
            }
        }
    }

    /// <summary>Why the last start did not take, or null.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>How many times a child has been launched since this process started.</summary>
    public int Launches
    {
        get
        {
            lock (_gate)
            {
                return _launches;
            }
        }
    }

    /// <summary>
    /// Starts the child if it is not running, and reports whether one is running afterwards.
    /// </summary>
    /// <remarks>
    /// Idempotent by construction, because everything that calls it — the host at startup, the
    /// resource's Act, the relaunch loop — may be racing one of the others.
    /// </remarks>
    public bool Start()
    {
        lock (_gate)
        {
            if (_child is { HasExited: false })
            {
                return true;
            }

            var settings = _services.Settings();

            if (!settings.IsComplete)
            {
                LastFailure = "this frame has not been issued an Immich server address and key yet";
                return false;
            }

            if (!File.Exists(BinaryPath))
            {
                LastFailure = $"there is no Immich Kiosk binary at {BinaryPath}";
                return false;
            }

            // Nothing is redirected, deliberately and twice over. Redirecting without draining is
            // the pipe-buffer deadlock HostProcessRunner documents: Kiosk logs on every asset it
            // fetches, 64 kB of unread stdout later it blocks on write(2) for ever, and the frame
            // shows a slideshow that has silently stopped advancing. Draining would fix that and
            // buy nothing, because inheriting is *better*: the agent's own stdout is the journal,
            // so Kiosk's log lines land beside the agent's under one `journalctl -u fl-agent`,
            // which is what replaces `docker logs immich-kiosk` from guide 9 step 4.
            var start = new ProcessStartInfo(BinaryPath)
            {
                WorkingDirectory = settings.WorkingDirectory,
                UseShellExecute = false,
            };

            foreach (var pair in settings.Environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }

            try
            {
                var child = Process.Start(start);
                if (child is null)
                {
                    LastFailure = $"{BinaryPath} did not start";
                    return false;
                }

                _child = child;
                _launches++;
                LastFailure = null;

                _services.Log.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Immich Kiosk started as pid {child.Id} on port {settings.Port}. {settings.Describe()}"));

                return true;
            }
            catch (Exception exception)
                when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                LastFailure = exception.Message;
                _services.Log.Warn($"Immich Kiosk could not be started: {exception.Message}");
                return false;
            }
        }
    }

    /// <summary>Ends the child and waits a short grace period for it to go.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Process? child;

        lock (_gate)
        {
            child = _child;
            _child = null;
        }

        if (child is null)
        {
            return;
        }

        try
        {
            if (!child.HasExited)
            {
                // Kill(entireProcessTree: false): the child is a single static Go binary with no
                // helpers, so there is no tree to take down, and a true here would reach into
                // whatever it happens to have spawned for a shell-out.
                //
                // On Linux this is SIGKILL, and the base class library offers no way to send a
                // child SIGTERM — so a graceful stop would mean shelling out to `kill`, and it is
                // not worth it: the only state Kiosk holds is the offline cache, which it rebuilds
                // from Immich, and a half-written cache file is a photo that gets fetched again.
                child.Kill(entireProcessTree: false);

                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(StopGrace);
                await child.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or OperationCanceledException
                or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or refusing to go inside the grace period. Either way the handle is
            // released below and the next Start makes a new one.
        }
        finally
        {
            child.Dispose();
        }
    }

    /// <summary>Stops and starts, as the resource's Act and as the relaunch.</summary>
    public async Task<bool> RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        return Start();
    }

    /// <summary>
    /// Keeps a child running for as long as the agent does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole of <c>restart: always</c>, and no more than that: this loop answers "the child
    /// exited", which is the only fault a parent can see for free. "The child is up and not
    /// answering" is a different fault with a different reading, and it belongs to
    /// <c>kiosk.process.supervised</c>, which makes a real request. What this adds over Docker's
    /// policy is the interlock window, so that the second between an exit and the relaunch is not
    /// read as drift by the resource watching the same process.
    /// </para>
    /// <para>
    /// <b>Nothing here backs off, and that is deliberate.</b> §2.5's backoff exists "to stop a
    /// reboot loop from wearing the hardware"; a child process that will not stay up wears nothing,
    /// and the frame it is failing on is showing a blank slideshow the whole time. What bounds the
    /// damage instead is the window's <c>supervision.recoveryDeadline</c>: a child still not
    /// answering when it expires stops being a transient and becomes ordinary drift, which is the
    /// path that reaches a person.
    /// </para>
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SuperviseOnce();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                _services.Log.Warn($"Supervising Immich Kiosk failed and was retried: {exception.Message}");
            }

            try
            {
                await _services.Clock.DelayAsync(_services.Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// One pass of the relaunch loop, exposed so a test does not have to race a clock.
    /// </summary>
    /// <remarks>
    /// Synchronous, and that is the shape rather than an oversight: every step is a handle check, a
    /// <c>fork</c>/<c>exec</c> or a field, and none of them waits on anything. The loop around it is
    /// asynchronous because the <i>interval</i> is.
    /// </remarks>
    public void SuperviseOnce()
    {
        Process? exited = null;

        lock (_gate)
        {
            if (_child is { HasExited: true } dead)
            {
                exited = dead;
                _child = null;
            }
        }

        if (exited is not null)
        {
            var code = ExitCodeOf(exited);
            exited.Dispose();

            _services.Log.Warn(string.Create(
                CultureInfo.InvariantCulture,
                $"Immich Kiosk exited ({code}). Starting it again; the slideshow is blank until it answers."));

            OpenWindow();
        }

        if (IsRunning)
        {
            CloseWindowIfRecovered();
            return;
        }

        if (!_services.Settings().IsComplete)
        {
            // Nothing to start and nothing wrong: §3.3 has given this frame no Immich values, and
            // the adoption edge on kiosk.config.* is what says so on the screen.
            return;
        }

        if (exited is null && _window is null && Launches > 0)
        {
            // Down without having been observed exiting — killed out from under the agent, or a
            // start that never took. Same treatment, because the frame looks identical.
            OpenWindow();
        }

        Start();
        CloseWindowIfRecovered();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private static int ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private void OpenWindow()
    {
        if (_services.Interlock is not { } interlock)
        {
            return;
        }

        if (_window is not null)
        {
            return;
        }

        _window = interlock.Open(
            SupervisionBehaviour,
            DisturbedResources,
            _services.Clock.UtcNow,
            _services.RecoveryDeadline());
    }

    private void CloseWindowIfRecovered()
    {
        if (_window is not { } window || _services.Interlock is not { } interlock)
        {
            return;
        }

        if (!IsRunning)
        {
            return;
        }

        interlock.Close(window);
        _window = null;
    }
}

/// <summary>Everything the child needs to be run and watched.</summary>
public sealed record KioskProcessServices
{
    /// <summary>The agent's state directory, which the child lives inside (§2.1).</summary>
    public required IStateStore Store { get; init; }

    /// <summary>Source of time and of waiting.</summary>
    public required IAgentClock Clock { get; init; }

    /// <summary>Where launches, exits and refusals are recorded.</summary>
    public required IAgentLog Log { get; init; }

    /// <summary>The current environment, read fresh so a settings change is picked up on restart.</summary>
    public required Func<KioskProcessSettings> Settings { get; init; }

    /// <summary>The interlock shared with the reconciler, where there is one.</summary>
    /// <remarks>
    /// Optional only so that a test can drive the child without one. On a frame it is always
    /// present, because without it every relaunch is drift.
    /// </remarks>
    public SupervisionInterlock? Interlock { get; init; }

    /// <summary>How long a relaunch has before it stops being a transient (§2.10 clause 3).</summary>
    public Func<TimeSpan> RecoveryDeadline { get; init; } = () => TimeSpan.FromMinutes(2);

    /// <summary>How often the relaunch loop looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);
}
