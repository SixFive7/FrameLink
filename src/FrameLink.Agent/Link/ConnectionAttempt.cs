using FrameLink.Agent.Identity;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Agent.Link;

/// <summary>How one connection attempt ended.</summary>
public enum AttemptResult
{
    /// <summary>The socket never opened.</summary>
    Unreachable,

    /// <summary>The socket opened but the handshake produced no verdict.</summary>
    HandshakeFailed,

    /// <summary>The handshake produced a verdict and the session then ran and ended.</summary>
    Session,

    /// <summary>The agent is shutting down.</summary>
    Cancelled,
}

/// <summary>The outcome of one attempt.</summary>
public sealed record AttemptOutcome
{
    /// <summary>How it ended.</summary>
    public required AttemptResult Result { get; init; }

    /// <summary>Plain-language reason, for the screen and the journal.</summary>
    public string? Reason { get; init; }

    /// <summary>The server's verdict, when there was one.</summary>
    public HandshakeResult? Handshake { get; init; }
}

/// <summary>Everything one attempt needs.</summary>
public sealed record AttemptContext
{
    /// <summary>Where to connect.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>How to open the transport.</summary>
    public required IControlTransportFactory Transports { get; init; }

    /// <summary>The device identity that signs the proof.</summary>
    public required DeviceKey Identity { get; init; }

    /// <summary>The shared status holder.</summary>
    public required AgentStatusHub Hub { get; init; }

    /// <summary>Board serial, sent in the hello for bench matching (§3.3).</summary>
    public string? HardwareSerial { get; init; }

    /// <summary>Free-text self-report, sent in the hello (§4.2).</summary>
    public string? AgentStatusText { get; init; }

    /// <summary>How long connect plus handshake may take.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Invoked with the server's verdict, before the session pump starts.</summary>
    /// <remarks>
    /// Optional by design (§2.8): this is what brings an update forward instead of waiting for
    /// the hourly tick, and nothing about convergence depends on it running or succeeding.
    /// </remarks>
    public Func<HandshakeResult, CancellationToken, Task>? OnVerdict { get; init; }

    /// <summary>Where this attempt publishes its transport for the duration of the session.</summary>
    public AgentUplink? Uplink { get; init; }

    /// <summary>Invoked when the Fleet Manager pushes effective settings (§3.4).</summary>
    public Action<SettingsPush>? OnSettings { get; init; }

    /// <summary>Invoked when the operator presses retry on this frame (§2.5 rung 3).</summary>
    public Action<RetryRequest>? OnRetry { get; init; }

    /// <summary>
    /// Invoked when the operator asks this frame to switch off (§2.5 rung 5, decision 92).
    /// </summary>
    /// <remarks>
    /// Optional like every other hook here, and the one whose absence is safe: a build that does not
    /// wire it leaves the kind unhandled, and an unhandled kind is skipped. That is the whole reason
    /// this is a kind rather than a member on the retry -- doing nothing is the only safe way to
    /// misunderstand a shutdown.
    /// </remarks>
    public Action<ShutdownRequest>? OnShutdown { get; init; }

    /// <summary>Invoked when the Fleet Manager pushes who to contact (§2.7 item 8).</summary>
    public Action<OperatorContact>? OnOperatorContact { get; init; }
}

/// <summary>
/// One connect-and-handshake-and-session cycle, and the sole owner of everything it allocates.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists because of the v1 LiveKit post-mortem.</b> That retry loop leaked engine
/// and listener state on every failed connect: a measured ~15 MB per minute, which killed a 2 GB
/// frame in under two hours. §4.1 turns the lesson into a rule — "cleanup per failed attempt" —
/// and the rule is implemented here as a structural property rather than as care taken at each
/// call site:
/// </para>
/// <list type="number">
/// <item><description>
/// Every disposable is handed to <c>Own</c> on the statement that creates it, so there is no
/// line of code between allocating a resource and becoming responsible for releasing it.
/// </description></item>
/// <item><description>
/// Release happens in <see cref="DisposeAsync"/>, in reverse order, unconditionally, for every
/// exit path including cancellation and an exception thrown mid-handshake.
/// </description></item>
/// <item><description>
/// Nothing survives the attempt. The transport is never pooled, the subscription is never
/// long-lived, and the attempt object itself is unreachable once <see cref="RunAsync"/> returns —
/// which is what lets a test assert, with weak references, that a hundred failures leave nothing
/// behind at all.
/// </description></item>
/// </list>
/// <para>
/// The one obligation this class cannot discharge is a factory that leaks while throwing; that is
/// stated on <see cref="IControlTransportFactory"/> and tested separately.
/// </para>
/// </remarks>
public sealed class ConnectionAttempt : IAsyncDisposable
{
    private readonly Stack<Func<ValueTask>> _releases = new();
    private bool _disposed;

