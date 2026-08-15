using System.Net;
using System.Net.Http.Json;
using FrameLink.Control;
using FrameLink.Control.Endpoints;
using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The operator API as the console actually calls it.
/// </summary>
/// <remarks>
/// Every test here reproduces something the GUI workstream hit while building against this
/// server. They are grouped rather than scattered because they share one cause: the API was
/// written against an imagined client, and each of these is a place where the real one asked
/// for something reasonable and was refused, or was handed a row it could not read.
/// </remarks>
public sealed class ControlOperatorApiTests
{
    private const string Password = "a-long-operator-passphrase-for-the-fleet";
    private const string Device = "AAAA-AAAA-AAAA-AAAA";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_device_can_be_read_on_its_own_without_scanning_the_fleet()
    {
        // Without this route the detail screen had to find its row in the polled list, so a hard
        // page load showed a placeholder until the next poll — and a blocked device's own page
        // only worked because the list was fetched with includeBlocked=true whatever the toggle
        // said.
        using var fixture = new StorageFixture();
        var registry = new FrameLink.Control.Agent.AgentConnectionRegistry();
        await fixture.SeeDeviceAsync(Device);

        var record = await fixture.Devices.FindAsync(Device, Token);
        var view = OperatorEndpoints.ToView(record!, registry);

        Assert.Equal(Device, view.DeviceId);
        Assert.Equal("pending", view.State);
    }

