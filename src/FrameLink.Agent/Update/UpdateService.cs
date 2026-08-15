using FrameLink.Agent.Hosting;
using FrameLink.Agent.State;

namespace FrameLink.Agent.Update;

/// <summary>What one convergence check concluded.</summary>
public enum UpdateOutcome
{
    /// <summary>The running version already matches the served one.</summary>
    AlreadyMatching,

    /// <summary>A different version was fetched, verified and put in place.</summary>
    Applied,

    /// <summary>The Fleet Manager could not be reached, or answered nothing usable.</summary>
    Unreachable,

    /// <summary>A different version was served but the download failed verification.</summary>
    VerificationFailed,

    /// <summary>No endpoint is configured yet.</summary>
    NoEndpoint,
}

/// <summary>Asks the process to stand aside for a new binary.</summary>
public interface IRestartSignal
{
    /// <summary>Requests a restart, naming the reason for the journal.</summary>
    void Request(string reason);
}

/// <summary>
/// §2.8's convergence loop: <b>hourly, out of band, and matching — upgrade or downgrade, always</b>.
/// </summary>
/// <remarks>
/// <para>
/// The hourly tick is the mechanism; the handshake is only an optimisation. That ordering is
/// load-bearing rather than stylistic. Because this loop runs on its own timer against a plain,
/// versionless HTTPS route (§4.2) with no dependency on the socket, every failure mode resolves
/// itself: a protocol mismatch that makes the socket useless, a server that was restarted, a
/// frame that was offline for a week, an agent whose update failed last time. All of them are
/// repaired by the next tick, with nobody in the loop.
/// </para>
/// <para>
/// <b>It matches rather than compares.</b> There is no "is the served version newer" anywhere in
/// this file, and there must never be one: reverting the container tag has to revert the fleet
/// within the hour, so a downgrade is an ordinary convergence, not an error to be refused.
/// </para>
/// </remarks>
public sealed class UpdateService
{
    /// <summary>§2.8's tick.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

    private readonly IReleaseSource _releases;
    private readonly IBinarySwap _swap;
    private readonly IAgentClock _clock;
    private readonly AgentStatusHub _hub;
    private readonly IRestartSignal _restart;
    private readonly IAgentLog _log;
    private readonly Func<Uri?> _endpoint;
    private readonly string _currentVersion;
    private readonly string _runtimeIdentifier;

    private TaskCompletionSource _trigger = NewTrigger();

    /// <summary>Creates the service.</summary>
    public UpdateService(
        IReleaseSource releases,
        IBinarySwap swap,
        IAgentClock clock,
        AgentStatusHub hub,
        IRestartSignal restart,
        IAgentLog log,
        Func<Uri?> endpoint,
        string currentVersion,
        string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(swap);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(restart);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        _releases = releases;
        _swap = swap;
        _clock = clock;
        _hub = hub;
        _restart = restart;
        _log = log;
        _endpoint = endpoint;
        _currentVersion = currentVersion;
        _runtimeIdentifier = runtimeIdentifier;
    }

    /// <summary>How long between out-of-band checks.</summary>
    public TimeSpan Interval { get; init; } = DefaultInterval;

    /// <summary>Whether updates are enabled for this device (§2.8, operator-disableable).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How many checks have run.</summary>
    public int CompletedChecks { get; private set; }

    /// <summary>The verdict of the most recent check.</summary>
    public UpdateOutcome LastOutcome { get; private set; } = UpdateOutcome.NoEndpoint;

    /// <summary>
    /// Brings the next check forward. Purely an optimisation (§2.8).
    /// </summary>
    /// <remarks>
    /// Deliberately returns nothing and reports nothing. If this call is lost, dropped or never
    /// made, the hourly tick still converges the frame — which is the property that lets the
    /// handshake be an optimisation rather than a dependency.
    /// </remarks>
    public void TriggerNow() => _trigger.TrySetResult();

