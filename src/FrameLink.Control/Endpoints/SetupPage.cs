using System.Net;
using FrameLink.Control.Authentication;

namespace FrameLink.Control.Endpoints;

/// <summary>
/// The page an unconfigured Fleet Manager shows instead of the GUI (§3.2).
/// </summary>
/// <remarks>
/// <para>
/// Server-rendered rather than part of the Svelte app, and that is deliberate: this page has
/// to work when nothing else does. It carries no build step and no dependency on
/// <c>wwwroot</c> having been populated — because the operator seeing it has a container that
/// started, a browser pointed at it, and no idea why nothing works.
/// </para>
/// <para>
/// It names the variable verbatim and gives a Compose fragment to paste. §3.2 is explicit
/// that an unconfigured instance explains itself rather than failing silently, and "explains
/// itself" means the reader can fix it without leaving the page.
/// </para>
/// </remarks>
public static class SetupPage
{
    /// <summary>A Compose fragment that configures the instance, ready to copy.</summary>
    public const string ComposeExample = """
        services:
          fl-control:
            image: framelink/fl-control:latest
            environment:
              FRAMELINK_OPERATOR_PASSWORD: "choose-a-long-passphrase-at-least-24-characters"
            volumes:
              - ./framelink-data:/var/lib/fl-control
            ports:
              - "8080:8080"
            restart: unless-stopped
        """;

    // Token substitution rather than an interpolated raw string: the page is mostly CSS and
    // the brace-doubling an interpolated literal would demand makes a stylesheet unreadable
    // and unmaintainable for no gain.
    private const string Template = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>FrameLink Fleet Manager — not set up yet</title>
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; }
          body {
            margin: 0; min-height: 100vh; display: grid; place-items: center;
            background: radial-gradient(120% 120% at 50% 0%, #1b2233 0%, #0b0e15 60%);
            color: #e8ecf5; padding: 2rem;
            font: 16px/1.6 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
          }
          main {
            max-width: 46rem; width: 100%;
            background: rgba(255,255,255,.04); border: 1px solid rgba(255,255,255,.09);
            border-radius: 18px; padding: 2.5rem;
            box-shadow: 0 24px 70px rgba(0,0,0,.5);
            animation: rise .5s cubic-bezier(.22,1,.36,1) both;
          }
          @keyframes rise { from { opacity: 0; transform: translateY(14px); } }
          @media (prefers-reduced-motion: reduce) { main { animation: none; } }
          .tag {
            display: inline-block; font-size: .72rem; letter-spacing: .14em;
            text-transform: uppercase; color: #ffc86b;
            border: 1px solid rgba(255,200,107,.35); border-radius: 999px;
            padding: .3rem .8rem; margin-bottom: 1.4rem;
          }
          h1 { margin: 0 0 .6rem; font-size: 1.85rem; letter-spacing: -.02em; }
          p { margin: 0 0 1.1rem; color: #b9c2d6; }
          code.var {
            color: #8fd3ff; background: rgba(143,211,255,.1);
            padding: .1rem .4rem; border-radius: 5px; font-size: .95em;
          }
          .block { position: relative; margin-top: 1.6rem; }
          pre {
            margin: 0; padding: 1.1rem 1.2rem; overflow-x: auto;
            background: #060911; border: 1px solid rgba(255,255,255,.08);
            border-radius: 12px; font-size: .85rem; line-height: 1.55; color: #cfe0ff;
          }
          button {
            position: absolute; top: .7rem; right: .7rem;
            background: rgba(255,255,255,.08); color: #e8ecf5; cursor: pointer;
            border: 1px solid rgba(255,255,255,.16); border-radius: 8px;
            padding: .35rem .75rem; font-size: .78rem;
            transition: background .18s ease, transform .18s ease;
          }
          button:hover { background: rgba(255,255,255,.16); transform: translateY(-1px); }
          footer { margin-top: 1.8rem; font-size: .85rem; color: #78839b; }
        </style>
        </head>
        <body>
        <main>
          <span class="tag">Not set up yet</span>
          <h1>This Fleet Manager has no operator password</h1>
          <p>__EXPLANATION__</p>
          <p>
            Until it is set, no frame can be adopted. Any frame already pointed at this address
            is being told the server is not configured, and is showing &ldquo;connected to a
            Fleet Manager, but it is not set up yet&rdquo; on its screen.
          </p>
          <p>Set <code class="var">__VARIABLE__</code> and restart the container:</p>
          <div class="block">
            <button type="button" id="copy">Copy</button>
            <pre id="compose">__COMPOSE__</pre>
          </div>
          <footer>Choose a long passphrase. This server is reachable from the internet, and it
          is the only credential there is.</footer>
        </main>
        <script>
        document.getElementById('copy').addEventListener('click', async (e) => {
          await navigator.clipboard.writeText(document.getElementById('compose').textContent);
          e.target.textContent = 'Copied';
          setTimeout(() => { e.target.textContent = 'Copy'; }, 1600);
        });
        </script>
        </body>
        </html>
        """;

    /// <summary>Renders the page for a given configuration problem.</summary>
    public static string Render(string? problem)
    {
        var explanation = problem
            ?? $"The environment variable {OperatorCredential.EnvironmentVariable} is not set.";

        return Template
            .Replace("__EXPLANATION__", WebUtility.HtmlEncode(explanation), StringComparison.Ordinal)
            .Replace("__VARIABLE__", WebUtility.HtmlEncode(OperatorCredential.EnvironmentVariable), StringComparison.Ordinal)
            .Replace("__COMPOSE__", WebUtility.HtmlEncode(ComposeExample), StringComparison.Ordinal);
    }
}
