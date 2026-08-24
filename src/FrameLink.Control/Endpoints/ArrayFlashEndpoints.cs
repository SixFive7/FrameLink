using FrameLink.Control.Agent;
using FrameLink.Control.Firmware;
using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Control.Endpoints;

/// <summary>
/// The operator's half of decision 91's firmware write: composing an authorisation, withdrawing
/// one, and seeing what the frame did with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no second mechanism here.</b> The frame already knows how to be authorised: it reads
/// <c>audio.arrayFirmwareFlash</c> out of the settings it is pushed, takes it apart as
/// <c>&lt;sha256&gt;:&lt;ticket&gt;</c>, and spends the whole string durably before <c>dfu-util</c>
/// starts. So this writes exactly that setting, as a per-device override, through the same
/// <c>ISettingsStore</c> and the same <c>SettingsPublisher</c> every other setting goes through —
/// and an operator who prefers to type the value by hand on the settings screen still can, and gets
/// the same result. What these routes add is that <b>nothing an operator types decides which frame
/// is written</b>: the digest comes from the pin and the device id comes from the route.
/// </para>
/// <para>
/// <b>The unattended bypass is refused unless it is acknowledged, and the refusal is a 400.</b>
/// Mains loss during the write destroys the array and no interlock in the product can reach it;
/// the only mitigation that exists for an attended write is a person in the room who was told that
/// and agreed. Taking the bypass removes that person, so the acceptance is the thing that replaces
/// them, and a request that omits it has not made a choice — it has skipped one.
/// </para>
/// <para>
/// <b>Offline is not refused, unlike a retry.</b> A settings write is replayed: a frame that was
/// off when the operator pressed the button receives the authorisation in the settings frame of its
/// next connect, which is §3.4's mechanism working as designed. A retry has no such replay, which
/// is why <c>RetryEndpoints</c> answers 409 and this does not. The response says whether the frame
/// is online so the console can say when the push actually left.
/// </para>
/// <para>
/// <b>Withdrawing is deleting the override, and it is honest about what it cannot undo.</b> Once
/// the frame has spent the authorisation the write is either happening or over, and removing a
/// settings row reaches none of it. What withdrawal is for is the window between arming and the
/// frame acting — which on a frame waiting for its household to agree can be hours.
/// </para>
/// </remarks>
public static class ArrayFlashEndpoints
{
    /// <summary>How many of a device's events are read to work out its flash standing.</summary>
    /// <remarks>
    /// Enough that a frame which has been drifting noisily still has its last firmware event inside
    /// the window, and small enough that the screen costs one bounded query. The events the console
    /// renders are filtered down from this to the two kinds it is about.
    /// </remarks>
    public const int EventWindow = 200;

    /// <summary>The journal category these routes log under.</summary>
    /// <remarks>
    /// A named category rather than <c>ILogger&lt;T&gt;</c>, because the component doing the work is
    /// a static class and a static class cannot be a type argument. Spelling it out keeps the
    /// container's journal readable, which is where an operator looks when the question is whether
    /// this server ever issued the authorisation a frame says it never saw.
    /// </remarks>
    private const string LogCategory = "FrameLink.Control.Endpoints.ArrayFlashEndpoints";