    /// <summary>Runs the out-of-band loop until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckOnceAsync(cancellationToken).ConfigureAwait(false);

            if (!await WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Runs one convergence check.</summary>
    public async Task<UpdateOutcome> CheckOnceAsync(CancellationToken cancellationToken)
    {
        CompletedChecks++;

        if (!Enabled)
        {
            return Record(UpdateOutcome.AlreadyMatching);
        }

        var endpoint = _endpoint();
        if (endpoint is null)
        {
            return Record(UpdateOutcome.NoEndpoint);
        }

        var release = await _releases
            .GetReleaseAsync(endpoint, _runtimeIdentifier, cancellationToken)
            .ConfigureAwait(false);

        if (release is null)
        {
            return Record(UpdateOutcome.Unreachable);
        }

        _hub.Publish(status => status with { ServedAgentVersion = release.Version });

        if (string.Equals(release.Version, _currentVersion, StringComparison.Ordinal))
        {
            return Record(UpdateOutcome.AlreadyMatching);
        }

        _log.Info($"Converging from {_currentVersion} to the served version {release.Version}.");
        _hub.Publish(status => status with
        {
            UpdateProgress = 0,
            Narration = new Narration
            {
                Detected = $"This frame runs version {_currentVersion}; the Fleet Manager serves {release.Version}.",
                WhyItMatters = "The frame and the server have to run the same version to understand each other.",
                Action = $"Downloading and verifying {release.Version}",
                ActionGloss = "Fetching the new software and checking it arrived intact.",
            },
        });

        var payload = await _releases.DownloadAsync(endpoint, release, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return Record(UpdateOutcome.Unreachable);
        }

        SwapResult swap;
        await using (payload.ConfigureAwait(false))
        {
            swap = await _swap.ApplyAsync(payload, release, cancellationToken).ConfigureAwait(false);
        }

        if (swap != SwapResult.Applied)
        {
            _hub.Publish(status => status with { UpdateProgress = null });
            return Record(UpdateOutcome.VerificationFailed);
        }

        _hub.Publish(status => status with { UpdateProgress = 1, RestartPending = true });
        _restart.Request($"version {release.Version} is in place");
        return Record(UpdateOutcome.Applied);
    }

    private UpdateOutcome Record(UpdateOutcome outcome)
    {
        LastOutcome = outcome;
        return outcome;
    }

    private async Task<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        var trigger = Volatile.Read(ref _trigger);

        using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sleeping = _clock.DelayAsync(Interval, tick.Token);

        try
        {
            await Task.WhenAny(sleeping, trigger.Task).ConfigureAwait(false);
        }
        finally
        {
            // Cancel the loser so its registration on the caller's token is released. A
            // Task.WhenAny that abandons the losing task is how a loop that runs for months
            // accumulates timers — the same leak class §4.1 warns about, on a different path.
            await tick.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await sleeping.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either the trigger won the race, or the agent is shutting down.
        }

        if (trigger.Task.IsCompleted)
        {
            Interlocked.CompareExchange(ref _trigger, NewTrigger(), trigger);
        }

        return !cancellationToken.IsCancellationRequested;
    }

    private static TaskCompletionSource NewTrigger() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Turns a restart request into a cancellation of the agent's own run.</summary>
public sealed class ProcessRestartSignal : IRestartSignal
{
    private readonly CancellationTokenSource _shutdown;
    private readonly IAgentLog _log;

    /// <summary>Creates a signal that cancels <paramref name="shutdown"/>.</summary>
    public ProcessRestartSignal(CancellationTokenSource shutdown, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        ArgumentNullException.ThrowIfNull(log);

        _shutdown = shutdown;
        _log = log;
    }

    /// <summary>Whether a restart has been asked for.</summary>
    public bool Requested { get; private set; }

    /// <inheritdoc/>
    public void Request(string reason)
    {
        Requested = true;
        _log.Info($"Restarting: {reason}.");

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already on the way down.
        }
    }
}
