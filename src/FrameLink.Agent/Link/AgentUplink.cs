namespace FrameLink.Agent.Link;

/// <summary>
/// The one place anything other than the connection loop may write to the Fleet Manager.
/// </summary>
/// <remarks>
/// <para>
/// The reconciler has to be able to send telemetry, and the socket belongs to
/// <see cref="ConnectionAttempt"/>, which is created and destroyed inside a single iteration of
/// the reconnect loop. Handing the transport to the reconciler directly would give a long-lived
/// object a reference to a short-lived one — precisely the shape of the v1 LiveKit leak that
/// §4.1's "cleanup per failed attempt" rule exists to prevent.
/// </para>
/// <para>
/// So the direction is inverted. This object is long-lived and empty; an attempt
/// <see cref="Attach"/>es its transport for the duration of a session and releases it through
/// the same ownership stack as everything else it allocates. Nothing here outlives an attempt,
/// and <see cref="IsConnected"/> answering false is an ordinary state rather than an error —
/// §4.1 requires the agent to reconcile, verify, retry and escalate with no server present.
/// </para>
/// </remarks>
public sealed class AgentUplink : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Lock _gate = new();

    private IControlTransport? _transport;

    /// <summary>Whether a session is live right now.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _transport is not null;
            }
        }
    }

    /// <summary>How many frames have gone up successfully.</summary>
    public int Sent { get; private set; }

    /// <summary>
    /// Publishes <paramref name="transport"/> as the current session until the handle is disposed.
    /// </summary>
    /// <remarks>
    /// Detaching compares identity before clearing, so a late-disposing attempt cannot unhook a
    /// newer session that has already taken its place.
    /// </remarks>
    public IDisposable Attach(IControlTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        lock (_gate)
        {
            _transport = transport;
        }

        return new Attachment(this, transport);
    }

    /// <summary>Sends one encoded envelope, or reports that there was nowhere to send it.</summary>
    /// <returns>True only if the bytes reached the transport.</returns>
    public async ValueTask<bool> SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
    {
        // Taken before the transport is read, so two callers cannot interleave writes on a
        // WebSocket, which permits exactly one send at a time.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IControlTransport? transport;
            lock (_gate)
            {
                transport = _transport;
            }

            if (transport is null)
            {
                return false;
            }

            await transport.SendAsync(utf8, cancellationToken).ConfigureAwait(false);
            Sent++;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException)
        {
            // A send that fails means the session is going away; the reconnect loop will notice
            // on its own read. The caller's answer is simply "not delivered", which sends the
            // payload to the offline buffer — the same place it would have gone had the frame
            // never been connected.
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Releases the send lock. The uplink lives as long as the agent does.</summary>
    public void Dispose() => _sendLock.Dispose();

    private void Detach(IControlTransport transport)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_transport, transport))
            {
                _transport = null;
            }
        }
    }

    private sealed class Attachment : IDisposable
    {
        private readonly AgentUplink _uplink;
        private IControlTransport? _transport;

        public Attachment(AgentUplink uplink, IControlTransport transport)
        {
            _uplink = uplink;
            _transport = transport;
        }

        public void Dispose()
        {
            var transport = Interlocked.Exchange(ref _transport, null);
            if (transport is not null)
            {
                _uplink.Detach(transport);
            }
        }
    }
}
