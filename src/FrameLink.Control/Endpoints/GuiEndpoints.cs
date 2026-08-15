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
    public static void MapGuiEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/healthz", static () => Results.Text("ok"));

        // A single fallback, so every GUI route resolves to the SPA shell — or, while the
        // instance is unconfigured, to the page that explains why there is no GUI yet.
        app.MapFallback(static (HttpContext context, OperatorCredential credential) =>
        {
            if (!credential.IsConfigured)
            {
                return Results.Content(
                    SetupPage.Render(credential.Problem),
                    "text/html; charset=utf-8");
            }

            var shell = Path.Combine(
                context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath
                    ?? string.Empty,
                "index.html");

            return File.Exists(shell)
                ? Results.File(shell, "text/html; charset=utf-8")
                : Results.Content(PlaceholderShell, "text/html; charset=utf-8");
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