    [Fact]
    public async Task The_single_device_route_answers_for_every_state_and_404s_for_none()
    {
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);
        using var key = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        await using (var agent = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, agent.Result.Status);
        }

        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        var pending = await server.Client.GetAsync($"/api/devices/{deviceId}", Token);
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal(deviceId, (await pending.ReadAsync(ControlJson.Default.DeviceView)).DeviceId);

        var blocked = await server.Client.PostAsync($"/api/devices/{deviceId}/block", null, Token);
        blocked.EnsureSuccessStatusCode();

        // The important half: a blocked device's own page is reachable. It is filtered from the
        // list by default, which is a list concern and not an existence one.
        var afterBlock = await server.Client.GetAsync($"/api/devices/{deviceId}", Token);
        Assert.Equal(HttpStatusCode.OK, afterBlock.StatusCode);
        Assert.Equal("blocked", (await afterBlock.ReadAsync(ControlJson.Default.DeviceView)).State);

        var ghost = await server.Client.GetAsync("/api/devices/ZZZZ-ZZZZ-ZZZZ-ZZZZ", Token);
        Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
    }

    [Fact]
    public async Task Adopting_with_nothing_at_all_is_the_normal_case_and_is_not_a_400()
    {
        // Adoption used to take `AdoptRequest`, a non-nullable minimal-API parameter, which
        // makes the body *required* — so adopting a frame without naming it, which is what
        // adopting a frame usually is, was a framework 400. Making it nullable was not enough:
        // a POST with no `Content-Type` header is not routed to a body-bound endpoint at all,
        // so it fell through to the SPA fallback and came back as 200 text/html.
        //
        // The name is one optional scalar, so it is now a query parameter and there is no body
        // to negotiate — which also makes /adopt consistent with /block and /unblock, its two
        // siblings, and makes `curl -X POST .../adopt` do the obvious thing.
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);
        using var key = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        await using (await server.ConnectAgentAsync(key))
        {
        }

        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());
        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/adopt", null, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.ReadAsync(ControlJson.Default.DeviceView);
        Assert.Equal("adopted", view.State);
        Assert.Null(view.Name);
    }

    [Fact]
    public async Task A_blocked_device_cannot_be_adopted_in_one_press()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync(Device);
        await fixture.Devices.BlockAsync(Device, Token);

        var adoption = await fixture.Devices.AdoptAsync(Device, "Kitchen", Token);
        var stillBlocked = await fixture.Devices.FindAsync(Device, Token);

        // §3.3: unblocking returns a device to the adoption queue, and re-trusting it is a
        // second, deliberate press. `TransitionAsync` used to set state='adopted'
        // unconditionally, so POST /adopt walked straight past the rule UnblockAsync exists to
        // enforce. The GUI never offered the button; the route offered it to anything.
        Assert.Equal(DeviceAdoptionResult.Blocked, adoption.Result);
        Assert.Null(adoption.Record);
        Assert.Equal(DeviceState.Blocked, stillBlocked!.State);
        Assert.Null(stillBlocked.DisplayName);
    }

    [Fact]
    public async Task The_two_press_path_out_of_blocked_still_works()
    {
        using var fixture = new StorageFixture();
        await fixture.SeeDeviceAsync(Device);
        await fixture.Devices.BlockAsync(Device, Token);

        await fixture.Devices.ReturnToPendingAsync(Device, Token);
        var adoption = await fixture.Devices.AdoptAsync(Device, "Kitchen", Token);

        Assert.Equal(DeviceAdoptionResult.Adopted, adoption.Result);
        Assert.Equal(DeviceState.Adopted, adoption.Record!.State);
        Assert.Equal("Kitchen", adoption.Record.DisplayName);
    }

    [Fact]
    public async Task Renaming_is_still_an_adopt_and_does_not_move_the_adoption_date()
    {
        // Adoption doubles as the rename route, so the blocked guard must not catch an
        // already-adopted device — and the timestamp must not move under it, or a rename would
        // briefly make last-seen older than state-changed, which is how §3.5 reads "adopted,
        // never checked in since".
        var clock = new TestClock();
        using var fixture = new StorageFixture(clock);
        await fixture.SeeDeviceAsync(Device);

        var first = await fixture.Devices.AdoptAsync(Device, "Kitchen", Token);
        clock.Advance(TimeSpan.FromDays(3));
        var renamed = await fixture.Devices.AdoptAsync(Device, "Keuken", Token);

        Assert.Equal(DeviceAdoptionResult.Adopted, renamed.Result);
        Assert.Equal("Keuken", renamed.Record!.DisplayName);
        Assert.Equal(first.Record!.StateChangedUtc, renamed.Record.StateChangedUtc);
    }

    [Fact]
    public async Task A_blocked_adopt_is_refused_with_a_sentence_and_not_a_404()
    {
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);
        using var key = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        await using (await server.ConnectAgentAsync(key))
        {
        }

        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());
        (await server.Client.PostAsync($"/api/devices/{deviceId}/block", null, Token))
            .EnsureSuccessStatusCode();

        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/adopt", null, Token);
        var error = await response.ReadAsync(ControlJson.Default.ApiError);

        // "No such device" would be a lie about a row that is right there, and the operator
        // needs told which button to press instead.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("blocked", error.Error);
        Assert.Contains("Unblock", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_row_carries_when_its_state_changed_and_where_it_called_from()
    {
        // Both are on DeviceRecord and neither reached the browser. StateChangedUtc is what
        // makes §3.5's `Never enrolled` rung derivable at all — adopted, and not seen since —
        // and it doubles as "blocked since" and "adopted on".
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);
        using var key = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        await using (await server.ConnectAgentAsync(key, agentStatus: "InSync"))
        {
        }

        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());
        var response = await server.Client.GetAsync($"/api/devices/{deviceId}", Token);
        var view = await response.ReadAsync(ControlJson.Default.DeviceView);

        Assert.NotEqual(default, view.StateChangedUtc);
        Assert.Equal("127.0.0.1", view.LastRemoteAddress);
    }

    [Theory]

    // The vocabulary of §2.3, in the shape §2.5 specifies for the one term that carries detail.
    [InlineData("InSync", AgentHealth.InSync)]
    [InlineData("Progressing(display.overlay)", AgentHealth.Working)]
    [InlineData("AwaitingReboot(audio.volume)", AgentHealth.Working)]
    [InlineData("Degraded(audio.volume, expected 75 observed 40, attempt 3)", AgentHealth.Degraded)]
    [InlineData("Blocked(kiosk.stack)", AgentHealth.Degraded)]
    [InlineData("Escalated(firmware)", AgentHealth.Degraded)]
    [InlineData("Halted(firmware)", AgentHealth.Halted)]
    [InlineData("degraded", AgentHealth.Degraded)]
    [InlineData(null, AgentHealth.Unknown)]
    [InlineData("", AgentHealth.Unknown)]

    // The case that made this necessary: a real agent's self-report is prose, and the browser
    // was matching it against the vocabulary, so every healthy frame in the fleet rendered as
    // "Online — degraded". Unrecognised is unknown, and unknown is not a problem.
    [InlineData("linux-arm64, endpoints resolved by boot file", AgentHealth.Unknown)]
    public void Health_is_classified_by_the_server_from_the_vocabulary_both_programs_share(
        string? agentStatus,
        string expected) =>
        Assert.Equal(expected, AgentHealth.Classify(agentStatus));

    [Fact]
    public void The_agents_own_self_report_classifies_as_something_the_ladder_can_use()
    {
        // The other half of "coordinate both sides": the agent writes its status through the
        // same vocabulary the server reads it with, so the classification is a fact about a
        // shared contract rather than a lucky match on a sentence.
        var text = AgentHealth.Describe(AgentResourceStatus.Degraded, "audio.volume, attempt 3");

        Assert.Equal("Degraded(audio.volume, attempt 3)", text);
        Assert.Equal(AgentHealth.Degraded, AgentHealth.Classify(text));
        Assert.Equal(AgentResourceStatus.InSync, AgentHealth.Describe(AgentResourceStatus.InSync, null));
    }

    [Theory]
    [InlineData(true, "203.0.113.7", true)]
    [InlineData(true, "127.0.0.1", true)]

    // The case §3.8 produces and the old code got wrong: TLS terminated at Traefik, so the
    // request reaching Kestrel is plain HTTP from the proxy's address. IsHttps is false there,
    // and false again whenever FRAMELINK_TRUSTED_PROXIES was forgotten — which is precisely when
    // nothing else would have noticed.
    [InlineData(false, "172.16.14.1", true)]
    [InlineData(false, "203.0.113.7", true)]
    [InlineData(false, null, true)]

    // The only opt-out: a developer running fl-control directly on plain HTTP.
    [InlineData(false, "127.0.0.1", false)]
    [InlineData(false, "::1", false)]
    public void The_session_cookie_is_secure_unless_a_developer_is_plainly_on_loopback(
        bool isHttps,
        string? remoteAddress,
        bool expected) =>
        Assert.Equal(
            expected,
            OperatorEndpoints.ShouldSecureCookie(
                isHttps,
                remoteAddress is null ? null : IPAddress.Parse(remoteAddress)));

    [Fact]
    public async Task A_developer_on_loopback_still_gets_a_usable_session_cookie()
    {
        await using var server = await ControlServer.StartAsync(Password);

        var response = await server.Client.PostAsJsonAsync(
            "/api/session",
            new LoginRequest { Password = Password },
            ControlJson.Default.LoginRequest,
            Token);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        // A `Secure` cookie over http:// is refused by some browsers, so the one place the flag
        // is dropped is the one place nobody is exposed by dropping it.
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }
}
