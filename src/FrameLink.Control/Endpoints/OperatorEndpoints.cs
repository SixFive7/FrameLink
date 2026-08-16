using System.Net;
using FrameLink.Control.Agent;
using FrameLink.Control.Authentication;
using FrameLink.Control.LiveKit;
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

        app.MapGet("/api/events", StreamFleetEventsAsync);

        app.MapGet("/api/devices", ListDevicesAsync);
        app.MapGet("/api/devices/{deviceId}", GetDeviceAsync);
        app.MapPost("/api/devices/{deviceId}/adopt", AdoptAsync);
        app.MapPost("/api/devices/{deviceId}/block", BlockAsync);
        app.MapPost("/api/devices/{deviceId}/unblock", UnblockAsync);
        app.MapDelete("/api/devices/{deviceId}", ForgetAsync);

        app.MapGet("/api/settings", GetFleetSettingsAsync);
        app.MapPut("/api/settings/{key}", SetFleetSettingAsync);
        app.MapDelete("/api/settings/{key}", RemoveFleetSettingAsync);

        app.MapGet("/api/devices/{deviceId}/reconcile", GetReconcileAsync);
        app.MapGet("/api/devices/{deviceId}/events", GetDeviceEventsAsync);

        app.MapGet("/api/packages", GetFleetPackagesAsync);
        app.MapGet("/api/devices/{deviceId}/packages", GetDevicePackagesAsync);

        app.MapGet("/api/devices/{deviceId}/settings", GetDeviceSettingsAsync);
        app.MapPut("/api/devices/{deviceId}/settings/{key}", SetDeviceSettingAsync);
        app.MapDelete("/api/devices/{deviceId}/settings/{key}", RemoveDeviceSettingAsync);

        app.MapGet("/api/livekit", GetLiveKitAsync);
        app.MapPost("/api/livekit/rotate", RotateLiveKitAsync);
        app.MapPost("/api/devices/{deviceId}/call-token", IssueCallTokenAsync);
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
            Health = AgentHealth.Classify(record.AgentStatus),
            ProtocolVersion = record.ProtocolVersion,
            ProtocolCompatible = record.ProtocolVersion == ProtocolConstants.Version,
            FirstSeenUtc = record.FirstSeenUtc,
            LastSeenUtc = record.LastSeenUtc,
            StateChangedUtc = record.StateChangedUtc,
            LastRemoteAddress = record.LastRemoteAddress,
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
                Secure = ShouldSecureCookie(
                    context.Request.IsHttps,
                    context.Connection.RemoteIpAddress),
                Expires = expires,
                Path = "/",
            });

        return Results.Json(
            new LoginResponse { Token = token, ExpiresUtc = expires },
            ControlJson.Default.LoginResponse);
    }

    /// <summary>
    /// Whether the operator's session cookie is marked <c>Secure</c>. Yes, unless a developer
    /// is plainly on loopback over plain HTTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be <c>context.Request.IsHttps</c>, which is false on the one deployment
    /// that most needs the flag: §3.8 terminates TLS at Traefik, so what reaches Kestrel is
    /// plain HTTP, and <c>IsHttps</c> only becomes true once <c>UseForwardedHeaders</c> has
    /// rewritten the scheme — which <c>ControlApp</c> only installs when
    /// <c>FRAMELINK_TRUSTED_PROXIES</c> is set. Forgetting one environment variable therefore
    /// issued the operator's session cookie without <c>Secure</c> over an HTTPS site, and
    /// nothing anywhere said so.
    /// </para>
    /// <para>
    /// Inverting the default fixes that permanently: a misconfiguration now costs a developer
    /// on a non-loopback plain-HTTP address a cookie the browser declines to store, which is
    /// visible in the first second, rather than costing a production operator a credential
    /// that travels in clear on the first request that escapes TLS.
    /// </para>
    /// </remarks>
    /// <param name="isHttps">Whether the request reached this process over TLS.</param>
    /// <param name="remoteAddress">The peer address, after any trusted-proxy rewriting.</param>
    /// <returns>True when the cookie must carry <c>Secure</c>.</returns>
    public static bool ShouldSecureCookie(bool isHttps, IPAddress? remoteAddress)
    {
        if (isHttps)
        {
            return true;
        }

        // Browsers treat loopback as a secure context, so `Secure` would mostly work here too —
        // but "mostly" has an exception per browser, and a developer running `dotnet run` is the
        // one person who can see the problem instantly and is not exposed by it.
        return remoteAddress is null || !IPAddress.IsLoopback(remoteAddress);
    }

    private static IResult SignOut(HttpContext context, OperatorSessions sessions)
    {
        sessions.Revoke(OperatorGate.ReadToken(context.Request));
        context.Response.Cookies.Delete(OperatorSessions.CookieName);
        return Results.NoContent();
    }

    /// <summary>
    /// Server-sent events carrying the id of every device that changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The console polled <c>/api/devices</c> every four seconds because presence <i>is</i> the
    /// socket (§3.5) and there was no way for the server to say so. §3.3 optimises for one
    /// moment above all others — a frame plugged in on the bench, and the row appearing — and
    /// four seconds is a long time to stand there wondering whether the URL was wrong.
    /// </para>
    /// <para>
    /// Deliberately the smallest thing that closes that gap: an id, never a device. The console
    /// re-reads the list, so there is exactly one place a fleet row is rendered from and no
    /// second serialisation to keep in step. A missed or coalesced event costs a few hundred
    /// milliseconds, not correctness — the poll is still there underneath, just slower.
    /// </para>
    /// <para>
    /// The keep-alive comment matters as much as the events: §3.8 puts Traefik in front of
    /// this, and an idle proxy will close a stream that says nothing.
    /// </para>
    /// </remarks>
    private static async Task StreamFleetEventsAsync(
        HttpContext context,
        FleetEvents events,
        ControlOptions options,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        // nginx and friends buffer a response with no Content-Length by default, which turns a
        // live stream into a page that arrives all at once, some minutes later.
        context.Response.Headers["X-Accel-Buffering"] = "no";

        // The subscription is released on every exit path, including a browser that vanishes
        // mid-write. One console per tab, tabs closed all day: a subscription that outlived its
        // reader would be the v1 LiveKit leak with a different shape.
        using var subscription = events.Subscribe();

        try
        {
            // Before anything has happened, so the client knows the stream is open rather than
            // merely accepted. An EventSource that has connected but heard nothing is
            // indistinguishable from one that never will.
            await WriteEventAsync(context, "ready", string.Empty, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(options.PingInterval);

                var woken = true;
                try
                {
                    woken = await subscription.Reader.WaitToReadAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    // Nothing happened for a whole interval. A comment frame keeps the
                    // connection alive without inventing an event that did not occur.
                    await WriteRawAsync(context, ": keep-alive\n\n", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!woken)
                {
                    return;
                }

                // Drain, so a burst — a fleet default changing every device at once — is one
                // wake-up for the console rather than one per row.
                while (subscription.Reader.TryRead(out var deviceId))
                {
                    await WriteEventAsync(context, "device", deviceId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The operator closed the tab. Ordinary, and not worth a log line per tab.
        }
        catch (IOException)
        {
            // Same thing, seen from the write side: the socket went while a frame was going out.
        }
    }

    private static Task WriteEventAsync(
        HttpContext context,
        string name,
        string data,
        CancellationToken cancellationToken) =>
        WriteRawAsync(context, $"event: {name}\ndata: {data}\n\n", cancellationToken);

    private static async Task WriteRawAsync(HttpContext context, string frame, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>One device, by id.</summary>
    /// <remarks>
    /// Without this the detail screen has to find its row in the fleet list, which means a hard
    /// page load shows a placeholder until the next poll lands, and a blocked device's own page
    /// only works because the list is fetched with <c>includeBlocked=true</c> whatever the
    /// toggle says. Both are the same missing route wearing two costumes.
    /// </remarks>
    private static async Task<IResult> GetDeviceAsync(
        string deviceId,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        var record = await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return record is null
            ? NotFound(deviceId)
            : Results.Json(ToView(record, registry), ControlJson.Default.DeviceView);
    }

    /// <summary>Adopts a device, optionally naming it. Also the rename route.</summary>
    /// <remarks>
    /// <para>
    /// The name arrives as a query parameter and there is no request body. It used to be a
    /// non-nullable <c>AdoptRequest</c>, which makes the body <i>required</i> in a minimal API —
    /// so adopting a frame without naming it, which is what adopting a frame usually is, was a
    /// framework 400. Making that parameter nullable fixes only half of it: a POST carrying no
    /// <c>Content-Type</c> header is not routed to a body-bound endpoint at all, so it reached
    /// the SPA fallback and the caller got 200 <c>text/html</c> back from an API call.
    /// </para>
    /// <para>
    /// One optional scalar does not need a JSON envelope to travel in. Dropping it puts
    /// <c>/adopt</c> in line with <c>/block</c> and <c>/unblock</c>, which take nothing, and
    /// makes the route answer correctly to any client that just posts to it.
    /// </para>
    /// </remarks>
    private static async Task<IResult> AdoptAsync(
        string deviceId,
        string? name,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        SettingsPublisher publisher,
        CallProvisioning calls,
        FleetEvents events,
        CancellationToken cancellationToken)
    {
        var adoption = await devices
            .AdoptAsync(deviceId, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), cancellationToken)
            .ConfigureAwait(false);

        switch (adoption.Result)
        {
            case DeviceAdoptionResult.Unknown:
                return NotFound(deviceId);

            case DeviceAdoptionResult.Blocked:
                // §3.3: re-trusting a device is a separate, deliberate press. Adopting straight
                // out of `blocked` would collapse unblock's two steps into one and make the rule
                // an accident of which button the GUI happens to draw.
                return Results.Json(
                    new ApiError
                    {
                        Error = "blocked",
                        Detail = "This device is blocked. Unblock it first — that returns it to "
                            + "the adoption queue, where it can be adopted deliberately.",
                    },
                    ControlJson.Default.ApiError,
                    statusCode: StatusCodes.Status409Conflict);

            default:
                // §3.3: "Adoption binds that key to a record and issues identity, room, LiveKit
                // token and desired values." This is that moment, and it has to be here rather
                // than at the frame's next connect — the settings store refuses a per-device
                // write until the row is adopted, so a second earlier there was structurally
                // nowhere to put a token, and a second later is when the frame comes to collect
                // it. A rename lands here too, which re-mints: the display name is the token's
                // `name` claim, so it is what the rest of the household sees on screen.
                await calls.ReviewAsync(deviceId, force: false, cancellationToken).ConfigureAwait(false);

                // The device learns it was adopted on its next connect — its handshake was
                // answered and closed while it was pending. This push only matters for the case
                // where the row was already adopted and the operator is renaming it.
                await publisher.PushAsync(deviceId, cancellationToken).ConfigureAwait(false);
                events.Publish(deviceId);
                return Results.Json(ToView(adoption.Record!, registry), ControlJson.Default.DeviceView);
        }
    }

    private static async Task<IResult> BlockAsync(
        string deviceId,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        FleetEvents events,
        CancellationToken cancellationToken)
    {
        var record = await devices.BlockAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(deviceId);
        }

        events.Publish(deviceId);

        // Blocking has to take effect now, not at the next reconnect. The frame's next
        // handshake is answered `blocked`, which stops its product (§2.6).
        registry.Find(deviceId)?.RequestClose();
        return Results.Json(ToView(record, registry), ControlJson.Default.DeviceView);
    }

    private static async Task<IResult> UnblockAsync(
        string deviceId,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        FleetEvents events,
        CancellationToken cancellationToken)
    {
        // Unblocking returns a device to the adoption queue rather than adopting it. The
        // operator blocked it; deciding to trust it again is a separate, deliberate press.
        var record = await devices.ReturnToPendingAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(deviceId);
        }

        events.Publish(deviceId);
        return Results.Json(ToView(record, registry), ControlJson.Default.DeviceView);
    }

    private static async Task<IResult> ForgetAsync(
        string deviceId,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        FleetEvents events,
        CancellationToken cancellationToken)
    {
        registry.Find(deviceId)?.RequestClose();
        if (!await devices.ForgetAsync(deviceId, cancellationToken).ConfigureAwait(false))
        {
            return NotFound(deviceId);
        }

        events.Publish(deviceId);
        return Results.NoContent();
    }

    /// <summary>A device's live reconciliation state (§3.5).</summary>
    /// <remarks>
    /// A 404 only when the device is unknown. A known device with no report yet answers 200 with
    /// a null report, because "adopted a second ago and has not reported" is a state the screen
    /// has to render and is not an error.
    /// </remarks>
    private static async Task<IResult> GetReconcileAsync(
        string deviceId,
        IDeviceStore devices,
        IFleetTelemetryStore telemetry,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        if (await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFound(deviceId);
        }

        var report = await telemetry.GetReportAsync(deviceId, cancellationToken).ConfigureAwait(false);

        return Results.Json(
            new DeviceReconcileResponse
            {
                DeviceId = deviceId,
                Online = registry.IsOnline(deviceId),
                Report = report,
            },
            ControlJson.Default.DeviceReconcileResponse);
    }

    /// <summary>A device's recent events, newest first.</summary>
    private static async Task<IResult> GetDeviceEventsAsync(
        string deviceId,
        int? limit,
        IDeviceStore devices,
        IFleetTelemetryStore telemetry,
        CancellationToken cancellationToken)
    {
        if (await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFound(deviceId);
        }

        var events = await telemetry
            .ListEventsAsync(deviceId, limit ?? 50, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(
            new DeviceEventsResponse { DeviceId = deviceId, Events = events },
            ControlJson.Default.DeviceEventsResponse);
    }

    /// <summary>The fleet-wide package comparison (§3.5).</summary>
    /// <remarks>
    /// <para>
    /// <b>The route answers with differences, never with sets.</b> Ten frames carrying ~930
    /// packages each is nine thousand facts, and the number an operator can act on is the handful
    /// they disagree about — so the comparison happens here, where the sets already are, and what
    /// crosses to the browser is the disagreement plus five numbers per frame.
    /// </para>
    /// <para>
    /// Frames that have never reported are simply absent from the list rather than present with
    /// zeros, which is the same distinction the reconcile route makes with a null report.
    /// </para>
    /// </remarks>
    private static async Task<IResult> GetFleetPackagesAsync(
        IPackageStore packages,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        var sets = await packages.ListAsync(cancellationToken).ConfigureAwait(false);
        var records = await devices.ListAsync(includeBlocked: true, cancellationToken).ConfigureAwait(false);
        var names = records.ToDictionary(record => record.DeviceId, record => record.DisplayName, StringComparer.Ordinal);

        var (rows, total, agreed) = PackageDrift.AcrossFleet(sets);

        return Results.Json(
            new FleetPackagesResponse
            {
                Devices =
                [
                    .. sets.Select(set => PackageDrift.Summarise(
                        set,
                        names.GetValueOrDefault(set.DeviceId),
                        registry.IsOnline(set.DeviceId))),
                ],
                Agreed = agreed,
                DisagreementTotal = total,
                Disagreements = rows,
                DistinctSets = sets.Select(set => set.ContentHash).Distinct(StringComparer.Ordinal).Count(),
                BaselineCount = PackageBaseline.Versions.Count,
                BaselineReviewedUtc = PackageBaseline.ReviewedUtc,
            },
            ControlJson.Default.FleetPackagesResponse);
    }

    /// <summary>One frame's packages: how it stands, and what moved on it recently.</summary>
    /// <remarks>
    /// A 404 only when the device is unknown. A known frame that has never sent an inventory
    /// answers 200 with a null summary and empty lists, because "adopted and has not reported
    /// yet" is a state the screen renders rather than an error.
    /// </remarks>
    private static async Task<IResult> GetDevicePackagesAsync(
        string deviceId,
        int? history,
        IPackageStore packages,
        IDeviceStore devices,
        AgentConnectionRegistry registry,
        CancellationToken cancellationToken)
    {
        var record = await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(deviceId);
        }

        var online = registry.IsOnline(deviceId);
        var set = await packages.GetAsync(deviceId, cancellationToken).ConfigureAwait(false);

        if (set is null)
        {
            return Results.Json(
                new DevicePackagesResponse
                {
                    DeviceId = deviceId,
                    Online = online,
                    Drift = [],
                    Recent = [],
                    BaselineCount = PackageBaseline.Versions.Count,
                    BaselineReviewedUtc = PackageBaseline.ReviewedUtc,
                },
                ControlJson.Default.DevicePackagesResponse);
        }

        var entries = await packages
            .ListHistoryAsync(deviceId, (history ?? 8) + 1, cancellationToken)
            .ConfigureAwait(false);

        var summary = PackageDrift.Summarise(set, record.DisplayName, online);

        return Results.Json(
            new DevicePackagesResponse
            {
                DeviceId = deviceId,
                Online = online,
                Summary = summary,
                ObservedCount = set.ObservedCount,
                DriftTotal = summary.Ahead + summary.Behind + summary.Missing + summary.Extra,
                Drift = PackageDrift.AgainstBaseline(set.Packages),
                Recent = PackageDrift.Timeline(entries),
                BaselineCount = PackageBaseline.Versions.Count,
                BaselineReviewedUtc = PackageBaseline.ReviewedUtc,
            },
            ControlJson.Default.DevicePackagesResponse);
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
        CallProvisioning calls,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadKey("A value is required.");
        }

        await settings.SetFleetDefaultAsync(key, request.Value, cancellationToken).ConfigureAwait(false);

        // Unconditionally, and never keyed on which setting moved. A call token is bound to a
        // room, so changing the fleet's `call.room` invalidates every token in the fleet — but
        // §3.4 makes settings "not a fixed list but a generic mechanism", and a route that knew
        // one key by name would be exactly the hard-coding that rules out. Reviewing every device
        // is a token decode and a handful of string comparisons each, and re-mints nothing when
        // nothing needs it.
        await calls.ReviewFleetAsync(force: false, cancellationToken).ConfigureAwait(false);

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
        CallProvisioning calls,
        CancellationToken cancellationToken)
    {
        var removed = await settings.RemoveFleetDefaultAsync(key, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return NotFoundKey(key);
        }

        // Removing a default moves effective values exactly as setting one does — deleting
        // `call.room` drops every frame back to the catalog fallback, which is a different room.
        await calls.ReviewFleetAsync(force: false, cancellationToken).ConfigureAwait(false);

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
        CallProvisioning calls,
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

        await calls.ReviewAsync(deviceId, force: false, cancellationToken).ConfigureAwait(false);
        await publisher.PushAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveDeviceSettingAsync(
        string deviceId,
        string key,
        ISettingsStore settings,
        SettingsPublisher publisher,
        CallProvisioning calls,
        CancellationToken cancellationToken)
    {
        var removed = await settings
            .RemoveDeviceOverrideAsync(deviceId, key, cancellationToken)
            .ConfigureAwait(false);

        if (!removed)
        {
            return NotFoundKey(key);
        }

        // Deleting an override is how an operator undoes a per-device room, and it is also how
        // somebody deletes `call.token` itself. Both leave the frame needing a token, and both
        // are answered here rather than at that frame's next call attempt.
        await calls.ReviewAsync(deviceId, force: false, cancellationToken).ConfigureAwait(false);
        await publisher.PushAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>The call server's standing (§3.7).</summary>
    private static async Task<IResult> GetLiveKitAsync(
        LiveKitService livekit,
        LiveKitDeployment deployment,
        LiveKitOptions options,
        CancellationToken cancellationToken)
    {
        var state = livekit.State;

        // The key, never the secret. Reading it costs a database round trip on the bundled path
        // and generates the pair if this is the first thing that has ever asked for it, which is
        // exactly §3.2's "generated automatically" happening at the first moment it can be seen.
        var credential = await deployment.CredentialAsync(cancellationToken).ConfigureAwait(false);

        return Results.Json(
            new LiveKitStatusResponse
            {
                Mode = state.Mode switch
                {
                    LiveKitMode.Bundled => "bundled",
                    LiveKitMode.External => "external",
                    _ => "disabled",
                },
                Version = state.Version,
                Ready = state.Ready,
                Step = state.Step,
                Problems = state.Problems,
                Url = options.EffectiveUrl,
                SignalPort = options.SignalPort,
                TcpMediaPort = options.TcpMediaPort,
                UdpPortStart = options.UdpPortStart,
                UdpPortEnd = options.UdpPortEnd,
                TokenLifetimeDays = (int)options.TokenLifetime.TotalDays,
                ReviewedUtc = livekit.Pin.ReviewedUtc,
                ApiKey = credential?.Key,
                SecretIssuedUtc = credential is { IssuedUtc.Ticks: > 0 } ? credential.IssuedUtc : null,
                Process = state.Process,
            },
            ControlJson.Default.LiveKitStatusResponse);
    }

    /// <summary>Rotates the API secret and re-mints the whole fleet (§3.7).</summary>
    /// <remarks>
    /// The revocation button. Everything signed with the old secret stops verifying the moment
    /// LiveKit reloads, which is what makes a leaked frame token a bounded problem rather than a
    /// permanent one — v1's answer to the same question was "rotate the secret and re-mint every
    /// frame's token by hand at a workstation", and this is that, performed.
    /// </remarks>
    private static async Task<IResult> RotateLiveKitAsync(
        LiveKitService livekit,
        LiveKitDeployment deployment,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var issued = await livekit.RotateAsync(cancellationToken).ConfigureAwait(false);

        if (issued is null)
        {
            return Results.Json(
                new ApiError
                {
                    Error = "not-rotatable",
                    Detail = "This Fleet Manager does not own the LiveKit API secret, so it "
                        + "cannot rotate it. Rotate it where that server is configured, then "
                        + $"update {LiveKitOptions.ExternalSecretVariable} and restart.",
                },
                ControlJson.Default.ApiError,
                statusCode: StatusCodes.Status409Conflict);
        }

        var credential = await deployment.CredentialAsync(cancellationToken).ConfigureAwait(false);

        return Results.Json(
            new LiveKitRotateResponse
            {
                Issued = issued.Value,
                ApiKey = credential?.Key,
                RotatedUtc = credential is { IssuedUtc.Ticks: > 0 } ? credential.IssuedUtc : clock.GetUtcNow(),
            },
            ControlJson.Default.LiveKitRotateResponse);
    }

    /// <summary>Mints one frame a new call token, unconditionally.</summary>
    private static async Task<IResult> IssueCallTokenAsync(
        string deviceId,
        IDeviceStore devices,
        CallProvisioning calls,
        SettingsPublisher publisher,
        CancellationToken cancellationToken)
    {
        if (await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFound(deviceId);
        }

        var result = await calls.ReviewAsync(deviceId, force: true, cancellationToken).ConfigureAwait(false);

        if (result.Outcome is CallIssueOutcome.Issued)
        {
            await publisher.PushAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }

        var response = new CallTokenResponse
        {
            DeviceId = deviceId,
            Outcome = result.Outcome switch
            {
                CallIssueOutcome.Issued => "issued",
                CallIssueOutcome.AlreadyCurrent => "already-current",
                CallIssueOutcome.NotAdopted => "not-adopted",
                _ => "not-configured",
            },
            Identity = result.Identity,
            Room = result.Room,
            ExpiresUtc = result.ExpiresUtc,
            Reason = result.Reason,
        };

        // A refusal is a 409 rather than a 200 with a sad field, so a script that checks the
        // status code is not quietly told a token exists when none does.
        return result.Outcome is CallIssueOutcome.Issued
            ? Results.Json(response, ControlJson.Default.CallTokenResponse)
            : Results.Json(
                response,
                ControlJson.Default.CallTokenResponse,
                statusCode: StatusCodes.Status409Conflict);
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
