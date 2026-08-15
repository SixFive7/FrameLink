namespace FrameLink.Agent.Link;

/// <summary>
/// One live, message-framed connection to a Fleet Manager.
/// </summary>
/// <remarks>
/// <para>
/// <b>The disposal contract is the point of this interface.</b> §4.1 requires cleanup per failed
/// attempt, so the rules are stated here rather than left to each implementation to infer:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="IAsyncDisposable.DisposeAsync"/> releases every operating-system and managed
/// resource the transport holds, is safe to call more than once, and never throws.
/// </description></item>
/// <item><description>
/// A transport that has been disposed is finished. It is never reconnected, reused or pooled —
/// a fresh attempt gets a fresh transport, because reuse is how state from a failed attempt
/// survives into the next one.
/// </description></item>
/// </list>
/// </remarks>
public interface IControlTransport : IAsyncDisposable
{
    /// <summary>Sends one complete message.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken);

    /// <summary>
    /// Receives one complete message, or <see langword="null"/> once the peer has closed.
    /// </summary>
    /// <remarks>
    /// A close is a return value, not an exception: the server closing the socket after
    /// answering is ordinary, expected traffic, and routing it through the exception path would
    /// make it indistinguishable from a failure in the reconnect loop's own bookkeeping.
    /// </remarks>
    ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Opens transports.</summary>
public interface IControlTransportFactory
{
    /// <summary>
    /// Connects to <paramref name="endpoint"/>, or throws.
    /// </summary>
    /// <remarks>
    /// <b>An implementation that throws must have released everything it allocated first.</b>
    /// This is the half of the cleanup contract a caller cannot enforce with a
    /// <c>finally</c> — a failure inside the connect never hands back a handle to dispose — so
    /// it is stated as an obligation on the factory and has its own test.
    /// </remarks>
    ValueTask<IControlTransport> ConnectAsync(Uri endpoint, CancellationToken cancellationToken);
}
