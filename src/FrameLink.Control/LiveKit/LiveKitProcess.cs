using System.Diagnostics;

namespace FrameLink.Control.LiveKit;

/// <summary>What the console is told about the supervised child.</summary>
/// <param name="Running">Whether a child is alive right now.</param>
/// <param name="Pid">Its process id, or null.</param>
/// <param name="Launches">How many times a child has been started since this server started.</param>
/// <param name="LastExitCode">The exit status of the last child that died, or null.</param>
/// <param name="LastStartedUtc">When the current or last child was started.</param>
/// <param name="LastExitedUtc">When the last child died.</param>
/// <param name="LastFailure">Why the last start did not take, or null.</param>
public sealed record LiveKitProcessState(
    bool Running,
    int? Pid,
    int Launches,
    int? LastExitCode,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastExitedUtc,
    string? LastFailure);

/// <summary>
/// <b><c>livekit-server</c> as a supervised child process of the Fleet Manager</b> (§3.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the Immich Kiosk shape, and it is the right precedent because the specification
/// says so.</b> <c>KioskProcess</c> argues at length that the agent supervising Kiosk is
/// <i>not</i> §2.10's shape but §3.7's — "the Fleet Manager supervising <c>livekit-server</c> as
/// a child process with the same restart-and-report discipline" — and it drew that comparison
/// before this class existed. Reading it back the other way costs nothing and gains consistency:
/// a parent owns the child's lifetime, "the child exited" is an event it is told about rather
/// than a symptom it has to infer, and the response is to start it again. There is no memory
/// ceiling, no clock, no silence timeout and no health probe anywhere in this file, because
/// §2.10's four behaviours all exist to manufacture a trigger for a restart <i>systemd</i> would
/// never take on its own — and nothing here is a systemd unit.
/// </para>
/// <para>
/// <b>The one structural difference is the interlock, and it is absent because there is nothing
/// to interlock with.</b> <c>KioskProcess</c> opens a supervision window over
/// <c>kiosk.process.supervised</c> and <c>kiosk.listen-address</c> on every relaunch, because a
/// reconcile pass landing in the second between an exit and the restart would read a blinking
/// process as drift and, under §2.6, stop the product and reboot the frame. The Fleet Manager
/// runs no reconciliation loop over itself: nothing on this side compares a declared state
/// against an observed one, so a blink here has no second observer to mislead. Adding a window
/// would be ceremony over an empty set.
/// </para>
/// <para>
/// <b>What replaces <c>supervision.recoveryDeadline</c> is the console, not a state ladder.</b>
/// On a frame, a supervision window that expires becomes ordinary drift and §2.6 takes over —
/// the product stops and the screen narrates, because the frame is the thing that is wrong.
/// Here the thing that is wrong is the <i>server</i>, and the frames are blameless: their
/// configuration is exactly what was declared, their slideshows keep running, and what they
/// cannot do is place a call. So the escalation is reporting rather than repair — every exit is
/// logged at warning with its exit status, and <see cref="State"/> is what
/// <c>/api/livekit</c> renders, which is the surface an operator actually looks at.
/// </para>
/// <para>
/// <b>Nothing backs off, for <c>KioskProcess</c>'s reason.</b> §2.5's backoff exists to stop a
/// reboot loop from wearing hardware; a child process that will not stay up wears nothing, and
/// every second it is down is a second the household cannot call. The relaunch interval is the
/// only spacing there is.
/// </para>
/// </remarks>
public sealed class LiveKitProcess : IAsyncDisposable
{
    /// <summary>How long a stop waits for the child to go before it is abandoned.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    private readonly LiveKitProcessServices _services;
    private readonly Lock _gate = new();

    private Process? _child;
    private int _launches;
    private int? _lastExitCode;
    private DateTimeOffset? _lastStartedUtc;
    private DateTimeOffset? _lastExitedUtc;

