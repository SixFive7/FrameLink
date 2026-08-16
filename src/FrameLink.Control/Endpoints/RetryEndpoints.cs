using FrameLink.Control.Agent;
using FrameLink.Control.Storage;

namespace FrameLink.Control.Endpoints;

/// <summary>
/// The operator's <b>retry</b> — §2.5 rung 3, which had no route until now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own file, and the whole server half of the feature is in it.</b> The routes it maps hang
/// off <c>MapOperatorEndpoints</c> like every other operator route and are covered by the same
/// session gate; keeping the handler, the contract and its serialiser registration together is
/// what stops a feature this small from being four one-line additions nobody can find later.
/// </para>
/// <para>
/// <b>Two routes, one verb.</b> <c>POST /api/devices/{id}/retry</c> is the frame-level action an
/// operator reaches for when a device row says it has stopped reconciling — rung 4 stops the whole
/// frame, and a stopped frame can have several resources that gave up, so asking about each one
/// would be a list the operator has to reconstruct from a screen that already knows it.
/// <c>POST /api/devices/{id}/retry/{resource}</c> is the same verb aimed at one escalation, which
/// is what the reconcile screen renders a button beside. The agent decides nothing differently
/// between them; the payload simply carries a resource or does not.
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
    }

    private static async Task<IResult> RetryAsync(
        string deviceId,
        string? resource,
        IDeviceStore devices,
        RetryPublisher retries,
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
            .RetryAsync(deviceId, resource, time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        var named = string.IsNullOrWhiteSpace(resource) ? null : resource.Trim();

        var response = new RetryResponse
        {
            DeviceId = deviceId,
            Resource = named,
            Outcome = outcome is RetryOutcome.Sent ? "sent" : "offline",
            Detail = outcome is RetryOutcome.Sent
                ? named is null
                    ? "This frame has been asked to try every setting it gave up on again. Its attempt budgets are "
                        + "reset; the next reconcile pass picks them up."
                    : $"This frame has been asked to try '{named}' again. Its attempt budget is reset; the next "
                        + "reconcile pass picks it up."
                : "This frame is not connected, so nothing was delivered. A retry is not replayed on reconnect — "
                    + "press it again once the frame is online.",
        };

        return Results.Json(
            response,
            ControlJson.Default.RetryResponse,
            statusCode: outcome is RetryOutcome.Sent ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
    }
}
