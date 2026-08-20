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
        EventId = 1307,
        Level = LogLevel.Warning,
        Message = "Device {DeviceId} claimed package-set hash {Claimed}; this server computed "
            + "{Computed} over the same set. Storing under the computed one.")]
    public static partial void PackageHashMismatch(
        this ILogger logger,
        string deviceId,
        string claimed,
        string computed);

    [LoggerMessage(
        EventId = 1502,
        Level = LogLevel.Information,
        Message = "Rolled off {Count} device events older than the retention window.")]
    public static partial void ExpiredDeviceEvents(this ILogger logger, int count);

    [LoggerMessage(
        EventId = 1503,
        Level = LogLevel.Information,
        Message = "Rolled off {Entries} package-history entries and collected {Sets} package sets "
            + "nothing referenced any more.")]
    public static partial void ExpiredPackageHistory(this ILogger logger, int entries, int sets);

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Debug,
        Message = "Could not push settings to {DeviceId}; the socket had already gone.")]
    public static partial void SettingsPushMissed(this ILogger logger, Exception exception, string deviceId);

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Debug,
        Message = "Could not tell {DeviceId} who to contact; the socket had already gone.")]
    public static partial void ContactPushMissed(this ILogger logger, Exception exception, string deviceId);

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

    [LoggerMessage(
        EventId = 1700,
        Level = LogLevel.Warning,
        Message = "Refusing to build an image from {Path}: {Problem}")]
    public static partial void BaseImageRejected(this ILogger logger, string path, string problem);

    [LoggerMessage(
        EventId = 1701,
        Level = LogLevel.Error,
        Message = "Image build stopped at '{Step}': {Problem} Nothing was published.")]
    public static partial void ImageStepFailed(this ILogger logger, string step, string problem);

    [LoggerMessage(
        EventId = 1702,
        Level = LogLevel.Information,
        Message = "Built {FileName} (sha256 {Sha256}) seeded with {ControlUrl}.")]
    public static partial void ImageBuilt(
        this ILogger logger,
        string fileName,
        string sha256,
        string controlUrl);

    [LoggerMessage(
        EventId = 1703,
        Level = LogLevel.Error,
        Message = "An image build faulted. Nothing was published.")]
    public static partial void ImageBuildFaulted(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1800,
        Level = LogLevel.Warning,
        Message = "Fetching {Url} answered {Status}.")]
    public static partial void LiveKitFetchRefused(this ILogger logger, string url, int status);

    [LoggerMessage(
        EventId = 1801,
        Level = LogLevel.Warning,
        Message = "Fetching {Url} failed.")]
    public static partial void LiveKitFetchFailed(this ILogger logger, Exception exception, string url);

    [LoggerMessage(
        EventId = 1802,
        Level = LogLevel.Warning,
        Message = "The LiveKit server binary at {Path} could not be read.")]
    public static partial void LiveKitBinaryUnreadable(this ILogger logger, Exception exception, string path);

    [LoggerMessage(
        EventId = 1803,
        Level = LogLevel.Error,
        Message = "The LiveKit {Version} release could not be written to this server's data directory.")]
    public static partial void LiveKitInstallFailed(this ILogger logger, Exception exception, string version);

    [LoggerMessage(
        EventId = 1804,
        Level = LogLevel.Error,
        Message = "LiveKit {Version} rejected: {Fetched} bytes fetched, {Expected} expected.")]
    public static partial void LiveKitArchiveWrongLength(
        this ILogger logger,
        string version,
        long fetched,
        long expected);

    [LoggerMessage(
        EventId = 1805,
        Level = LogLevel.Error,
        Message = "LiveKit {Version} rejected: {FileName} does not match the checksum upstream published.")]
    public static partial void LiveKitArchiveRejected(this ILogger logger, string version, string fileName);

    [LoggerMessage(
        EventId = 1806,
        Level = LogLevel.Error,
        Message = "LiveKit {Version} rejected: the archive does not hold a {Expected}-byte '{Member}'.")]
    public static partial void LiveKitArchiveMalformed(
        this ILogger logger,
        string version,
        string member,
        long expected);

    [LoggerMessage(
        EventId = 1807,
        Level = LogLevel.Error,
        Message = "LiveKit {Version} rejected: the unpacked executable does not match the pinned digest.")]
    public static partial void LiveKitBinaryRejected(this ILogger logger, string version);

    [LoggerMessage(
        EventId = 1808,
        Level = LogLevel.Error,
        Message = "LiveKit {Version} rejected: the archive could not be read — {Problem}")]
    public static partial void LiveKitArchiveUnreadable(this ILogger logger, string version, string problem);

    [LoggerMessage(
        EventId = 1809,
        Level = LogLevel.Information,
        Message = "LiveKit {Version} installed at {Path}.")]
    public static partial void LiveKitInstalled(this ILogger logger, string version, string path);

    [LoggerMessage(
        EventId = 1810,
        Level = LogLevel.Information,
        Message = "LiveKit {Version} started as pid {Pid}, signalling on port {Port}.")]
    public static partial void LiveKitStarted(this ILogger logger, int pid, int port, string version);

    [LoggerMessage(
        EventId = 1811,
        Level = LogLevel.Warning,
        Message = "The LiveKit server could not be started: {Problem}")]
    public static partial void LiveKitStartRefused(this ILogger logger, string problem);

    [LoggerMessage(
        EventId = 1812,
        Level = LogLevel.Warning,
        Message = "The LiveKit server exited ({ExitCode}). Starting it again; calls do not connect "
            + "until it answers. Photos and everything else are unaffected.")]
    public static partial void LiveKitExited(this ILogger logger, int exitCode);

    [LoggerMessage(
        EventId = 1813,
        Level = LogLevel.Warning,
        Message = "Supervising the LiveKit server failed and was retried.")]
    public static partial void LiveKitSuperviseFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1814,
        Level = LogLevel.Information,
        Message = "The LiveKit API secret was rotated and {Issued} frames were issued a new call "
            + "token. Every token signed with the old secret is now refused.")]
    public static partial void LiveKitSecretRotated(this ILogger logger, int issued);

    [LoggerMessage(
        EventId = 1815,
        Level = LogLevel.Information,
        Message = "Issued {DeviceId} a call token as '{Identity}' in room '{Room}', expiring "
            + "{Expires}, because {Reason}.")]
    public static partial void CallTokenIssued(
        this ILogger logger,
        string deviceId,
        string identity,
        string room,
        DateTimeOffset expires,
        string reason);

    [LoggerMessage(
        EventId = 1816,
        Level = LogLevel.Information,
        Message = "Minted a call token for '{Identity}' in room '{Room}', expiring {Expires}. "
            + "Nothing renews or revokes it; it is gone when it expires.")]
    public static partial void GuestTokenIssued(
        this ILogger logger,
        string identity,
        string room,
        DateTimeOffset expires);

    [LoggerMessage(
        EventId = 1817,
        Level = LogLevel.Warning,
        Message = "Device {DeviceId} has call.identity set to '{Configured}', which is inside the "
            + "namespace reserved for people joining a call. It is being ignored and the device "
            + "id used instead, so that no frame can be knocked off its own call by a name "
            + "collision. Remove the setting to stop this repeating.")]
    public static partial void CallIdentityReserved(
        this ILogger logger,
        string deviceId,
        string configured);

    // §3.5's alerting. Warning rather than Information for an opened alert, because the container
    // log IS the delivery channel on a deployment with no webhook configured — an operator
    // grepping their logs has to be able to find these by level rather than by wording.

    [LoggerMessage(
        EventId = 1900,
        Level = LogLevel.Warning,
        Message = "ALERT {Kind} opened [{Key}]: {Subject} — {Detail}")]
    public static partial void AlertOpened(
        this ILogger logger,
        string kind,
        string key,
        string subject,
        string detail);

    [LoggerMessage(
        EventId = 1901,
        Level = LogLevel.Information,
        Message = "ALERT {Kind} cleared [{Key}]: {Subject}")]
    public static partial void AlertCleared(this ILogger logger, string kind, string key, string subject);

    [LoggerMessage(
        EventId = 1902,
        Level = LogLevel.Warning,
        Message = "The alert receiver at {Url} answered {Status} for [{Key}]. The alert stays open "
            + "and is delivered again on the next pass.")]
    public static partial void AlertDeliveryRefused(this ILogger logger, string url, int status, string key);

    [LoggerMessage(
        EventId = 1903,
        Level = LogLevel.Warning,
        Message = "The alert receiver could not be reached for [{Key}]. The alert stays open and is "
            + "delivered again on the next pass.")]
    public static partial void AlertDeliveryFailed(this ILogger logger, Exception exception, string key);

    [LoggerMessage(
        EventId = 1904,
        Level = LogLevel.Error,
        Message = "An alert evaluation pass failed. The next pass tries again; nothing else in the "
            + "Fleet Manager is affected.")]
    public static partial void AlertSweepFailed(this ILogger logger, Exception exception);
}
