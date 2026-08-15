namespace FrameLink.Control;

/// <summary>
/// Every log statement the Fleet Manager makes, source-generated.
/// </summary>
/// <remarks>
/// <para>
/// Not a style preference. <c>Directory.Build.props</c> runs the analysers at error severity
/// and §7.2 forbids weakening them to make code pass, and CA1848 fires on every
/// <c>logger.LogInformation("… {Thing}", thing)</c> call in the codebase. CA1873 fires
/// alongside it wherever an argument would be boxed to reach the <c>params object?[]</c>
/// overload.
/// </para>
/// <para>
/// The generator resolves both at once: each method below compiles to a cached delegate with
/// strongly typed arguments, so nothing allocates when the level is disabled and nothing is
/// boxed when it is not. It also happens to be the AOT-correct shape, since the generated
/// code contains no reflection.
/// </para>
/// </remarks>
internal static partial class ControlLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "FrameLink Fleet Manager starting with an operator password configured.")]
    public static partial void StartingConfigured(this ILogger logger);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "FrameLink Fleet Manager starting UNCONFIGURED. {Problem} Devices are being "
            + "answered 'not-configured' and the web interface shows how to fix it.")]
    public static partial void StartingUnconfigured(this ILogger logger, string? problem);

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Warning,
        Message = "Rejected a handshake from {Address}: the proof did not verify against the claimed key.")]
    public static partial void ProofRejected(this ILogger logger, string? address);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Device {DeviceId} speaks protocol {Claimed}; this server speaks {Served}.")]
    public static partial void ProtocolMismatch(this ILogger logger, string deviceId, int claimed, int served);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Rate limited a device connection from {Address}.")]
    public static partial void RateLimited(this ILogger logger, string? address);

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Debug,
        Message = "A connection from {Address} did not open with a hello.")]
    public static partial void NoHello(this ILogger logger, string? address);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Debug,
        Message = "A hello from {Address} could not be read.")]
    public static partial void UnreadableHello(this ILogger logger, string? address);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Debug,
        Message = "A connection from {Address} did not answer the challenge.")]
    public static partial void NoProof(this ILogger logger, string? address);

    [LoggerMessage(
        EventId = 1203,
        Level = LogLevel.Debug,
        Message = "A handshake from {Address} timed out.")]
    public static partial void HandshakeTimedOut(this ILogger logger, string? address);

    [LoggerMessage(
        EventId = 1204,
        Level = LogLevel.Debug,
        Message = "A handshake from {Address} failed at the transport.")]
    public static partial void HandshakeTransportFailed(this ILogger logger, Exception exception, string? address);

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "Device {DeviceId} reconnected; closing its previous socket.")]
    public static partial void DisplacedPreviousSocket(this ILogger logger, string deviceId);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Information,
        Message = "Device {DeviceId} is online from {Address}.")]
    public static partial void DeviceOnline(this ILogger logger, string deviceId, string? address);

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Information,
        Message = "Device {DeviceId} is offline.")]
    public static partial void DeviceOffline(this ILogger logger, string deviceId);

    [LoggerMessage(
        EventId = 1303,
        Level = LogLevel.Information,
        Message = "Device {DeviceId} missed its pong deadline after {Silence}. Closing the socket.")]
    public static partial void PongDeadlineMissed(this ILogger logger, string deviceId, TimeSpan silence);

    [LoggerMessage(
        EventId = 1304,
        Level = LogLevel.Debug,
        Message = "Device {DeviceId} sent kind '{Kind}' on channel '{Channel}'.")]
    public static partial void InboundMessage(
        this ILogger logger,
        string deviceId,
        string kind,
        string? channel);

    [LoggerMessage(
        EventId = 1305,
        Level = LogLevel.Warning,
        Message = "Device {DeviceId} sent a '{Kind}' this server could not read. Dropping it.")]
    public static partial void UnreadableTelemetry(this ILogger logger, string deviceId, string kind);

    [LoggerMessage(
        EventId = 1306,
        Level = LogLevel.Warning,
        Message = "Device {DeviceId} reported '{Kind}' on {Resource}: {Summary}")]
    public static partial void DeviceEscalated(
        this ILogger logger,
        string deviceId,
        string kind,
        string? resource,
        string summary);

    [LoggerMessage(
        EventId = 1502,
        Level = LogLevel.Information,
        Message = "Rolled off {Count} device events older than the retention window.")]
    public static partial void ExpiredDeviceEvents(this ILogger logger, int count);

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Debug,
        Message = "Could not push settings to {DeviceId}; the socket had already gone.")]
    public static partial void SettingsPushMissed(this ILogger logger, Exception exception, string deviceId);

    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Information,
        Message = "Expired {Count} un-adopted device rows.")]
    public static partial void ExpiredPendingDevices(this ILogger logger, int count);

    [LoggerMessage(
        EventId = 1501,
        Level = LogLevel.Error,
        Message = "A pending-device sweep failed.")]
    public static partial void SweepFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1600,
        Level = LogLevel.Warning,
        Message = "Could not hash the agent binary at {Path}.")]
    public static partial void ReleaseHashFailed(this ILogger logger, Exception exception, string path);

    [LoggerMessage(
        EventId = 1601,
        Level = LogLevel.Warning,
        Message = "Could not read the version sidecar at {Path}.")]
    public static partial void ReleaseVersionUnreadable(this ILogger logger, Exception exception, string path);
}