    /// <summary>Maps the array-flash routes onto the operator API.</summary>
    public static void MapArrayFlashEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/devices/{deviceId}/array-flash", GetAsync);
        app.MapPost("/api/devices/{deviceId}/array-flash", AuthoriseAsync);
        app.MapDelete("/api/devices/{deviceId}/array-flash", WithdrawAsync);
    }

    private static async Task<IResult> GetAsync(
        string deviceId,
        IDeviceStore devices,
        ISettingsStore settings,
        IFleetTelemetryStore telemetry,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        var device = await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return NoSuchDevice(deviceId);
        }

        return Results.Json(
            await ViewAsync(device, settings, telemetry, registry, cancellationToken).ConfigureAwait(false),
            ControlJson.Default.ArrayFlashStatusResponse);
    }

    private static async Task<IResult> AuthoriseAsync(
        string deviceId,
        ArrayFlashRequest request,
        IDeviceStore devices,
        ISettingsStore settings,
        IFleetTelemetryStore telemetry,
        AgentConnectionRegistry registry,
        SettingsPublisher publisher,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loggers);

        var device = await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return NoSuchDevice(deviceId);
        }

        if (request is null)
        {
            return Refused("bad-request", "A body is required, even an empty one.");
        }

        if (request.Unattended && !request.Acknowledged)
        {
            return Refused(
                "not-acknowledged",
                "An unattended write is only authorised once the warnings have been accepted. "
                    + string.Join(" ", ArrayFlashPin.UnattendedWarning));
        }

        if (ArrayFlashTicket.NoteProblem(request.Note) is { } problem)
        {
            return Refused("bad-note", problem);
        }

        var authorisation = ArrayFlashTicket.Compose(
            deviceId,
            request.Unattended,
            request.Note,
            clock.GetUtcNow());

        var written = await settings
            .SetDeviceOverrideAsync(deviceId, ArrayFlashPin.AuthorisationKey, authorisation, cancellationToken)
            .ConfigureAwait(false);

        if (!written)
        {
            // The same 409 the settings route answers, and for the same structural reason: §3.3
            // gives a device that has not been adopted nothing at all, and a settings row is
            // something. A frame in that state has no reconcile loop to read an authorisation.
            return Results.Json(
                new ApiError
                {
                    Error = "not-adopted",
                    Detail = "Only an adopted device can hold settings, so only an adopted device can be authorised "
                        + "to write firmware. Adopt it first.",
                },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status409Conflict);
        }

        // The one operation in this product that cannot be undone by rewriting the card, so it goes
        // in the server's own log as well as the frame's — the container's journal is what an
        // operator has when a frame's own record is the thing in question.
        loggers.CreateLogger(LogCategory).ArrayFlashAuthorised(
            deviceId,
            request.Unattended,
            ArrayFlashPin.Target.Version,
            authorisation);

        await publisher.PushAsync(deviceId, cancellationToken).ConfigureAwait(false);

        var record = await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false) ?? device;

        return Results.Json(
            await ViewAsync(record, settings, telemetry, registry, cancellationToken).ConfigureAwait(false),
            ControlJson.Default.ArrayFlashStatusResponse);
    }

    private static async Task<IResult> WithdrawAsync(
        string deviceId,
        IDeviceStore devices,
        ISettingsStore settings,
        IFleetTelemetryStore telemetry,
        AgentConnectionRegistry registry,
        SettingsPublisher publisher,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loggers);

        var device = await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return NoSuchDevice(deviceId);
        }

        var removed = await settings
            .RemoveDeviceOverrideAsync(deviceId, ArrayFlashPin.AuthorisationKey, cancellationToken)
            .ConfigureAwait(false);

        if (removed)
        {
            loggers.CreateLogger(LogCategory).ArrayFlashWithdrawn(deviceId);
            await publisher.PushAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }

        return Results.Json(
            await ViewAsync(device, settings, telemetry, registry, cancellationToken).ConfigureAwait(false),
            ControlJson.Default.ArrayFlashStatusResponse);
    }

    private static async Task<ArrayFlashStatusResponse> ViewAsync(
        DeviceRecord device,
        ISettingsStore settings,
        IFleetTelemetryStore telemetry,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        // The override, never the resolved value. An authorisation is per-device by construction,
        // and reading the resolution would let a fleet default this screen never wrote show up as
        // this frame's own authorisation — which is exactly the fleet-wide arming decision 91
        // rules out. A fleet default is still legible on the settings screen, where it belongs.
        var overrides = await settings
            .GetDeviceOverridesAsync(device.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        var authorisation = overrides.GetValueOrDefault(ArrayFlashPin.AuthorisationKey);

        var events = await telemetry
            .ListEventsAsync(device.DeviceId, EventWindow, cancellationToken)
            .ConfigureAwait(false);

        var mine = events
            .Where(moment =>
                string.Equals(moment.Kind, DeviceEventKinds.ArrayFlash, StringComparison.Ordinal)
                || string.Equals(moment.Kind, DeviceEventKinds.ArrayFirmware, StringComparison.Ordinal))
            .ToArray();

        var online = registry.IsOnline(device.DeviceId);

        // <b>Only for a frame with a socket open, and that is the whole of the staleness guard.</b>
        // The self-report is the current picture rather than history: it is not buffered when a
        // frame is offline and it is not cleared when one disappears, so the last thing a frame said
        // before it went quiet stays in this column for as long as the row exists. Reading it on an
        // offline frame would draw a bar at 41% for a week over a write that may have finished, may
        // have failed, or may have taken the frame with it. Offline therefore falls back to the
        // event trail, which is history and is allowed to be old.
        var live = online ? ArrayFlashWire.Read(device.AgentStatus) : null;

        var reading = ArrayFlashReading.From(mine, authorisation, live);

        return new ArrayFlashStatusResponse
        {
            DeviceId = device.DeviceId,
            Adopted = device.State is DeviceState.Adopted,
            Online = online,
            Target = new ArrayFlashTargetView
            {
                Name = ArrayFlashPin.Target.Name,
                Version = ArrayFlashPin.Target.Version,
                Sha256 = ArrayFlashPin.Target.Sha256,
                SizeBytes = ArrayFlashPin.Target.SizeBytes,
            },
            UnattendedPrefix = ArrayFlashPin.UnattendedPrefix,
            UnattendedWarning = ArrayFlashPin.UnattendedWarning,
            Authorisation = authorisation is { Length: > 0 }
                ? new ArrayFlashAuthorisationView
                {
                    Value = authorisation,
                    Ticket = ArrayFlashTicket.TicketOf(authorisation),
                    Unattended = ArrayFlashTicket.IsUnattendedFor(authorisation, device.DeviceId),
                    NamesTheTarget = ArrayFlashTicket.NamesTheTarget(authorisation),
                    IssuedUtc = ArrayFlashTicket.IssuedAt(authorisation),
                    Note = ArrayFlashTicket.NoteOf(authorisation),
                    UnattendedDeviceId = ArrayFlashTicket.UnattendedDeviceId(authorisation),
                }
                : null,
            Phase = reading.Phase,
            Detail = reading.Detail,
            Refusal = reading.Refusal,
            Progress = reading.Progress is { } running
                ? new ArrayFlashProgressView
                {
                    Stage = running.Stage ?? string.Empty,
                    Percent = running.Percent,
                    BytesWritten = running.BytesWritten,
                    BytesTotal = running.BytesTotal,
                    ElapsedSeconds = running.ElapsedSeconds,

                    // Derived here rather than in the browser, so the bar on this screen and the bar
                    // on the frame's own panel are filled to the same place by the same rule.
                    Fraction = running.Fraction,
                }
                : null,
            ReportedUtc = reading.ReportedUtc,
            RunningFirmware = reading.RunningFirmware,
            RunningFirmwareUtc = reading.RunningFirmwareUtc,
            Events = mine,
        };
    }

    private static IResult NoSuchDevice(string deviceId) =>
        Results.Json(
            new ApiError { Error = "no-such-device", Detail = $"No device with id '{deviceId}'." },
            ControlJson.Default.ApiError,
            statusCode: StatusCodes.Status404NotFound);

    private static IResult Refused(string error, string detail) =>
        Results.Json(
            new ApiError { Error = error, Detail = detail },
            ControlJson.Default.ApiError,
            statusCode: StatusCodes.Status400BadRequest);
}
