using System.Text.Json;

namespace FrameLink.Control.Authentication;

/// <summary>
/// The password gate in front of the operator API.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>/agent</c> is exempt</b>, and that exemption is structural rather than a special
/// case bolted on: §3.8 records that Authelia cannot sit in front of a machine-to-machine
/// route, so the device path authenticates by keypair instead and must never meet a password
/// prompt. The gate is expressed as "guard <c>/api</c>" rather than "allow <c>/agent</c>",
/// so a new device route inherits the exemption and a new operator route inherits the guard.
/// </para>
/// <para>
/// The GUI shell and its static assets are also ungated. They contain no fleet data — every
/// byte the operator actually cares about arrives through <c>/api</c>, and an unconfigured
/// instance has to be able to render its own setup page to somebody who by definition has no
/// password yet (§3.2).
/// </para>
/// </remarks>
public static class OperatorGate
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>True when the request must carry a valid operator session.</summary>
    public static bool RequiresOperator(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Two holes, both unavoidable: the login route cannot require a session to create
        // one, and the setup status is what tells an operator with no password why they have
        // no password.
        if (request.Path.StartsWithSegments("/api/status", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !(request.Path.StartsWithSegments("/api/session", StringComparison.OrdinalIgnoreCase)
            && HttpMethods.IsPost(request.Method));
    }

    /// <summary>Pulls the session token from the cookie or the Authorization header.</summary>
    /// <remarks>
    /// The cookie is what a browser sends; the bearer header is what a script or a test sends.
    /// Both name the same in-memory session, so there is one thing to revoke.
    /// </remarks>
    public static string? ReadToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Cookies.TryGetValue(OperatorSessions.CookieName, out var cookie)
            && !string.IsNullOrEmpty(cookie))
        {
            return cookie;
        }

        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;
    }

    /// <summary>Installs the gate. Runs before endpoint routing, so nothing can slip past it.</summary>
    public static void UseOperatorGate(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(static async (context, next) =>
        {
            if (!RequiresOperator(context.Request))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var sessions = context.RequestServices.GetRequiredService<OperatorSessions>();
            if (sessions.IsValid(ReadToken(context.Request)))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var credential = context.RequestServices.GetRequiredService<OperatorCredential>();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";

            await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    new ApiError
                    {
                        Error = credential.IsConfigured ? "unauthorized" : "not-configured",
                        Detail = credential.Problem ?? "Sign in with the operator password.",
                    },
                    ControlJson.Default.ApiError,
                    context.RequestAborted)
                .ConfigureAwait(false);
        });
    }
}
