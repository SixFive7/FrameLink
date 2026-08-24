namespace FrameLink.Control;

/// <summary>
/// What the server says about §2.5 rung 3's retry and rung 5's shutdown.
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

    /// <remarks>
    /// Information rather than a debug line, and worth the level: this is the last thing this server
    /// will ever record about a frame that carries out the instruction. Everything else on the
    /// socket is corroborated by the frame's next report; nothing corroborates this one, because the
    /// intended outcome is silence.
    /// </remarks>
    [LoggerMessage(
        EventId = 1412,
        Level = LogLevel.Information,
        Message = "Asked {DeviceId} to switch off. Nothing here can switch it back on — that needs "
            + "somebody at the frame.")]
    public static partial void ShutdownSent(this ILogger logger, string deviceId);

    [LoggerMessage(
        EventId = 1413,
        Level = LogLevel.Warning,
        Message = "Could not ask {DeviceId} to switch off; the socket had already gone. It is still "
            + "on unless something else took it down, and nothing replays this.")]
    public static partial void ShutdownMissed(this ILogger logger, Exception exception, string deviceId);
}
