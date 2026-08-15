using FrameLink.Control.Authentication;

namespace FrameLink.Control.Endpoints;

/// <summary>
/// The browser-facing routes.
/// </summary>
/// <remarks>
/// The Svelte GUI is a separate workstream (§3.1: SvelteKit with <c>adapter-static</c> in SPA
/// mode, output served from <c>wwwroot</c>). What is here is the hosting contract that
/// workstream drops its build into, plus the one page that must exist without it.
/// </remarks>
public static class GuiEndpoints
{
    /// <summary>Maps the shell, the setup page and a liveness probe.</summary>
    /// <remarks>
    /// <para>
    /// One fallback, and the order inside it is the whole design. The SPA shell wins whenever
    /// it exists — <b>including while the instance is unconfigured</b> — because §3.2 makes the
    /// unconfigured experience a designed screen rather than a failure, and the GUI is where
    /// that screen was designed. <c>/api/status</c> is exempt from the operator gate precisely
    /// so the shell can ask, with no session, whether there is a password yet.
    /// </para>
    /// <para>
    /// Getting this backwards is not a cosmetic bug and it did happen: checking
    /// <c>IsConfigured</c> first meant that on an instance carrying the built GUI, an
    /// unconfigured server served the plain C# page at every path, and the designed setup
    /// screen — the one an operator meets first, on the day they meet the product — could never
    /// render at all. Nothing failed. It just quietly served the fallback for the feature.
    /// </para>
    /// </remarks>
    public static void MapGuiEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/healthz", static () => Results.Text("ok"));

        app.MapFallback(static (HttpContext context, OperatorCredential credential) =>
        {
            // An unmatched API call is a 404 in the shape the client asked for, never the SPA
            // shell. The fallback matches every method and every path, so without this a POST
            // that misses its route — one missing `Content-Type` is enough — is answered 200
            // text/html, and the caller's JSON parser reports a syntax error in a document it
            // never asked for.
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new ApiError
                    {
                        Error = "no-such-route",
                        Detail = $"Nothing is mapped at {context.Request.Method} {context.Request.Path}.",
                    },
                    ControlJson.Default.ApiError,
                    statusCode: StatusCodes.Status404NotFound);
            }

            var shell = Path.Combine(
                context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath
                    ?? string.Empty,
                "index.html");

            if (File.Exists(shell))
            {
                return Results.File(shell, "text/html; charset=utf-8");
            }

            // No GUI in this image. SetupPage is then the only thing that can explain an
            // unconfigured server, which is what it is for and why it is not deleted.
            return credential.IsConfigured
                ? Results.Content(PlaceholderShell, "text/html; charset=utf-8")
                : Results.Content(SetupPage.Render(credential.Problem), "text/html; charset=utf-8");
        });
    }

    /// <summary>
    /// What a configured instance renders before the Svelte build exists.
    /// </summary>
    /// <remarks>
    /// Deliberately a placeholder and deliberately says so. A convincing-looking stub would be
    /// worse than an honest one: total transparency (§1.2 principle 3) applies to the Fleet
    /// Manager's own state as much as to a frame's.
    /// </remarks>
    private const string PlaceholderShell = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>FrameLink Fleet Manager</title>
        <style>
          :root { color-scheme: dark; }
          body {
            margin: 0; min-height: 100vh; display: grid; place-items: center;
            background: radial-gradient(120% 120% at 50% 0%, #16203a 0%, #0a0d14 60%);
            color: #e8ecf5; font: 16px/1.6 ui-sans-serif, system-ui, "Segoe UI", sans-serif;
            padding: 2rem; text-align: center;
          }
          h1 { font-size: 1.6rem; margin: 0 0 .5rem; letter-spacing: -.02em; }
          p { color: #97a3bb; margin: 0 0 1.4rem; }
          code { color: #8fd3ff; background: rgba(143,211,255,.1);
                 padding: .1rem .4rem; border-radius: 5px; }
        </style>
        </head>
        <body>
        <div>
          <h1>FrameLink Fleet Manager</h1>
          <p>The server is configured and running. The web interface is not built into this
             image yet.</p>
          <p>Devices connect at <code>/agent</code>. The operator API is under
             <code>/api</code>.</p>
        </div>
        </body>
        </html>
        """;
}
