using FrameLink.Control.Agent;
using FrameLink.Control.Updates;
using FrameLink.Protocol;

namespace FrameLink.Control.Endpoints;

/// <summary>
/// Everything a frame talks to: the socket, and the versionless update feed.
/// </summary>
/// <remarks>
/// All of it under <c>/agent</c>, which is the prefix the operator password does not guard
/// (§3.2) and the prefix an SSO proxy must be told to leave alone (§3.8). Keeping the device
/// surface to one prefix is what makes that deployment rule a single line in a reverse-proxy
/// config instead of a list somebody has to keep in sync.
/// </remarks>
public static class AgentEndpoints
{
    /// <summary>Maps the device routes.</summary>
    public static void MapAgentEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/agent", HandleSocketAsync);
        app.MapGet("/agent/release/{runtimeIdentifier}", GetRelease);
        app.MapGet("/agent/binary/{runtimeIdentifier}", GetBinary);
    }

    /// <summary>The address the rate limiter and the device row are attributed to.</summary>
    /// <remarks>
    /// Deliberately just the connection's peer. <c>X-Forwarded-For</c> reaches this only when
    /// the operator has named a trusted proxy, in which case <c>UseForwardedHeaders</c> has
    /// already rewritten the connection address — so the header is never read here directly
    /// and can never be used to mint a fresh source address per request.
    /// </remarks>
    public static string? ClientAddress(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress?.ToString();
    }

    private static async Task<IResult> HandleSocketAsync(
        HttpContext context,
        AgentSocketHandler handler,
        RegistrationRateLimiter limiter,
        ILogger<AgentSocketHandler> logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            // A person who pasted the control URL into a browser lands here, so it says what
            // the route is rather than returning a bare 400.
            return Results.Json(
                new ApiError
                {
                    Error = "websocket-required",
                    Detail = "This is the FrameLink device channel. Point a frame at this URL.",
                },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var address = ClientAddress(context);

        // The budget is spent before the upgrade, so a refused attempt costs one HTTP
        // response and never reaches the crypto, the database or a socket allocation.
        if (!limiter.TryAcquire(address))
        {
            logger.RateLimited(address);
            return Results.Json(
                new ApiError
                {
                    Error = "rate-limited",
                    Detail = "Too many connection attempts from this address.",
                },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await handler.HandleAsync(socket, address, context.RequestAborted).ConfigureAwait(false);
        return Results.Empty;
    }

    /// <summary>
    /// The update feed's metadata route.
    /// </summary>
    /// <remarks>
    /// Plain, versionless HTTPS outside the negotiated protocol (§4.2), polled hourly
    /// regardless of socket state, and unauthenticated on purpose: an agent whose protocol is
    /// too old to be adopted must still be able to repair itself, which is exactly the case
    /// where nothing else about it works.
    /// </remarks>
    private static IResult GetRelease(string runtimeIdentifier, AgentReleaseCatalog catalog)
    {
        var release = catalog.TryGet(runtimeIdentifier);
        return release is null
            ? Results.Json(
                new ApiError
                {
                    Error = "no-release",
                    Detail = $"This Fleet Manager serves no agent build for '{runtimeIdentifier}'.",
                },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status404NotFound)
            : Results.Json(release, ProtocolJson.Default.AgentRelease);
    }

    /// <summary>The binary itself, at the URL the metadata points to.</summary>
    private static IResult GetBinary(string runtimeIdentifier, AgentReleaseCatalog catalog)
    {
        var path = catalog.ResolveBinaryPath(runtimeIdentifier);
        if (path is null)
        {
            return Results.Json(
                new ApiError
                {
                    Error = "no-release",
                    Detail = $"This Fleet Manager serves no agent build for '{runtimeIdentifier}'.",
                },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status404NotFound);
        }

        // Range processing is on because a household link can drop mid-download and the
        // updater's next attempt should not have to start again from zero.
        return Results.File(
            path,
            "application/octet-stream",
            fileDownloadName: "fl-agent",
            enableRangeProcessing: true);
    }
}
