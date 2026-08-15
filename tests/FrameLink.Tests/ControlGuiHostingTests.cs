using System.Net;
using FrameLink.Control.Authentication;

namespace FrameLink.Tests;

/// <summary>
/// What a browser is served, in each of the three shapes an image can be in.
/// </summary>
/// <remarks>
/// <para>
/// There are exactly three: the GUI is built into the image, or it is not and the server is
/// configured, or it is not and the server is unconfigured. The fallback has to answer all
/// three, and the interesting one is the first crossed with the last — a real image, carrying
/// the Svelte build, on the day the operator has not set the password yet.
/// </para>
/// <para>
/// <b>That combination is why this file exists.</b> The fallback used to check
/// <c>IsConfigured</c> before it looked for <c>index.html</c>, so an unconfigured instance
/// served the plain C# setup page at every path — and the designed first-run screen in §3.2,
/// the first thing an operator ever sees, could not render on any image that had a GUI. No
/// error, no log line, no failing test: it just served the fallback in place of the feature.
/// </para>
/// </remarks>
public sealed class ControlGuiHostingTests : IDisposable
{
    private const string Password = "a-long-operator-passphrase-for-the-fleet";

    /// <summary>Marks the stand-in shell, so a test can tell it from the C# pages.</summary>
    private const string ShellMarker = "<!-- svelte shell -->";

    private readonly TempWorkspace _workspace = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unconfigured_server_with_a_gui_serves_the_gui_not_the_fallback_page()
    {
        await using var server = await ControlServer.StartAsync(
            operatorPassword: null,
            webRoot: WriteShell());

        var root = await server.Client.GetStringAsync("/", Token);

        // §3.2 makes the unconfigured experience a designed screen. The SPA renders it from
        // /api/status, which OperatorGate exempts precisely so it can be read with no session.
        Assert.Contains(ShellMarker, root, StringComparison.Ordinal);
        Assert.DoesNotContain("services:", root, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_client_route_resolves_to_the_shell_so_a_reload_deep_in_the_app_works()
    {
        await using var server = await ControlServer.StartAsync(Password, webRoot: WriteShell());

        var deep = await server.Client.GetAsync("/devices/K4W2-9TRB-8ZQ1-3MHF", Token);
        var setup = await server.Client.GetAsync("/setup", Token);

        // adapter-static is in SPA mode: the server knows none of these paths, and the fallback
        // handing back index.html is the entire routing contract.
        Assert.Equal(HttpStatusCode.OK, deep.StatusCode);
        Assert.Contains(ShellMarker, await deep.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Contains(ShellMarker, await setup.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_built_asset_is_served_as_itself_rather_than_swallowed_by_the_fallback()
    {
        var webRoot = WriteShell();
        Directory.CreateDirectory(Path.Combine(webRoot, "_app"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "_app", "start.js"), "export const x = 1;", Token);

        await using var server = await ControlServer.StartAsync(Password, webRoot: webRoot);
        var asset = await server.Client.GetAsync("/_app/start.js", Token);

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("export const x = 1;", await asset.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Without_a_gui_an_unconfigured_server_still_explains_itself()
    {
        await using var server = await ControlServer.StartAsync(operatorPassword: null);

        var html = await server.Client.GetStringAsync("/", Token);

        // SetupPage's real job, and the reason it is not deleted: an image built without the
        // Svelte output — or one whose GUI failed to build — must still name the variable.
        Assert.Contains(OperatorCredential.EnvironmentVariable, html, StringComparison.Ordinal);
        Assert.Contains("services:", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_gui_a_configured_server_says_it_has_no_gui_rather_than_faking_one()
    {
        await using var server = await ControlServer.StartAsync(Password);

        var html = await server.Client.GetStringAsync("/", Token);

        // §1.2 principle 3 applies to the Fleet Manager's own state too. A convincing-looking
        // stub would be worse than an honest one.
        Assert.Contains("not built into this", html, StringComparison.Ordinal);
        Assert.DoesNotContain(OperatorCredential.EnvironmentVariable, html, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public void Dispose() => _workspace.Dispose();

    private string WriteShell()
    {
        var webRoot = Path.Combine(_workspace.Root, "wwwroot");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(
            Path.Combine(webRoot, "index.html"),
            $"<!DOCTYPE html>\n<html lang=\"en\">{ShellMarker}<body></body></html>\n");

        return webRoot;
    }
}
