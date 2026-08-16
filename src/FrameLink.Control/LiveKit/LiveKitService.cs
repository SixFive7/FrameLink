using System.Runtime.InteropServices;
using FrameLink.Control.Storage;

namespace FrameLink.Control.LiveKit;

/// <summary>
/// Which LiveKit this fleet's tokens are for, and what signs them.
/// </summary>
/// <remarks>
/// <para>
/// The escape hatch of §3.7 lives here and nowhere else: "an operator with an existing LiveKit
/// can point the Fleet Manager at it." Every other class in this folder either only matters in
/// the bundled case (the installer, the config file, the child process) or does not care which
/// case it is in (the token minter, the provisioner). This one is where the two paths meet, so
/// it is the only place that has to know they exist.
/// </para>
/// <para>
/// <b>The credential is resolved once and cached, and the difference between the two modes is a
/// branch.</b> External means the operator's own key and secret, taken from the environment and
/// never written anywhere — this Fleet Manager has no business persisting somebody else's
/// credential, and if the variables change it should follow, not remember. Bundled means the
/// generated pair from the database, which is the one this server owns and rotates.
/// </para>
/// </remarks>
public sealed class LiveKitDeployment(LiveKitOptions options, ILiveKitStore store) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LiveKitCredential? _cached;

    /// <summary>The options this deployment was built from.</summary>
    public LiveKitOptions Options { get; } = options;

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// The key and secret to sign with, generating the bundled pair on first use.
    /// </summary>
    /// <returns>Null when calling is switched off, or an external server was named without one.</returns>
    public async Task<LiveKitCredential?> CredentialAsync(CancellationToken cancellationToken)
    {
        if (Options.Mode is LiveKitMode.Disabled)
        {
            return null;
        }

        if (Options.Mode is LiveKitMode.External)
        {
            return Options.ExternalKey.Length > 0 && Options.ExternalSecret.Length > 0
                ? new LiveKitCredential(Options.ExternalKey, Options.ExternalSecret, DateTimeOffset.MinValue)
                : null;
        }

        if (_cached is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // §3.2: "LiveKit's key and secret are generated automatically." First run, first
            // token, first anything — whichever comes first pays for the generation and the rest
            // read it back.
            _cached ??= await store.EnsureAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces the signing secret. Bundled only — this server does not own an operator's.</summary>
    public async Task<LiveKitCredential?> RotateSecretAsync(CancellationToken cancellationToken)
    {
        if (Options.Mode is not LiveKitMode.Bundled)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = await store.RotateSecretAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>What the console renders about the call server.</summary>
/// <param name="Mode">Bundled, external or disabled.</param>
/// <param name="Version">The pinned LiveKit version, when bundled.</param>
/// <param name="Ready">Whether a frame issued a token right now could actually place a call.</param>
/// <param name="Step">What the bundled path is doing, or the last thing it did.</param>
/// <param name="Problems">Everything an operator has to fix, in plain sentences.</param>
/// <param name="Process">The supervised child, when there is one.</param>
public sealed record LiveKitRuntimeState(
    LiveKitMode Mode,
    string Version,
    bool Ready,
    string Step,
    IReadOnlyList<string> Problems,
    LiveKitProcessState? Process);

/// <summary>
/// Installs, configures and supervises the bundled <c>livekit-server</c> (§3.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything slow happens after the server is already serving.</b> A first run fetches
/// roughly 17 MB and writes a 50 MB executable, and doing that inside <c>StartAsync</c> would
/// mean a Fleet Manager that does not answer its own console — including the §3.2 setup page
/// that explains what is missing — until GitHub has finished sending. So this is a
/// <c>BackgroundService</c>: Kestrel is listening, frames can connect, adopt and reconcile, and
/// the call server arrives when it arrives. A frame adopted in that window is issued its token
/// immediately, because the token depends on the credential rather than on the binary.
/// </para>
/// <para>
/// <b>The bundled path runs on Linux and refuses elsewhere, out loud.</b> The pinned assets are
/// ELF binaries; there is no Windows or macOS build in the pin, deliberately (§3.1 ships one
/// Linux container). A developer running the Fleet Manager on a workstation therefore gets a
/// named refusal on the status route and a fully working everything-else, which is the same
/// treatment §3.2 gives a missing password — rather than a crash, a silent no-op, or a
/// half-installed directory.
/// </para>
/// </remarks>
public sealed class LiveKitService(
    LiveKitOptions options,
    LiveKitDeployment deployment,
    CallProvisioning provisioning,
    ILiveKitDownload download,
    TimeProvider clock,
    ILogger<LiveKitService> logger) : BackgroundService
{
    private readonly Lock _stateGate = new();
    private LiveKitProcess? _process;
    private string _step = "not started";
    private bool _installed;

    /// <summary>The release this service installs.</summary>
    public LiveKitReleasePin Pin { get; } = LiveKitReleasePin.Current;

    /// <summary>The supervised child, once there is one.</summary>
    public LiveKitProcess? Process
    {
        get
        {
            lock (_stateGate)
            {
                return _process;
            }
        }
    }

    /// <summary>Everything the console renders.</summary>
    public LiveKitRuntimeState State
    {
        get
        {
            var problems = new List<string>(options.Problems());

            string step;
            bool installed;
            LiveKitProcess? process;

            lock (_stateGate)
            {
                step = _step;
                installed = _installed;
                process = _process;
            }

            if (options.Mode is LiveKitMode.Bundled && !SupportedHere())
            {
                problems.Add(
                    "The bundled call server is a Linux binary and this Fleet Manager is not "
                    + "running on Linux, so nothing is supervised here. Point "
                    + $"{LiveKitOptions.ExternalUrlVariable} at an existing LiveKit instead.");
            }

            var ready = options.Mode switch
            {
                LiveKitMode.Disabled => false,
                LiveKitMode.External => problems.Count == 0,
                _ => problems.Count == 0 && installed && process is { IsRunning: true },
            };

            return new LiveKitRuntimeState(
                options.Mode,
                Pin.Version,
                ready,
                step,
                problems,
                process?.State);
        }
    }

    /// <summary>
    /// Rotates the signing secret, re-mints every frame's token and restarts the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is not arbitrary and is the whole of what makes rotation safe. The secret is
    /// replaced first, then every adopted frame is issued a token signed with it, and only then
    /// is LiveKit restarted onto the new configuration. Restarting first would leave a window in
    /// which the running server rejects every token in the fleet; re-minting first would sign
    /// against a secret the database no longer holds.
    /// </para>
    /// <para>
    /// A frame that is offline for the rotation is not stranded: its new token is already in the
    /// database and arrives in the settings frame it receives on its next connect. Its old token
    /// stops working the moment LiveKit reloads, which is what revocation means.
    /// </para>
    /// </remarks>
    /// <returns>How many frames were issued a new token, or null if this deployment cannot rotate.</returns>
    public async Task<int?> RotateAsync(CancellationToken cancellationToken)
    {
        var credential = await deployment.RotateSecretAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return null;
        }

        var issued = await provisioning.ReviewFleetAsync(force: true, cancellationToken).ConfigureAwait(false);

        LiveKitConfigFile.Write(options, credential);

        if (Process is { } process)
        {
            await process.RestartAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LiveKitSecretRotated(issued);
        return issued;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Mode is not LiveKitMode.Bundled)
        {
            Step(options.Mode is LiveKitMode.External
                ? "using the LiveKit server named in the environment"
                : "switched off");
            return;
        }

        if (!SupportedHere())
        {
            Step("no bundled build for this operating system or architecture");
            return;
        }

        try
        {
            var process = await PrepareAsync(stoppingToken).ConfigureAwait(false);
            if (process is null)
            {
                return;
            }

            await process.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down. RunAsync stops the child on its way out.
        }
        catch (Exception exception)
        {
            // Calling failing must never take the Fleet Manager down with it. Photos, adoption,
            // reconciliation and updates are all independent of this, and a server that refused
            // to serve them because a download failed would be trading nine working things for
            // one broken one.
            Step("the call server could not be started");
            logger.LiveKitSuperviseFailed(exception);
        }
    }

    /// <summary>
    /// Fetches, verifies, configures and starts. Public so a test can drive it without a host.
    /// </summary>
    /// <returns>The supervisor, or null when the release could not be put in place.</returns>
    public async Task<LiveKitProcess?> PrepareAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Directory);

        var credential = await deployment.CredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            Step("no key and secret to configure the call server with");
            return null;
        }

        Step("fetching the pinned LiveKit release");

        var installer = new LiveKitInstaller(
            options.BinaryPath,
            RuntimeInformation.OSArchitecture,
            download,
            logger,
            Pin);

        var result = await installer.InstallAsync(cancellationToken).ConfigureAwait(false);
        var installed = result is LiveKitInstallResult.Installed or LiveKitInstallResult.AlreadyInstalled;

        lock (_stateGate)
        {
            _installed = installed;
        }

        if (!installed)
        {
            Step(LiveKitInstaller.Describe(result, Pin.Version));
            return null;
        }

        LiveKitConfigFile.Write(options, credential);

        var process = new LiveKitProcess(new LiveKitProcessServices
        {
            BinaryPath = options.BinaryPath,
            ConfigPath = options.ConfigPath,
            WorkingDirectory = options.Directory,
            SignalPort = options.SignalPort,
            Version = Pin.Version,
            Clock = clock,
            Log = logger,
        });

        lock (_stateGate)
        {
            _process = process;
        }

        Step("supervising the call server");
        return process;
    }

    /// <summary>Whether the pin has a build for the machine this Fleet Manager is running on.</summary>
    private bool SupportedHere() =>
        OperatingSystem.IsLinux() && Pin.AssetFor(RuntimeInformation.OSArchitecture) is not null;

    private void Step(string step)
    {
        lock (_stateGate)
        {
            _step = step;
        }
    }
}
