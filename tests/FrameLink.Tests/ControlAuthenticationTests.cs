using System.Net.Http.Json;
using FrameLink.Control;
using FrameLink.Control.Authentication;

namespace FrameLink.Tests;

/// <summary>
/// The single-operator credential of §3.2, and the gate in front of the operator API.
/// </summary>
public sealed class ControlAuthenticationTests
{
    private const string GoodPassword = "a-long-operator-passphrase-for-the-fleet";

    [Fact]
    public void An_unset_variable_leaves_the_instance_unconfigured_and_says_which_one()
    {
        var credential = OperatorCredential.FromValue(null);

        // §3.2's designed state, not an error path. The problem text is what the setup page
        // and the API both render, so it has to name the variable.
        Assert.False(credential.IsConfigured);
        Assert.Contains(OperatorCredential.EnvironmentVariable, credential.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_variable_counts_as_unset()
    {
        Assert.False(OperatorCredential.FromValue("   ").IsConfigured);
        Assert.False(OperatorCredential.FromValue(string.Empty).IsConfigured);
    }

    [Fact]
    public void A_short_password_leaves_the_instance_unconfigured_and_says_why()
    {
        var credential = OperatorCredential.FromValue("short");

        // Starting with a weak credential on an internet-exposed login route would be worse
        // than starting with none, because none is visible and weak is not.
        Assert.False(credential.IsConfigured);
        Assert.Contains("at least", credential.Problem, StringComparison.Ordinal);
        Assert.False(credential.Verify("short"));
    }

    [Fact]
    public void A_configured_credential_accepts_only_the_exact_password()
    {
        var credential = OperatorCredential.FromValue(GoodPassword);

        Assert.True(credential.IsConfigured);
        Assert.Null(credential.Problem);
        Assert.True(credential.Verify(GoodPassword));
        Assert.False(credential.Verify(GoodPassword + "x"));
        Assert.False(credential.Verify(GoodPassword.ToUpperInvariant()));
        Assert.False(credential.Verify(null));
        Assert.False(credential.Verify(string.Empty));
    }

    [Fact]
    public void An_unconfigured_credential_accepts_nothing_at_all()
    {
        var credential = OperatorCredential.FromValue(null);

        // There is no "no password means open". An unconfigured server adopts nothing and
        // authenticates nobody.
        Assert.False(credential.Verify(string.Empty));
        Assert.False(credential.Verify("anything"));
        Assert.False(credential.Verify(null));
    }

    [Fact]
    public void Sessions_are_unique_unguessable_and_revocable()
    {
        var clock = new TestClock();
        var sessions = new OperatorSessions(new ControlOptions(), clock);

        var first = sessions.Create();
        var second = sessions.Create();

        Assert.NotEqual(first.Token, second.Token);
        Assert.True(first.Token.Length >= 32);
        Assert.True(sessions.IsValid(first.Token));

        sessions.Revoke(first.Token);
        Assert.False(sessions.IsValid(first.Token));
        Assert.True(sessions.IsValid(second.Token));
    }

    [Fact]
    public void A_session_stops_working_once_its_lifetime_is_up()
    {
        var clock = new TestClock();
        var sessions = new OperatorSessions(
            new ControlOptions { SessionLifetime = TimeSpan.FromHours(1) },
            clock);

        var session = sessions.Create();
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.True(sessions.IsValid(session.Token));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(sessions.IsValid(session.Token));
    }

    [Fact]
    public void An_unknown_token_is_never_valid()
    {
        var sessions = new OperatorSessions(new ControlOptions(), new TestClock());

        Assert.False(sessions.IsValid("not-a-token"));
        Assert.False(sessions.IsValid(string.Empty));
        Assert.False(sessions.IsValid(null));
    }

    [Fact]
    public async Task The_operator_api_is_closed_and_the_device_route_is_not()
    {
        await using var server = await ControlServer.StartAsync(GoodPassword);

        var devices = await server.Client.GetAsync("/api/devices", TestContext.Current.CancellationToken);
        var status = await server.Client.GetAsync("/api/status", TestContext.Current.CancellationToken);
        var release = await server.Client.GetAsync(
            "/agent/release/linux-arm64",
            TestContext.Current.CancellationToken);

        // §3.2 and §3.8: the password guards /api, and /agent is exempt because a device
        // authenticates by keypair and an SSO proxy cannot front a machine-to-machine route.
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, devices.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, status.StatusCode);
        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, release.StatusCode);
    }

    [Fact]
    public async Task The_wrong_password_does_not_open_the_operator_api()
    {
        await using var server = await ControlServer.StartAsync(GoodPassword);

        var response = await server.Client.PostAsJsonAsync(
            "/api/session",
            new LoginRequest { Password = "not-the-operator-passphrase-at-all" },
            ControlJson.Default.LoginRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_opens_the_operator_api()
    {
        await using var server = await ControlServer.StartAsync(GoodPassword);

        await server.SignInAsync(GoodPassword);
        var devices = await server.Client.GetAsync("/api/devices", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, devices.StatusCode);
    }

    [Fact]
    public async Task An_unconfigured_server_says_so_instead_of_asking_for_a_password()
    {
        await using var server = await ControlServer.StartAsync(operatorPassword: null);

        var status = await server.Client.GetAsync("/api/status", TestContext.Current.CancellationToken);
        var setup = await status.ReadAsync(ControlJson.Default.SetupStatus);

        var login = await server.Client.PostAsJsonAsync(
            "/api/session",
            new LoginRequest { Password = "anything" },
            ControlJson.Default.LoginRequest,
            TestContext.Current.CancellationToken);

        Assert.False(setup.Configured);
        Assert.Equal(OperatorCredential.EnvironmentVariable, setup.Variable);
        Assert.Contains(
            OperatorCredential.EnvironmentVariable,
            setup.ComposeExample,
            StringComparison.Ordinal);

        // Not 401. There is no password to get wrong, and telling the operator that they got
        // it wrong would send them looking in exactly the wrong place.
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, login.StatusCode);
    }

    [Fact]
    public async Task An_unconfigured_server_serves_a_page_naming_the_variable_and_a_compose_example()
    {
        await using var server = await ControlServer.StartAsync(operatorPassword: null);

        var response = await server.Client.GetAsync("/", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(OperatorCredential.EnvironmentVariable, html, StringComparison.Ordinal);
        Assert.Contains("services:", html, StringComparison.Ordinal);
        Assert.Contains("/var/lib/fl-control", html, StringComparison.Ordinal);
    }
}
