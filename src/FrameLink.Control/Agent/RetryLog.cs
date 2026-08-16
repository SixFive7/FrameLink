namespace FrameLink.Control;

/// <summary>
/// The two things the server says about §2.5 rung 3's retry.
/// </summary>
/// <remarks>
/// A partial part of <see cref="ControlLog"/> rather than lines inside it, for the same reason
/// <c>RetryEndpoints</c> is its own file: the retry path is one feature and it reads better in one
/// place than as four additions scattered through four shared files. The generator sees a single
/// class either way.
/// </remarks>
internal static partial class ControlLog
{
    [LoggerMessage(
        EventId = 1410,
        Level = LogLevel.Information,
        Message = "Asked {DeviceId} to try {Resource} again.")]
    public static partial void RetrySent(this ILogger logger, string deviceId, string resource);

    [LoggerMessage(
        EventId = 1411,
        Level = LogLevel.Warning,
        Message = "Could not ask {DeviceId} to try again; the socket had already gone. "
            + "Nothing replays this — the operator has to press it again.")]
    public static partial void RetryMissed(this ILogger logger, Exception exception, string deviceId);
}
