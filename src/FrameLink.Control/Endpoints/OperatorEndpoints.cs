using FrameLink.Control.Agent;
using FrameLink.Control.Authentication;
using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Control.Endpoints;

/// <summary>The operator's API: adoption, blocking and the settings mechanism.</summary>
public static class OperatorEndpoints
{
    /// <summary>Maps the operator routes. Everything under <c>/api</c> except the two named
    /// in <see cref="OperatorGate"/> requires a session.</summary>
    public static void MapOperatorEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/status", GetStatus);
        app.MapPost("/api/session", SignIn);
        app.MapDelete("/api/session", SignOut);

        app.MapGet("/api/devices", ListDevicesAsync);
        app.MapPost("/api/devices/{deviceId}/adopt", AdoptAsync);
        app.MapPost("/api/devices/{deviceId}/block", BlockAsync);
        app.MapPost("/api/devices/{deviceId}/unblock", UnblockAsync);
        app.MapDelete("/api/devices/{deviceId}", ForgetAsync);

        app.MapGet("/api/settings", GetFleetSettingsAsync);
        app.MapPut("/api/settings/{key}", SetFleetSettingAsync);
        app.MapDelete("/api/settings/{key}", RemoveFleetSettingAsync);

        app.MapGet("/api/devices/{deviceId}/settings", GetDeviceSettingsAsync);
        app.MapPut("/api/devices/{deviceId}/settings/{key}", SetDeviceSettingAsync);
        app.MapDelete("/api/devices/{deviceId}/settings/{key}", RemoveDeviceSettingAsync);
    }

    /// <summary>Turns a stored row into what the GUI renders.</summary>
    public static DeviceView ToView(DeviceRecord record, AgentConnectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(registry);

        return new DeviceView
        {
            DeviceId = record.DeviceId,
            State = record.State switch
            {
                DeviceState.Adopted => "adopted",
                DeviceState.Blocked => "blocked",
                _ => "pending",
            },

            // Presence is the socket (§3.5). Nothing is read from the database to answer this.
            Online = registry.IsOnline(record.DeviceId),
            Name = record.DisplayName,
            HardwareSerial = record.HardwareSerial,
            AgentVersion = record.AgentVersion,
            AgentStatus = record.AgentStatus,
            ProtocolVersion = record.ProtocolVersion,
            ProtocolCompatible = record.ProtocolVersion == ProtocolConstants.Version,
            FirstSeenUtc = record.FirstSeenUtc,
            LastSeenUtc = record.LastSeenUtc,
        };
    }

    private static IResult GetStatus(OperatorCredential credential) =>
        Results.Json(
            new SetupStatus
            {
                Configured = credential.IsConfigured,
                Variable = OperatorCredential.EnvironmentVariable,
                Problem = credential.Problem,
                ComposeExample = credential.IsConfigured ? null : SetupPage.ComposeExample,
            },
            ControlJson.Default.SetupStatus);

    private static IResult SignIn(
        LoginRequest request,
        HttpContext context,
        OperatorCredential credential,
        OperatorSessions sessions)
    {
        if (!credential.IsConfigured)
        {
            // Not 401. There is no password to get wrong — the server is unconfigured, and
            // saying so is the whole of §3.2's first-run behaviour.
            return Results.Json(
                new ApiError { Error = "not-configured", Detail = credential.Problem },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!credential.Verify(request?.Password))
        {
            return Results.Json(
                new ApiError { Error = "unauthorized", Detail = "That is not the operator password." },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var (token, expires) = sessions.Create();
        context.Response.Cookies.Append(
            OperatorSessions.CookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,

                // Set only over HTTPS in production; the flag follows the request scheme so a
                // developer on plain HTTP still gets a working session.
                Secure = context.Request.IsHttps,
                Expires = expires,
                Path = "/",
            });

        return Results.Json(
            new LoginResponse { Token = token, ExpiresUtc = expires },
            ControlJson.Default.LoginResponse);
    }

    private static IResult SignOut(HttpContext context, OperatorSessions sessions)
    {
        sessions.Revoke(OperatorGate.ReadToken(context.Request));
        context.Response.Cookies.Delete(OperatorSessions.CookieName);
        return Results.NoContent();
    }

    private static async Task<IResult> ListDevicesAsync(
        bool? includeBlocked,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        // Blocked devices are filtered out by default but stay one query parameter away, so an
        // accidental block is reversible (§3.3).
        var withBlocked = includeBlocked ?? false;
        var records = await devices.ListAsync(withBlocked, cancellationToken).ConfigureAwait(false);

        return Results.Json(
            new DeviceListResponse
            {
                Devices = [.. records.Select(record => ToView(record, registry))],
                IncludeBlocked = withBlocked,
            },
            ControlJson.Default.DeviceListResponse);
    }

    private static async Task<IResult> AdoptAsync(
        string deviceId,
        AdoptRequest request,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        SettingsPublisher publisher,
        CancellationToken cancellationToken)
    {
        var record = await devices.AdoptAsync(deviceId, request?.Name, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(deviceId);
        }

        // The device learns it was adopted on its next connect — its handshake was answered
        // and closed while it was pending. This push only matters for the case where the row
        // was already adopted and the operator is renaming it.
        await publisher.PushAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return Results.Json(ToView(record, registry), ControlJson.Default.DeviceView);
    }

    private static async Task<IResult> BlockAsync(
        string deviceId,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        var record = await devices.BlockAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(deviceId);
        }

        // Blocking has to take effect now, not at the next reconnect. The frame's next
        // handshake is answered `blocked`, which stops its product (§2.6).
        registry.Find(deviceId)?.RequestClose();
        return Results.Json(ToView(record, registry), ControlJson.Default.DeviceView);
    }

    private static async Task<IResult> UnblockAsync(
        string deviceId,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        // Unblocking returns a device to the adoption queue rather than adopting it. The
        // operator blocked it; deciding to trust it again is a separate, deliberate press.
        var record = await devices.ReturnToPendingAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return record is null
            ? NotFound(deviceId)
            : Results.Json(ToView(record, registry), ControlJson.Default.DeviceView);
    }

    private static async Task<IResult> ForgetAsync(
        string deviceId,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        registry.Find(deviceId)?.RequestClose();
        return await devices.ForgetAsync(deviceId, cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : NotFound(deviceId);
    }

    private static async Task<IResult> GetFleetSettingsAsync(
        ISettingsStore settings,
        CancellationToken cancellationToken)
    {
        var values = await settings.GetFleetDefaultsAsync(cancellationToken).ConfigureAwait(false);
        var revision = await settings.GetRevisionAsync(cancellationToken).ConfigureAwait(false);

        return Results.Json(
            new FleetSettingsResponse { Revision = revision, Values = values },
            ControlJson.Default.FleetSettingsResponse);
    }

    private static async Task<IResult> SetFleetSettingAsync(
        string key,
        SettingValueRequest request,
        ISettingsStore settings,
        SettingsPublisher publisher,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadKey("A value is required.");
        }

        await settings.SetFleetDefaultAsync(key, request.Value, cancellationToken).ConfigureAwait(false);

        // A fleet default can move any device's effective value, so every online device is
        // told — except those where an override still wins, which resolve to the same value
        // they already had and ignore the push.
        await publisher.PushAllAsync(cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveFleetSettingAsync(
        string key,
        ISettingsStore settings,
        SettingsPublisher publisher,
        CancellationToken cancellationToken)
    {
        var removed = await settings.RemoveFleetDefaultAsync(key, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return NotFoundKey(key);
        }

        await publisher.PushAllAsync(cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> GetDeviceSettingsAsync(
        string deviceId,
        ISettingsStore settings,
        CancellationToken cancellationToken)
    {
        var fleet = await settings.GetFleetDefaultsAsync(cancellationToken).ConfigureAwait(false);
        var overrides = await settings.GetDeviceOverridesAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var resolved = await settings.ResolveAsync(deviceId, cancellationToken).ConfigureAwait(false);

        // Effective is empty for a device that is not adopted, and showing all three side by
        // side is what makes that visible instead of surprising.
        return Results.Json(
            new DeviceSettingsResponse
            {
                DeviceId = deviceId,
                Revision = resolved.Revision,
                FleetDefaults = fleet,
                Overrides = overrides,
                Effective = resolved.Values,
            },
            ControlJson.Default.DeviceSettingsResponse);
    }

    private static async Task<IResult> SetDeviceSettingAsync(
        string deviceId,
        string key,
        SettingValueRequest request,
        ISettingsStore settings,
        SettingsPublisher publisher,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadKey("A value is required.");
        }

        var written = await settings
            .SetDeviceOverrideAsync(deviceId, key, request.Value, cancellationToken)
            .ConfigureAwait(false);

        if (!written)
        {
            return Results.Json(
                new ApiError
                {
                    Error = "not-adopted",
                    Detail = "Only an adopted device can hold settings. Adopt it first.",
                },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status409Conflict);
        }

        await publisher.PushAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveDeviceSettingAsync(
        string deviceId,
        string key,
        ISettingsStore settings,
        SettingsPublisher publisher,
        CancellationToken cancellationToken)
    {
        var removed = await settings
            .RemoveDeviceOverrideAsync(deviceId, key, cancellationToken)
            .ConfigureAwait(false);

        if (!removed)
        {
            return NotFoundKey(key);
        }

        await publisher.PushAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static IResult NotFound(string deviceId) =>
        Results.Json(
            new ApiError { Error = "no-such-device", Detail = $"No device with id '{deviceId}'." },
            ControlJson.Default.ApiError,
            statusCode: StatusCodes.Status404NotFound);

    private static IResult NotFoundKey(string key) =>
        Results.Json(
            new ApiError { Error = "no-such-setting", Detail = $"No setting named '{key}'." },
            ControlJson.Default.ApiError,
            statusCode: StatusCodes.Status404NotFound);

    private static IResult BadKey(string detail) =>
        Results.Json(
            new ApiError { Error = "bad-request", Detail = detail },
            ControlJson.Default.ApiError,
            statusCode: StatusCodes.Status400BadRequest);
}
