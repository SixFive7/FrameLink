using System.Globalization;

namespace FrameLink.Control.Alerting;

/// <summary>
/// Everything §3.5's alerting needs that is not code, read from the environment.
/// </summary>
/// <remarks>
/// <para>
/// Environment variables rather than fleet settings, and the split is not arbitrary. §3.4's
/// "everything is fleet-managed" governs values the Fleet Manager <i>pushes to frames</i>; these
/// are values about the server's own behaviour, and a threshold that lived in the database could
/// be changed by the very console that a broken server cannot serve. The same reasoning already
/// puts <see cref="ControlOptions"/> and <see cref="LiveKit.LiveKitOptions"/> in the environment.
/// </para>
/// <para>
/// <b>Every value has a working default and none of them is a secret.</b> An operator who sets
/// nothing at all still gets every rule evaluated and every alert written to the container log,
/// which is the honest minimum: the log is a channel, it is just not one that reaches a phone.
/// Setting <see cref="WebhookUrl"/> is what turns it into one.
/// </para>
/// </remarks>
public sealed record AlertOptions
{
    /// <summary>Where notifications are POSTed. <c>FRAMELINK_ALERT_WEBHOOK</c>.</summary>
    public const string WebhookVariable = "FRAMELINK_ALERT_WEBHOOK";

    /// <summary>Optional bearer token for that URL. <c>FRAMELINK_ALERT_TOKEN</c>.</summary>
    public const string TokenVariable = "FRAMELINK_ALERT_TOKEN";

    /// <summary>
    /// The URL every notification is POSTed to as JSON, or null for log-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §3.5 and decision 22 route notifications through Home Assistant, "already wired here, no
    /// new credentials", and a Home Assistant <i>webhook</i> is precisely that: an unguessable URL
    /// under <c>/api/webhook/</c> that an automation triggers on, with no token to mint, store or
    /// rotate. That is why a webhook is the shape rather than the <c>notify</c> service, which
    /// would need a long-lived access token — a second credential for this deployment to own, and
    /// §3.7 has just spent a milestone reducing the number of those.
    /// </para>
    /// <para>
    /// It is nevertheless an ordinary HTTP POST of a documented JSON body, so any receiver works:
    /// ntfy, Gotify, an SMTP bridge, a shell script behind a socket. §3.5 keeps SMTP as the
    /// self-hoster's option and this is how it is reached, without an SMTP client, a From address,
    /// a TLS mode and a set of credentials living in this container.
    /// </para>
    /// </remarks>
    public Uri? WebhookUrl { get; init; }

    /// <summary>
    /// Sent as <c>Authorization: Bearer …</c> when set.
    /// </summary>
    /// <remarks>
    /// Empty by default because the Home Assistant webhook path needs no credential. It exists for
    /// receivers that do, and it is a <b>secret</b>: it belongs in the same place
    /// <c>FRAMELINK_OPERATOR_PASSWORD</c> does and never in a committed file.
    /// </remarks>
    public string BearerToken { get; init; } = string.Empty;

