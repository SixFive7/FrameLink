using FrameLink.Agent.Hosting;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Agent.Link;

/// <summary>
/// What this frame says about itself, composed from what the loop actually is and kept current.
/// </summary>
/// <remarks>
/// <para>
/// <b>One computation, two carriers.</b> <see cref="Hello"/> is read by
/// <see cref="ControlLink.AgentStatusText"/> for every handshake (§4.2 puts one on every connect),
/// and <see cref="RunAsync"/> pushes the same string again over a live session whenever it
/// changes. Both read <see cref="Current"/>, so there is no second place that decides what a frame
/// is doing — the authority is the <c>loopState</c> the reconciler already publishes with every
/// census, and this only turns it into the §2.3 vocabulary the Fleet Manager classifies
/// (<see cref="AgentHealth.ReportFor"/>).
/// </para>
/// <para>
/// <b>Both carriers are needed, and neither is sufficient.</b> Without the hello a fresh connect
/// would carry nothing until the loop next moved; without the push a converged frame would report
/// whatever it happened to be doing in the seconds after its last reboot, for ever, because a
/// healthy frame never reconnects. That second half is the defect this type was written for: a
/// frame verified at 81 of 81 was still telling its operator it was part-way through applying
/// something an hour later.
/// </para>
/// <para>
/// <b>Nothing is buffered and nothing is retried.</b> A send that finds no session, or fails, is
/// simply not made — <c>TelemetryOutbox</c>'s disk buffer exists because an event is
/// history and losing one loses a fact, and this is the opposite: the current picture, superseded
/// by the next change and re-sent in full by the next hello. Buffering it could only deliver a
/// stale sentence behind a fresh one.
/// </para>
/// </remarks>
public sealed class AgentStatusReporter : IDisposable
{
    private readonly AgentStatusHub _hub;
    private readonly AgentUplink _uplink;
    private readonly IAgentLog _log;
    private readonly string _deviceId;
    private readonly string? _detail;
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly IDisposable _subscription;
    private readonly Lock _gate = new();

    private string? _reported;

    /// <summary>Creates a reporter over <paramref name="hub"/>, sending through <paramref name="uplink"/>.</summary>
    /// <param name="hub">Where the loop publishes its own state.</param>
    /// <param name="uplink">The live session, when there is one.</param>
    /// <param name="log">Where a change of self-report is narrated.</param>
    /// <param name="deviceId">This frame's proven identity, carried in the payload.</param>
    /// <param name="detail">
    /// The parenthesis of §2.5's <c>Head(detail)</c> shape — what this build is and how it found
    /// its Fleet Manager. Fixed for the life of the process on purpose: it is what makes a
    /// <i>broken</i> agent legible, which is the frozen field's documented job, and everything
    /// that moves is the head in front of it.
    /// </param>
    public AgentStatusReporter(
        AgentStatusHub hub,
        AgentUplink uplink,
        IAgentLog log,
        string deviceId,
        string? detail)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(uplink);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(deviceId);

        _hub = hub;
        _uplink = uplink;
        _log = log;
        _deviceId = deviceId;
        _detail = detail;

        // The hub is long-lived and so is this, so the subscription is not the shape §4.1's
        // cleanup rule is about — but it is released in Dispose all the same, because
        // AgentStatusHub.SubscriberCount is what proves the agent accumulates no listeners.
        _subscription = hub.Subscribe(_ => Signal());
    }

    /// <summary>How many changes have actually reached a Fleet Manager.</summary>
    public int Sent { get; private set; }

    /// <summary>What this frame would say about itself right now.</summary>
    public string? Current => AgentHealth.ReportFor(_hub.Current.Reconcile.LoopState, _detail);

    /// <summary>
    /// The self-report for a handshake, recorded as the thing the Fleet Manager has been told.
    /// </summary>
    /// <remarks>
    /// Recording it here rather than on delivery is deliberate. A hello that never lands takes its
    /// whole attempt with it, and the next attempt sends another one — so an optimistic record
    /// costs nothing, while a pessimistic one would re-push over a session that already carried
    /// the value in its opening frame.
    /// </remarks>
    public string? Hello()
    {
        var report = Current;

        lock (_gate)
        {
            _reported = report;
        }

        return report;
    }

    /// <summary>Pushes the self-report whenever it changes, until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                await PublishAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The agent is shutting down; the ordinary way out.
        }
    }

    /// <summary>Releases the hub subscription.</summary>
    public void Dispose()
    {
        _subscription.Dispose();
        _changed.Dispose();
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        var report = Current;
        if (string.IsNullOrWhiteSpace(report))
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(report, _reported, StringComparison.Ordinal))
            {
                return;
            }
        }

        // A pass on a converged frame publishes to the hub every few minutes and says the same
        // thing every time, so this is the ordinary answer rather than the exceptional one.
        if (!_uplink.IsConnected)
        {
            return;
        }

        var sent = await _uplink.SendAsync(
            WireMessage.Encode(
                ControlWire.KindAgentStatus,
                new AgentStatusUpdate { DeviceId = _deviceId, Status = report },
                ProtocolJson.Default.AgentStatusUpdate,
                ProtocolConstants.ChannelTelemetry),
            cancellationToken).ConfigureAwait(false);

        if (!sent)
        {
            // The session is going away. The reconnect loop notices on its own read and the hello
            // it sends next carries whatever the loop is by then, which is at least this.
            return;
        }

        lock (_gate)
        {
            _reported = report;
        }

        Sent++;
        _log.Info($"This frame now reports itself as {report}.");
    }

    /// <summary>Wakes <see cref="RunAsync"/>, coalescing bursts into one pass.</summary>
    private void Signal()
    {
        try
        {
            _changed.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending and one is all that is ever needed: the loop reads the
            // hub afresh, so it cannot deliver a value the burst has already superseded.
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a publish was in flight on another thread.
        }
    }
}
