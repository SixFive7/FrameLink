using FrameLink.Control.Agent;
using FrameLink.Control.Storage;

namespace FrameLink.Control.Endpoints;

/// <summary>
/// The operator's <b>retry</b> — §2.5 rung 3 — and the two power verbs of rung 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own file, and the whole server half of the feature is in it.</b> The routes it maps hang
/// off <c>MapOperatorEndpoints</c> like every other operator route and are covered by the same
/// session gate; keeping the handler, the contract and its serialiser registration together is
/// what stops a feature this small from being four one-line additions nobody can find later.
/// </para>
/// <para>
/// <b>Three routes, one verb.</b> <c>POST /api/devices/{id}/retry</c> is the frame-level action an
/// operator reaches for when a device row says it has stopped reconciling — rung 4 stops the whole
/// frame, and a stopped frame can have several resources that gave up, so asking about each one
/// would be a list the operator has to reconstruct from a screen that already knows it.
/// <c>POST /api/devices/{id}/retry/{resource}</c> is the same verb aimed at one escalation, which
/// is what the reconcile screen renders a button beside. The agent decides nothing differently
/// between them; the payload simply carries a resource or does not.
/// <c>POST /api/devices/{id}/restart</c> is the third, and it is the frame-level verb with the
/// operator's own ending on it — the budgets are reset and then the frame restarts, which is what
/// the button on the frame's own screen does. It is only ever delivered down a live socket, so an
/// offline frame answers 409 exactly as the other two do rather than accepting a restart nobody
/// will carry out.
/// </para>
/// <para>
/// <b>Four routes now, and the fourth is not a retry</b> (decision 92).
/// <c>POST /api/devices/{id}/shutdown</c> resets nothing, names no resource and is offered against a
/// frame with nothing wrong with it. It lives here because this is where an operator looks for what
/// they may do to a frame, and because it shares the two properties that matter — a live socket or a
/// 409, and never a queue — but it has its own handler, its own publisher and its own wire kind. The
/// kind is the load-bearing part: a member on <c>RetryRequest</c> would leave an agent that did not
/// understand it doing the <i>retry</i>, which is reconciling a frame whose operator asked for it to
/// be off.
/// </para>
/// <para>
/// <b>Offline is a 409, not a 200.</b> Unlike a settings push, nothing replays a retry on the next
/// connect — an attempt budget is not resolved from the server, it is held on the frame — so an
/// operator whose click went nowhere has to be told, and a script checking the status code must
/// not be told a frame is trying again when it is not.
/// </para>
/// <para>
/// <b>What is deliberately not validated: whether the named resource exists, or has escalated.</b>
/// The catalog lives in the agent and moves with the agent's version (§2.8), so the server holds no
/// list it could check against that would not immediately be a second, staler copy — the exact
/// duplication <c>ControlWire</c>'s own remarks describe going wrong once already. The agent
/// resolves the name against the catalog it is actually running, and a name it does not have costs
/// one ledger entry that no walk will ever read.
/// </para>
/// </remarks>
public static class RetryEndpoints
{
    /// <summary>Maps the retry routes onto the operator API.</summary>
    public static void MapRetryEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/devices/{deviceId}/retry", (
            string deviceId,
            IDeviceStore devices,
            RetryPublisher retries,
            TimeProvider time,
            CancellationToken cancellationToken) =>
            RetryAsync(deviceId, resource: null, devices, retries, time, cancellationToken));

        app.MapPost("/api/devices/{deviceId}/retry/{resource}", (
            string deviceId,
            string resource,
            IDeviceStore devices,
            RetryPublisher retries,
            TimeProvider time,
            CancellationToken cancellationToken) =>
            RetryAsync(deviceId, resource, devices, retries, time, cancellationToken));

        // The operator's third route and the frame's second button: "reboot -> forces a new retry",
        // pressed from here instead of from the panel. Same verb, same reset, same 409 when the
        // frame is not holding a socket — the restart is simply what the agent does after it.
        app.MapPost("/api/devices/{deviceId}/restart", (
            string deviceId,
            IDeviceStore devices,
            RetryPublisher retries,
            TimeProvider time,
            CancellationToken cancellationToken) =>
            RetryAsync(deviceId, resource: null, devices, retries, time, cancellationToken, reboot: true));

        // §2.5 rung 5's other button, and the only route here that is not a retry in any sense
        // (decision 92). No resource, no budget, no attempt: it is the power switch, and it is
        // deliberately not conditional on anything having gone wrong, because an off switch that
        // only worked on broken frames would be no off switch at all.
        app.MapPost("/api/devices/{deviceId}/shutdown", (
            string deviceId,
            IDeviceStore devices,
            ShutdownPublisher shutdowns,
            TimeProvider time,
            CancellationToken cancellationToken) =>
            ShutdownAsync(deviceId, devices, shutdowns, time, cancellationToken));
    }

    /// <summary>
    /// <b>Switch a frame off</b> — the one verb here nothing on this server can undo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own handler rather than a branch in <see cref="RetryAsync"/>, because none of that
    /// method's shape applies.</b> There is no resource to normalise, no budget to describe, and the
    /// sentence it answers with has to carry something no retry ever has to: that the operator has
    /// just spent the last remote action available on this frame.
    /// </para>
    /// <para>
    /// <b>Offline is a 409 and says what it means.</b> A frame with no socket is either already off
    /// or has lost its network, and this server cannot tell which — so the answer says both
    /// possibilities out loud rather than reporting a failure the operator would read as "try
    /// again". A 200 here would be the worst outcome available: an operator who believes a frame is
    /// off, is told so by this API, and walks away from a frame that is still running.
    /// </para>
    /// <para>
    /// <b>A 200 is "the bytes left down a live socket", and the response says so.</b> The frame may
    /// still refuse: a firmware write in flight turns both power verbs down, because mains loss in
    /// the middle of one destroys the microphone unit. Nothing here waits to find out — the socket
    /// closing is what a successful shutdown looks like, and it is indistinguishable from the frame
    /// having gone for any other reason — so the sentence names the refusal as a possible outcome
    /// instead of implying a certainty this route cannot have.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ShutdownAsync(
        string deviceId,
        IDeviceStore devices,
        ShutdownPublisher shutdowns,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var device = await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Results.Json(
                new ApiError { Error = "no-such-device", Detail = $"No device with id '{deviceId}'." },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status404NotFound);
        }

        if (device.State is not DeviceState.Adopted)
        {
            // The same gate the other three carry, and it costs nothing here: §3.3 closes a blocked
            // device's socket, so a blocked frame is unreachable anyway, and a pending one is a frame
            // this operator has not yet claimed. Consistency is worth more than the one case it
            // excludes.
            return Results.Json(
                new ApiError
                {
                    Error = "not-adopted",
                    Detail = "Only an adopted device holds a connection this server may steer, so only an "
                        + "adopted device can be asked to switch off. A frame that has not been adopted has "
                        + "to be switched off at the frame.",
                },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status409Conflict);
        }

        var outcome = await shutdowns
            .ShutdownAsync(deviceId, time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        var response = new RetryResponse
        {
            DeviceId = deviceId,
            Resource = null,
            Outcome = outcome is RetryOutcome.Sent ? "sent" : "offline",
            Detail = outcome is RetryOutcome.Sent
                ? "This frame has been asked to switch off. It is going down now and it will not come back "
                    + "on its own: nothing on this page, and no other remote action, can start it again — "
                    + "somebody has to be in the room with it and unplug it and plug it in again. One "
                    + "exception: if its microphone unit is having firmware written to it right now the frame "
                    + "refuses this and stays on, because losing power mid-write destroys that unit."
                : "This frame is not connected, so nothing was delivered and this frame has not been switched "
                    + "off. It is either already off or it has lost its network, and this server cannot tell "
                    + "which — somebody at the frame is the only way to find out. Nothing queues a shutdown "
                    + "for a frame that is not there.",
        };

        return Results.Json(
            response,
            ControlJson.Default.RetryResponse,
            statusCode: outcome is RetryOutcome.Sent ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> RetryAsync(
        string deviceId,
        string? resource,
        IDeviceStore devices,
        RetryPublisher retries,
        TimeProvider time,
        CancellationToken cancellationToken,
        bool reboot = false)
    {
        var device = await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Results.Json(
                new ApiError { Error = "no-such-device", Detail = $"No device with id '{deviceId}'." },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status404NotFound);
        }

        if (device.State is not DeviceState.Adopted)
        {
            // §3.3 gives a pending device nothing at all, and a blocked one less. A frame that has
            // not been adopted has no reconcile loop the operator is entitled to steer.
            return Results.Json(
                new ApiError
                {
                    Error = "not-adopted",
                    Detail = "Only an adopted device reconciles, so only an adopted device can be asked to try again.",
                },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status409Conflict);
        }

        var outcome = await retries
            .RetryAsync(deviceId, resource, time.GetUtcNow(), cancellationToken, reboot)
            .ConfigureAwait(false);

        var named = string.IsNullOrWhiteSpace(resource) ? null : resource.Trim();

        var response = new RetryResponse
        {
            DeviceId = deviceId,
            Resource = named,
            Outcome = outcome is RetryOutcome.Sent ? "sent" : "offline",
            Detail = outcome is RetryOutcome.Sent
                ? reboot
                    ? "This frame has been asked to restart and try again. Its attempt budgets are reset and it is "
                        + "going down now; it comes back in about a minute and starts reconciling from the top."
                    : named is null
                        ? "This frame has been asked to try every setting it gave up on again. Its attempt budgets are "
                            + "reset; the next reconcile pass picks them up."
                        : $"This frame has been asked to try '{named}' again. Its attempt budget is reset; the next "
                            + "reconcile pass picks it up."
                : reboot
                    ? "This frame is not connected, so nothing was delivered. A restart cannot be queued for a frame "
                        + "that is not there — press it again once the frame is online, or use the button on the frame "
                        + "itself."
                    : "This frame is not connected, so nothing was delivered. A retry is not replayed on reconnect — "
                        + "press it again once the frame is online.",
        };

        return Results.Json(
            response,
            ControlJson.Default.RetryResponse,
            statusCode: outcome is RetryOutcome.Sent ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
    }
}
