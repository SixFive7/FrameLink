using System.Net.Http.Headers;

namespace FrameLink.Control.Alerting;

/// <summary>
/// The JSON body a notification is POSTed as.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat, and every field a string or a date.</b> The receiver is a Home Assistant automation
/// template, an ntfy topic or a five-line shell script — not a client with a generated model — so
/// nesting would buy nothing and cost everybody a <c>.alert.</c> in front of every field they
/// wanted. <c>{{ trigger.json.subject }}</c> is the shape this is designed around.
/// </para>
/// <para>
/// <c>source</c> is constant and present so that a shared webhook receiving several senders can
/// tell which one this is without inspecting the other fields.
/// </para>
/// </remarks>
public sealed record AlertWebhookBody
{
    /// <summary>Constant identifier of the sender.</summary>
    public required string Source { get; init; }

    /// <summary><c>opened</c> or <c>cleared</c>.</summary>
    public required string Event { get; init; }

    /// <summary>Stable identity of the condition, so a receiver can de-duplicate too.</summary>
    public required string Key { get; init; }

    /// <summary>One of <see cref="AlertKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary><c>warning</c> or <c>critical</c>.</summary>
    public required string Severity { get; init; }

    /// <summary>One line, fit to be a notification title.</summary>
    public required string Subject { get; init; }

    /// <summary>The detail behind it, in plain sentences.</summary>
    public required string Detail { get; init; }

    /// <summary>The frame this is about, when it is about one.</summary>
    public string? DeviceId { get; init; }

    /// <summary>That frame's name, when it has one.</summary>
    public string? DeviceName { get; init; }

    /// <summary>When the condition was first observed.</summary>
    public required DateTimeOffset OpenedUtc { get; init; }

    /// <summary>Builds the body from a notification.</summary>
    public static AlertWebhookBody From(AlertNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return new AlertWebhookBody
        {
            Source = "framelink-fleet-manager",
            Event = notification.Transition is AlertTransition.Opened ? "opened" : "cleared",
            Key = notification.Alert.Key,
            Kind = notification.Alert.Kind,
            Severity = notification.Alert.Severity is AlertSeverity.Critical ? "critical" : "warning",
            Subject = notification.Alert.Subject,
            Detail = notification.Alert.Detail,
            DeviceId = notification.Alert.DeviceId,
            DeviceName = notification.Alert.DeviceName,
            OpenedUtc = notification.OpenedUtc,
        };
    }
}

/// <summary>Somewhere a notification can be delivered.</summary>
/// <remarks>
/// A seam for the same reason every other outside surface in this codebase has one: the suite has
/// to be able to drive a refusing receiver, and a refusing receiver is not reproducible against a
/// real Home Assistant on a workstation with no network.
/// </remarks>
public interface IAlertSink
{
    /// <summary>Delivers one notification.</summary>
    /// <returns>
    /// True when it was delivered. False is not an error — it means "try again", and
    /// <see cref="FleetWatch"/> leaves the row un-notified so the next pass retries it.
    /// </returns>
    Task<bool> DeliverAsync(AlertNotification notification, CancellationToken cancellationToken);
}

/// <summary>The sink for a Fleet Manager with no webhook configured.</summary>
/// <remarks>
/// Reports success, because it <i>is</i> a successful delivery: the alert reached the container
/// log, which is where an operator without Home Assistant looks. Reporting failure would leave
/// every condition permanently un-notified and retried forever on a deployment that is working
/// exactly as configured.
/// </remarks>
public sealed class LogOnlyAlertSink : IAlertSink
{
    /// <summary>The shared instance.</summary>
    public static LogOnlyAlertSink Instance { get; } = new();

    /// <inheritdoc/>
    public Task<bool> DeliverAsync(AlertNotification notification, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

/// <summary>
/// POSTs each notification as JSON to the configured URL (§3.5, decision 22).
/// </summary>
/// <remarks>
/// <para>
/// Owns its client rather than taking one from a factory, for the reason
/// <c>HttpLiveKitDownload</c> gives: <c>AddHttpClient</c> brings the whole options, logging and
/// handler-lifetime machinery into a container §3.1 keeps deliberately small, to manage one client
/// that makes a request every few days.
/// </para>
/// <para>
/// <b>The timeout is short and the failure is quiet.</b> Ten seconds, because the caller is a
/// background sweep and a hung notification channel must not be able to stall the pass that
/// evaluates the next condition. A failure is logged once and returned as false, which puts the
/// alert back in the retry set rather than throwing it away — §3.5 exists because a signal went
/// missing, so this is the one component that must not be able to lose one.
/// </para>
/// </remarks>
public sealed class WebhookAlertSink : IAlertSink, IDisposable
{
    private readonly AlertOptions _options;
    private readonly ILogger _log;
    private readonly HttpClient _client;

    /// <summary>Creates a sink pointed at <see cref="AlertOptions.WebhookUrl"/>.</summary>
    /// <param name="options">Where to POST, and with what credential.</param>
    /// <param name="logger">Where refusals are recorded.</param>
    public WebhookAlertSink(AlertOptions options, ILogger<WebhookAlertSink> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _log = logger;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("FrameLink-fleet-manager", "1.0"));

        if (_options.BearerToken.Length > 0)
        {
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();

    /// <inheritdoc/>
    public async Task<bool> DeliverAsync(AlertNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_options.WebhookUrl is not { } url)
        {
            return true;
        }

        try
        {
            // Serialised first and posted as a byte array, rather than PostAsJsonAsync.
            //
            // Not a style choice, and it was measured: PostAsJsonAsync wraps a JsonContent whose
            // length is unknown before serialisation, so HttpClient sends the body with
            // `Transfer-Encoding: chunked` and no Content-Length. Home Assistant handles that
            // fine — and the receivers this project's own documentation suggests as alternatives
            // do not. A five-line script that reads Content-Length bytes gets zero of them,
            // answers 500, and the alert is reported undeliverable for a reason nothing in either
            // log names. A few hundred bytes of JSON has a length; sending it is free.
            var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                AlertWebhookBody.From(notification),
                ControlJson.Default.AlertWebhookBody);

            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using var response = await _client
                .PostAsync(url, content, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // The URL is logged, never the token. A Home Assistant webhook URL is itself
            // unguessable-by-design rather than secret-by-classification, and an operator
            // diagnosing a 404 needs to see which address was refused.
            _log.AlertDeliveryRefused(url.ToString(), (int)response.StatusCode, notification.Alert.Key);
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _log.AlertDeliveryFailed(exception, notification.Alert.Key);
            return false;
        }
    }
}