    /// <summary>How often the rules are evaluated.</summary>
    /// <remarks>
    /// Five minutes. Every rule reads persisted state and none of them is expensive, so the
    /// interval is chosen by how late an alert may be rather than by cost — and the conditions
    /// being watched are measured in hours and days, so a five-minute granularity is already far
    /// finer than any of them needs.
    /// </remarks>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an adopted frame may be out of contact before it is alerted on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thirty minutes, and the number is set by §2.4 rather than by taste. A frame reboots once
    /// per applied resource, so a bare provision takes the socket down some eighty times in half
    /// an hour; anything shorter than a few minutes would alert on a frame that is working
    /// perfectly and doing exactly what it was told. Thirty minutes is comfortably longer than the
    /// longest single gap a healthy frame produces and far shorter than the days the 2026-07-23
    /// incident went unnoticed for.
    /// </para>
    /// <para>
    /// Measured from <c>LastSeenUtc</c>, which is in the database. A Fleet Manager restart
    /// therefore does not reset anybody's clock, and a redeploy does not produce a burst of false
    /// alerts about a fleet that is simply reconnecting.
    /// </para>
    /// </remarks>
    public TimeSpan OfflineAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How much life a frame's call token must have left before it is alerted on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thirty days. §3.7 mints for a year and renews inside the last third, so a frame in contact
    /// is re-minted with about four months to go and can never reach this. Reaching it means
    /// renewal is <i>not arriving</i> — the frame has been out of contact for eight months, or its
    /// settings write is failing, or calling was switched off and nobody noticed.
    /// </para>
    /// <para>
    /// That is the point. This rule is not a second expiry mechanism; it is the check that the
    /// first one is working, and it is the direct descendant of the failure that shaped this
    /// project. Thirty days is chosen so that the answer is still "renew it" rather than "drive
    /// over there".
    /// </para>
    /// </remarks>
    public TimeSpan TokenExpiryWithin { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long after start-up the call server is left alone before its absence is an alert.
    /// </summary>
    /// <remarks>
    /// Fifteen minutes, and it applies to that one rule. The other three read persisted state and
    /// are correct the instant the process is up; the bundled call server is the only thing whose
    /// state legitimately starts as "not ready" — a first run fetches roughly 17 MB and writes a
    /// 50 MB executable before it can start anything. Alerting during that window would mean every
    /// first deploy pages the operator about a server that is doing what it was told.
    /// </remarks>
    public TimeSpan CallServerGrace { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether anything is delivered anywhere other than the log.</summary>
    public bool HasWebhook => WebhookUrl is not null;

    /// <summary>Builds the options from the process environment.</summary>
    public static AlertOptions FromEnvironment()
    {
        var defaults = new AlertOptions();

        return new AlertOptions
        {
            WebhookUrl = ReadUri(WebhookVariable),
            BearerToken = Read(TokenVariable) ?? string.Empty,
            Interval = ReadMinutes("FRAMELINK_ALERT_INTERVAL_MINUTES", defaults.Interval),
            OfflineAfter = ReadMinutes("FRAMELINK_ALERT_OFFLINE_MINUTES", defaults.OfflineAfter),
            TokenExpiryWithin = ReadDays("FRAMELINK_ALERT_EXPIRY_DAYS", defaults.TokenExpiryWithin),
            CallServerGrace = ReadMinutes("FRAMELINK_ALERT_CALL_GRACE_MINUTES", defaults.CallServerGrace),
        };

        static string? Read(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        // A malformed URL is null rather than an exception, for §3.2's reason: a Fleet Manager
        // that refused to start over a typo in an alerting variable would take photos, adoption
        // and calling down to complain about notifications. Problems() is where it is said.
        static Uri? ReadUri(string name) =>
            Uri.TryCreate(Read(name), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? uri
                    : null;

        static TimeSpan ReadMinutes(string name, TimeSpan fallback) =>
            int.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0
                    ? TimeSpan.FromMinutes(parsed)
                    : fallback;

        static TimeSpan ReadDays(string name, TimeSpan fallback) =>
            int.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0
                    ? TimeSpan.FromDays(parsed)
                    : fallback;
    }

    /// <summary>Everything structurally wrong with these options, in plain sentences.</summary>
    /// <remarks>
    /// Rendered on the alerts route rather than thrown, the same way <c>LiveKitOptions.Problems</c>
    /// is. An operator who set a variable and got nothing has to be able to find out why without
    /// reading a container log.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        var raw = Environment.GetEnvironmentVariable(WebhookVariable);
        if (!string.IsNullOrWhiteSpace(raw) && WebhookUrl is null)
        {
            problems.Add(
                $"{WebhookVariable} is set but is not an absolute http or https URL, so no "
                + "notification can be delivered. Alerts are still evaluated and written to this "
                + "server's log.");
        }

        if (WebhookUrl is null && problems.Count == 0)
        {
            problems.Add(
                $"{WebhookVariable} is not set, so alerts are written to this server's log and "
                + "nowhere else. Set it to a Home Assistant webhook URL to have them reach a "
                + "person.");
        }

        return problems;
    }
}