    /// <summary>Creates the supervisor of one <c>livekit-server</c> child.</summary>
    public LiveKitProcess(LiveKitProcessServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>Why the last start did not take, or null.</summary>
    public string? LastFailure { get; private set; }

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

    /// <summary>Everything the console renders about the child.</summary>
    public LiveKitProcessState State
    {
        get
        {
            lock (_gate)
            {
                return new LiveKitProcessState(
                    _child is { HasExited: false },
                    _child is { HasExited: false } child ? child.Id : null,
                    _launches,
                    _lastExitCode,
                    _lastStartedUtc,
                    _lastExitedUtc,
                    LastFailure);
            }
        }
    }

    /// <summary>
    /// Starts the child if it is not running, and reports whether one is running afterwards.
    /// </summary>
    /// <remarks>
    /// Idempotent by construction, because everything that calls it — the hosted service at
    /// start-up, a rotation, the relaunch loop — may be racing one of the others.
    /// </remarks>
    public bool Start()
    {
        lock (_gate)
        {
            if (_child is { HasExited: false })
            {
                return true;
            }

            if (!File.Exists(_services.BinaryPath))
            {
                LastFailure = $"there is no LiveKit server binary at {_services.BinaryPath}";
                return false;
            }

            if (!File.Exists(_services.ConfigPath))
            {
                LastFailure = $"there is no LiveKit configuration at {_services.ConfigPath}";
                return false;
            }

            // --config, never --keys or --config-body. Both of those put the fleet's signing
            // secret in the child's argument vector or environment block, and on Linux
            // /proc/<pid>/cmdline and /proc/<pid>/environ are readable by anything running as the
            // same user. A 0600 file is the only one of the three that keeps the secret to the
            // two processes that need it.
            var start = new ProcessStartInfo(_services.BinaryPath)
            {
                WorkingDirectory = _services.WorkingDirectory,
                UseShellExecute = false,
            };

            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(_services.ConfigPath);

            // Nothing is redirected, for HostProcessRunner's reason and KioskProcess's: redirecting
            // without draining is a pipe-buffer deadlock, and draining would buy nothing, because
            // inheriting is better — LiveKit's log lines land in the Fleet Manager's own stdout,
            // which is what replaces `docker logs livekit` from guide 7 step 4.
            try
            {
                var child = Process.Start(start);
                if (child is null)
                {
                    LastFailure = $"{_services.BinaryPath} did not start";
                    return false;
                }

                _child = child;
                _launches++;
                _lastStartedUtc = _services.Clock.GetUtcNow();
                LastFailure = null;

                _services.Log.LiveKitStarted(child.Id, _services.SignalPort, _services.Version);
                return true;
            }
            catch (Exception exception)
                when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                LastFailure = exception.Message;
                _services.Log.LiveKitStartRefused(exception.Message);
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
                // Kill(entireProcessTree: false): a static Go binary with no helpers, so there is
                // no tree. On Linux this is SIGKILL and the base class library offers no way to
                // send a child SIGTERM, so a graceful stop would mean shelling out to `kill` — not
                // worth it here, because LiveKit holds nothing that survives it. Rooms are
                // in-memory and re-created on demand; the participants are frames whose clients
                // already retry forever (see app/livekit.js).
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

    /// <summary>Stops and starts. What a rotation and a configuration change both do.</summary>
    public async Task<bool> RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        return Start();
    }

    /// <summary>Keeps a child running for as long as the Fleet Manager does.</summary>
    /// <remarks>
    /// The whole of Docker's <c>restart: unless-stopped</c> from guide 7, and no more than that:
    /// this loop answers "the child exited", which is the only fault a parent can see for free.
    /// "The child is up and not answering" is a different fault with a different reading, and it
    /// is deliberately not invented here — inventing it would be the health trigger §2.10 owns and
    /// §3.7 does not ask for.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SuperviseOnce();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                _services.Log.LiveKitSuperviseFailed(exception);
            }

            try
            {
                await Task.Delay(_services.Interval, _services.Clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>One pass of the relaunch loop, exposed so a test does not have to race a clock.</summary>
    /// <remarks>
    /// Synchronous, and that is the shape rather than an oversight: every step is a handle check,
    /// a <c>fork</c>/<c>exec</c> or a field, and none of them waits on anything. The loop around it
    /// is asynchronous because the <i>interval</i> is.
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

            lock (_gate)
            {
                _lastExitCode = code;
                _lastExitedUtc = _services.Clock.GetUtcNow();
            }

            _services.Log.LiveKitExited(code);
        }

        if (IsRunning)
        {
            return;
        }

        Start();
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
}

/// <summary>Everything the child needs to be run and watched.</summary>
public sealed record LiveKitProcessServices
{
    /// <summary>The executable to run.</summary>
    public required string BinaryPath { get; init; }

    /// <summary>The generated configuration it is pointed at.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>Where the child runs. Its own directory, which is where the binary lives.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The signalling port, for the start-up log line.</summary>
    public required int SignalPort { get; init; }

    /// <summary>The pinned version, for the same line.</summary>
    public required string Version { get; init; }

    /// <summary>Source of time and of waiting.</summary>
    public required TimeProvider Clock { get; init; }

    /// <summary>Where launches, exits and refusals are recorded.</summary>
    public required ILogger Log { get; init; }

    /// <summary>How often the relaunch loop looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);
}