    private ConnectionAttempt()
    {
    }

    /// <summary>Runs one attempt and releases everything it created before returning.</summary>
    public static async Task<AttemptOutcome> RunAsync(AttemptContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var attempt = new ConnectionAttempt();
        await using (attempt.ConfigureAwait(false))
        {
            return await attempt.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Releases everything this attempt owns, in reverse order, exactly once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        while (_releases.Count > 0)
        {
            var release = _releases.Pop();
            try
            {
                await release().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or IOException or InvalidOperationException)
            {
                // One resource failing to close must not strand the ones below it in the stack.
                // That is the exact shape of the v1 leak: an exception on the way out skipping
                // the rest of the cleanup.
            }
        }
    }

    private T Own<T>(T resource)
        where T : IDisposable
    {
        _releases.Push(() =>
        {
            resource.Dispose();
            return ValueTask.CompletedTask;
        });

        return resource;
    }

    private T OwnAsync<T>(T resource)
        where T : IAsyncDisposable
    {
        _releases.Push(resource.DisposeAsync);
        return resource;
    }

    private async Task<AttemptOutcome> ExecuteAsync(AttemptContext context, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new AttemptOutcome { Result = AttemptResult.Cancelled };
        }

        var handshakeDeadline = Own(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        handshakeDeadline.CancelAfter(context.HandshakeTimeout);

        IControlTransport transport;
        try
        {
            transport = await context.Transports
                .ConnectAsync(context.Endpoint, handshakeDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AttemptOutcome { Result = AttemptResult.Cancelled };
        }
        catch (OperationCanceledException)
        {
            return new AttemptOutcome
            {
                Result = AttemptResult.Unreachable,
                Reason = $"No answer from {context.Endpoint} within {(int)context.HandshakeTimeout.TotalSeconds} seconds.",
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new AttemptOutcome
            {
                Result = AttemptResult.Unreachable,
                Reason = $"Could not reach {context.Endpoint}: {exception.Message}",
            };
        }

        OwnAsync(transport);

        // The session runs on its own token so that a healthy connection is not killed by the
        // handshake deadline, and so that a pending restart can end it promptly.
        var session = Own(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));

        // The per-attempt subscription: the exact shape that leaked in v1. It is registered on a
        // long-lived object and released below, and AgentStatusHub.SubscriberCount is what proves
        // the release actually happens.
        Own(context.Hub.Subscribe(status =>
        {
            if (status.RestartPending)
            {
                CancelQuietly(session);
            }
        }));

        if (context.Hub.Current.RestartPending)
        {
            CancelQuietly(session);
        }

        HandshakeOutcome handshake;
        try
        {
            handshake = await HandshakeExchange.PerformAsync(
                transport,
                context.Identity,
                AgentBuild.Version,
                context.HardwareSerial,
                context.AgentStatusText,
                handshakeDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AttemptOutcome { Result = AttemptResult.Cancelled };
        }
        catch (OperationCanceledException)
        {
            return new AttemptOutcome
            {
                Result = AttemptResult.HandshakeFailed,
                Reason = "The Fleet Manager did not finish the handshake in time.",
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new AttemptOutcome
            {
                Result = AttemptResult.HandshakeFailed,
                Reason = $"The handshake failed: {exception.Message}",
            };
        }

        if (!handshake.Completed)
        {
            return new AttemptOutcome
            {
                Result = AttemptResult.HandshakeFailed,
                Reason = handshake.Failure,
            };
        }

        var verdict = handshake.Result!;
        Publish(context, verdict, connected: true);

        // Owned like everything else, so the reconciler's route to the server closes when this
        // attempt does. A long-lived object holding a short-lived transport is the exact shape
        // of the v1 leak, and Own() is what keeps it from being one here.
        if (context.Uplink is { } uplink)
        {
            Own(uplink.Attach(transport));
        }

        // Before the pump, not after it. §2.8 makes the handshake the thing that triggers an
        // update immediately instead of waiting for the hourly tick, and a session on a healthy
        // adopted frame never ends — so a callback that waited for the pump to return would fire
        // only when the connection dropped, which is the one moment the optimisation is worthless.
        if (context.OnVerdict is not null)
        {
            await context.OnVerdict(verdict, session.Token).ConfigureAwait(false);
        }

        var pumped = await PumpAsync(context, transport, verdict, session.Token).ConfigureAwait(false);

        return new AttemptOutcome
        {
            Result = cancellationToken.IsCancellationRequested ? AttemptResult.Cancelled : AttemptResult.Session,
            Handshake = pumped.Verdict,
            Reason = pumped.Reason,
        };
    }

    /// <summary>
    /// Reads the connection until it closes, answering the server's liveness probes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Answering <c>ping</c> is the reason this loop exists at all.</b> §3.5 makes connection
    /// presence <i>be</i> online status, and that is only trustworthy because the server proves
    /// the socket with an application-level ping and a missed-pong deadline. An agent that reads
    /// pings and says nothing is, from the server's side, a frame whose plug has been pulled: the
    /// connection is torn down on the deadline and rebuilt on the next backoff, forever, with
    /// every side's own unit tests passing.
    /// </para>
    /// <para>
    /// The pump only ever runs for an <c>ok</c> verdict in practice. Every other outcome is
    /// answered and then closed by the server, because §3.3 requires a pending record to allocate
    /// no resources on a route that is deliberately exposed to the internet — so the first receive
    /// returns null and the attempt ends. The loop is written to tolerate a verdict arriving here
    /// anyway, since a future server is free to keep the socket and change its mind on it.
    /// </para>
    /// </remarks>
    private static async Task<(HandshakeResult Verdict, string Reason)> PumpAsync(
        AttemptContext context,
        IControlTransport transport,
        HandshakeResult initial,
        CancellationToken cancellationToken)
    {
        var current = initial;
        var reason = "The Fleet Manager closed the connection.";

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                var envelope = WireMessage.Decode(frame.Value.Span);
                if (envelope is null)
                {
                    // Not FrameLink traffic at all: a captive portal, a stray proxy, an unrelated
                    // WebSocket service. Ending the session hands this to the reconnect backoff.
                    // Ignoring it instead would spin this loop at full speed against a peer that is
                    // never going to say anything useful — the CPU-shaped twin of the v1 leak, and
                    // just as fatal on a frame.
                    reason = $"{context.Endpoint} answered something that is not a FrameLink server.";
                    break;
                }

                if (string.Equals(envelope.Kind, ControlWire.KindPing, StringComparison.Ordinal))
                {
                    await PongAsync(transport, envelope, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(envelope.Kind, ControlWire.KindSettings, StringComparison.Ordinal))
                {
                    // §3.4's dynamic values, and the only thing on this socket that changes what
                    // the reconciler converges on. Still not logic: a dictionary of strings, read
                    // by resources the catalog compiled in (§2.2, decision 15).
                    if (envelope.PayloadAs(ProtocolJson.Default.SettingsPush) is { } push)
                    {
                        context.OnSettings?.Invoke(push);
                    }

                    continue;
                }

                if (string.Equals(envelope.Kind, ControlWire.KindOperatorContact, StringComparison.Ordinal))
                {
                    // §2.7 item 8. Nothing on the frame converges on it and nothing is acted on:
                    // it is written to the agent's durable memory so that the sentence naming who
                    // to ask is on screen at the one moment this socket is guaranteed not to be —
                    // when the server is the thing that is unreachable (decision 71).
                    if (envelope.PayloadAs(ProtocolJson.Default.OperatorContact) is { } contact)
                    {
                        context.OnOperatorContact?.Invoke(contact);
                    }

                    continue;
                }

                if (string.Equals(envelope.Kind, ControlWire.KindShutdown, StringComparison.Ordinal))
                {
                    // §2.5 rung 5's other button, decision 92. The only inbound message whose
                    // success is a frame this socket can never reach again -- which is why it is
                    // read here, on a live socket, and never queued anywhere for later.
                    if (envelope.PayloadAs(ProtocolJson.Default.ShutdownRequest) is { } shutdown)
                    {
                        context.OnShutdown?.Invoke(shutdown);
                    }

                    continue;
                }

                if (string.Equals(envelope.Kind, ControlWire.KindRetry, StringComparison.Ordinal))
                {
                    // §2.5 rung 3. The one thing on this socket that changes what the reconciler
                    // is *allowed* to do rather than what it converges on — and the only way an
                    // escalated frame gets its attempt budget back, because the ledger that holds
                    // it is durable across both the restart and the update an operator would
                    // otherwise reach for.
                    if (envelope.PayloadAs(ProtocolJson.Default.RetryRequest) is { } retry)
                    {
                        context.OnRetry?.Invoke(retry);
                    }

                    continue;
                }

                if (!string.Equals(envelope.Kind, WireMessage.KindResult, StringComparison.Ordinal))
                {
                    // A well-formed message this build has no use for — a newer server using a
                    // channel M1 does not implement. Skipping it is forward compatibility, and it
                    // is bounded by the socket rather than by this loop.
                    continue;
                }

                var updated = envelope.PayloadAs(ProtocolJson.Default.HandshakeResult);
                if (updated is null || string.IsNullOrEmpty(updated.Status))
                {
                    continue;
                }

                if (HandshakeExchange.IsThrottle(updated))
                {
                    // The same rule as at the handshake, at the other place a verdict can arrive.
                    // A throttle is not a statement about this frame, so it must not replace one:
                    // the server has no reason to send it mid-session, and a build that let it
                    // through here would have a green frame lose its green to a message that was
                    // only ever about how often it may knock.
                    continue;
                }

                current = updated;
                Publish(context, updated, connected: true);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "The connection was closed by this frame.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // A session that dies mid-stream is a reconnect, not a crash. The reason reaches the
            // screen through the NoContact condition the loop publishes next.
            reason = $"The connection to {context.Endpoint} was lost: {exception.Message}";
        }

        return (current, reason);
    }

    /// <summary>Answers one liveness probe on the channel it arrived on.</summary>
    /// <remarks>
    /// Both the kind and the shape of the answer come from <c>FrameLink.Protocol</c>, so the
    /// question this method used to raise — does the agent's idea of a pong still match the
    /// server's — is now answered by the compiler rather than by an integration test.
    /// <see cref="ControlWire.SequenceOf"/> is deliberately lenient about the ping it is
    /// reading; the reasoning is on that method.
    /// </remarks>
    private static async Task PongAsync(
        IControlTransport transport,
        WireEnvelope ping,
        CancellationToken cancellationToken) =>
        await transport.SendAsync(
            WireMessage.Encode(
                ControlWire.KindPong,
                new AgentPong { Sequence = ControlWire.SequenceOf(ping) },
                ProtocolJson.Default.AgentPong,
                ProtocolConstants.ChannelControl),
            cancellationToken).ConfigureAwait(false);

    private static void Publish(AttemptContext context, HandshakeResult verdict, bool connected)
    {
        var condition = DeviceStateLadder.FromHandshake(verdict);

        context.Hub.Publish(status => status with
        {
            Condition = condition,
            LastAuthoritative = condition,
            Connected = connected,
            CurrentEndpoint = context.Endpoint,
            ServedAgentVersion = verdict.ServedAgentVersion ?? status.ServedAgentVersion,
            Attempt = 0,
            BackoffTotal = TimeSpan.Zero,
            BackoffEndsAt = null,
            Narration = new Narration
            {
                Detected = condition.Headline,
                WhyItMatters = condition.Detail,
                Action = verdict.Message,
            },
        });
    }

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt is already unwinding.
        }
    }
}
