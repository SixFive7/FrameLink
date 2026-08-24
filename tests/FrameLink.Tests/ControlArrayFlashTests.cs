using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FrameLink.Agent.Firmware;
using FrameLink.Control;
using FrameLink.Control.Firmware;

// The pin is one file compiled into both programs (src/FrameLink.Agent/Firmware/XvfFirmwarePin.cs),
// so both namespaces above carry a type of this name. The agent's is what an unqualified use means
// here; the Fleet Manager's is named in full in the one test that checks the two compilations agree.
using XvfFirmwarePin = FrameLink.Agent.Firmware.XvfFirmwarePin;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The operator's half of decision 91: composing an authorisation for one frame, refusing to make
/// the unattended bypass cheap, and reading back what the frame did with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three kinds of test, and the split is the point.</b> The first kind holds this server's
/// record of the pin and of the frame's warnings <i>equal to the agent's own</i>, which is the only
/// thing standing between a Fleet Manager that offers a write and a fleet that refuses every one of
/// them. The second drives the real HTTP routes over a real socket. The third takes events the
/// <i>real agent</i> produced against a synthetic frame and feeds them through this server's
/// reader, so the two halves cannot drift apart in silence.
/// </para>
/// <para>
/// <b>Nothing here writes firmware, and nothing here can.</b> The agent-side fixture answers
/// <c>dfu-util</c> with a scripted process runner and a synthetic USB tree; the server-side tests
/// never touch the agent at all. What is being asserted is the authorisation, which is a string.
/// </para>
/// </remarks>
public sealed class ControlArrayFlashTests
{
    private const string Password = "a-long-operator-passphrase-for-the-fleet";
    private const string Device = "G7D8-FM0C-Y764-89BJ";
    private const string OtherDevice = "WWAR-5R1Y-K2QW-EFCV";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // -------------------------------------------------------------------------------------------
    // The pin, held twice
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_image_this_server_offers_is_the_image_the_frame_will_accept()
    {
        // This used to compare two hand-written records field by field, because the Fleet Manager
        // cannot reference FrameLink.Agent and the digest is on no wire. There is now one record:
        // src/FrameLink.Agent/Firmware/XvfFirmwarePin.cs is <Compile Include>d across the project
        // boundary, so the two programs are compiled from the same bytes rather than checked
        // against each other.
        //
        // What is left to check is not nothing, and it is the reason this test survives the
        // simplification: the two compilations are separate assemblies, and a build that linked a
        // stale copy — a half-applied edit, a dirty obj directory, a csproj whose Compile item
        // stopped resolving — would produce exactly the failure the old test guarded against, a
        // console handing out authorisations every frame answers with `NotThePinnedImage`. So the
        // agent's compilation is compared to the Fleet Manager's, by value, once.
        var agent = XvfFirmwarePin.Current.Target;
        var control = FrameLink.Control.Firmware.XvfFirmwarePin.Current.Target;

        Assert.Equal(agent.Name, control.Name);
        Assert.Equal(agent.Version, control.Version);
        Assert.Equal(agent.Sha256, control.Sha256);
        Assert.Equal(agent.SizeBytes, control.SizeBytes);
        Assert.Equal(agent.Commit, control.Commit);

        // And what the endpoints actually hand out is that same record rather than a copy of it.
        Assert.Equal(control.Sha256, ArrayFlashPin.Target.Sha256);
        Assert.Equal(control.Name, ArrayFlashPin.Target.Name);
    }

    [Fact]
    public void The_setting_this_server_writes_is_the_setting_the_frame_reads()
    {
        Assert.Equal(ArrayFirmwareFlash.AuthorisationKey, ArrayFlashPin.AuthorisationKey);
    }

    [Fact]
    public void The_bypass_token_is_the_frame_s_own_and_never_a_paraphrase_of_it()
    {
        // A single character adrift here produces a bypass the frame reads as an ordinary word in a
        // ticket: it would ask its household anyway, which is safe, and the console would say the
        // opposite, which is not.
        Assert.Equal(ArrayFirmwareFlash.UnattendedPrefix, ArrayFlashPin.UnattendedPrefix);
    }

    [Fact]
    public void The_warning_an_operator_accepts_is_the_frame_s_own_words()
    {
        // The agent emits these same sentences verbatim into the array-flash event of every
        // unattended write. If the console reworded them, the operator would have accepted one text
        // and the permanent record would claim they accepted another.
        Assert.Equal(ArrayFirmwareFlash.UnattendedWarning, ArrayFlashPin.UnattendedWarning);
        Assert.Equal(4, ArrayFlashPin.UnattendedWarning.Count);
    }

    // -------------------------------------------------------------------------------------------
    // Composition: what the operator never types
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void An_attended_authorisation_names_the_pinned_image_and_bypasses_nothing()
    {
        var value = ArrayFlashTicket.Compose(Device, unattended: false, note: null, DateTimeOffset.UtcNow);

        // Parsed by the agent's own parser, which is the reader that matters.
        var parsed = ArrayFlashAuthorisation.Parse(value);

        Assert.Equal(XvfFirmwarePin.Current.Target.Sha256, parsed.Digest);
        Assert.Null(parsed.UnattendedDeviceId);
        Assert.False(parsed.BypassesLocalApproval(Device));
        Assert.Contains(Device, parsed.Ticket, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unattended_authorisation_bypasses_the_frame_it_was_composed_for_and_no_other()
    {
        var value = ArrayFlashTicket.Compose(Device, unattended: true, note: null, DateTimeOffset.UtcNow);
        var parsed = ArrayFlashAuthorisation.Parse(value);

        Assert.Equal(Device, parsed.UnattendedDeviceId);
        Assert.True(parsed.BypassesLocalApproval(Device));

        // The property that makes the bypass safe to offer at all: pushed to any other frame — by
        // hand, as a fleet default, by mistake — it bypasses nothing there and that frame asks its
        // own household exactly as it would have.
        Assert.False(parsed.BypassesLocalApproval(OtherDevice));
        Assert.True(parsed.BypassNamesAnotherDevice(OtherDevice));
    }

    [Fact]
    public void Two_authorisations_for_one_frame_are_never_the_same_string()
    {
        // Load-bearing rather than tidy. The agent's authorisation is single-use by exact string:
        // the whole value is recorded before dfu-util starts and an equal value is refused for
        // ever. A ticket built only from stable parts would authorise one write per frame per
        // lifetime, and the second press would be answered `AlreadyConsumed` with no way out.
        var at = DateTimeOffset.UtcNow;
        var first = ArrayFlashTicket.Compose(Device, unattended: false, note: null, at);
        var second = ArrayFlashTicket.Compose(Device, unattended: false, note: null, at);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_note_may_not_smuggle_a_bypass_into_the_attended_path()
    {
        var problem = ArrayFlashTicket.NoteProblem(
            "routine " + ArrayFirmwareFlash.UnattendedPrefix + Device);

        Assert.NotNull(problem);
        Assert.Contains("unattended-write token", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_survives_into_the_ticket_and_reads_back_unchanged()
    {
        var value = ArrayFlashTicket.Compose(
            Device,
            unattended: true,
            note: "  bench   session,\n asked for by Douwe  ",
            new DateTimeOffset(2026, 8, 24, 9, 30, 0, TimeSpan.Zero));

        Assert.Equal("bench session, asked for by Douwe", ArrayFlashTicket.NoteOf(value));
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 9, 30, 0, TimeSpan.Zero), ArrayFlashTicket.IssuedAt(value));
        Assert.Equal(Device, ArrayFlashTicket.UnattendedDeviceId(value));
        Assert.True(ArrayFlashTicket.NamesTheTarget(value));
    }

    [Fact]
    public void This_server_and_the_frame_read_the_same_meaning_out_of_every_shape_of_ticket()
    {
        // Two parsers over one grammar is the drift this table exists to catch — including the two
        // subtleties the agent's own parser documents: a bare prefix with nothing after it is not a
        // bypass, and the last matching word wins.
        var digest = XvfFirmwarePin.Current.Target.Sha256;

        string[] tickets =
        [
            digest,
            digest + ":",
            digest + ":routine",
            digest + ":" + ArrayFirmwareFlash.UnattendedPrefix,
            digest + ":" + ArrayFirmwareFlash.UnattendedPrefix + Device,
            digest + ":note " + ArrayFirmwareFlash.UnattendedPrefix + OtherDevice,
            digest + ":" + ArrayFirmwareFlash.UnattendedPrefix + OtherDevice + " "
                + ArrayFirmwareFlash.UnattendedPrefix + Device,
            "not-a-digest:" + ArrayFirmwareFlash.UnattendedPrefix + Device,
            ArrayFlashTicket.Compose(Device, unattended: true, "with a note", DateTimeOffset.UtcNow),
            ArrayFlashTicket.Compose(Device, unattended: false, "with a note", DateTimeOffset.UtcNow),
        ];

        foreach (var ticket in tickets)
        {
            var agent = ArrayFlashAuthorisation.Parse(ticket);

            Assert.Equal(agent.Digest, ArrayFlashTicket.DigestOf(ticket));
            Assert.Equal(agent.Ticket, ArrayFlashTicket.TicketOf(ticket));
            Assert.Equal(agent.UnattendedDeviceId, ArrayFlashTicket.UnattendedDeviceId(ticket));
            Assert.Equal(
                agent.BypassesLocalApproval(Device),
                ArrayFlashTicket.IsUnattendedFor(ticket, Device));
        }
    }

    // -------------------------------------------------------------------------------------------
    // The routes
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Authorising_writes_the_one_setting_the_frame_already_knows_how_to_read()
    {
        // No second mechanism: the frame reads `audio.arrayFirmwareFlash` out of the settings it is
        // pushed, and that is exactly what this route writes — as a per-device override, over the
        // ordinary settings store, pushed by the ordinary publisher.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var response = await server.Client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/array-flash",
            new ArrayFlashRequest { Unattended = false, Note = "bench" },
            ControlJson.Default.ArrayFlashRequest,
            Token);

        response.EnsureSuccessStatusCode();
        var view = await response.ReadAsync(ControlJson.Default.ArrayFlashStatusResponse);

        Assert.NotNull(view.Authorisation);
        Assert.False(view.Authorisation.Unattended);
        Assert.True(view.Authorisation.NamesTheTarget);
        Assert.Equal("bench", view.Authorisation.Note);
        Assert.Equal(ArrayFlashPhases.Authorised, view.Phase);

        // And it really is the setting, resolved the way the frame resolves it.
        var effective = await server.EffectiveAsync(deviceId, ArrayFirmwareFlash.AuthorisationKey);
        Assert.Equal(view.Authorisation.Value, effective);

        // Which the frame's own parser reads as an authorisation for this frame and no other.
        var parsed = ArrayFlashAuthorisation.Parse(effective!);
        Assert.Equal(XvfFirmwarePin.Current.Target.Sha256, parsed.Digest);
        Assert.False(parsed.BypassesLocalApproval(deviceId));
    }

    [Fact]
    public async Task The_authorisation_reaches_the_frame_on_the_channel_settings_already_use()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        (await server.Client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/array-flash",
            new ArrayFlashRequest { Unattended = true, Acknowledged = true },
            ControlJson.Default.ArrayFlashRequest,
            Token)).EnsureSuccessStatusCode();

        var frames = await agent.AnswerPingsAsync(TimeSpan.FromMilliseconds(600));
        var push = frames
            .Where(frame => string.Equals(frame.Kind, ControlWire.KindSettings, StringComparison.Ordinal))
            .Select(frame => frame.PayloadAs(ProtocolJson.Default.SettingsPush))
            .LastOrDefault(payload => payload?.Values.ContainsKey(ArrayFirmwareFlash.AuthorisationKey) == true);

        Assert.NotNull(push);

        var arrived = ArrayFlashAuthorisation.Parse(push.Values[ArrayFirmwareFlash.AuthorisationKey]);
        Assert.True(arrived.BypassesLocalApproval(deviceId));
    }

    [Fact]
    public async Task The_bypass_is_refused_outright_unless_the_warnings_have_been_accepted()
    {
        // The one interlock that lives here rather than on the frame. An attended write's only
        // mitigation for mains loss is the person in the room; the bypass removes them, so the
        // acceptance is what replaces them and a request without it has skipped a choice rather
        // than made one.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var response = await server.Client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/array-flash",
            new ArrayFlashRequest { Unattended = true, Acknowledged = false },
            ControlJson.Default.ArrayFlashRequest,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.ReadAsync(ControlJson.Default.ApiError);
        Assert.Equal("not-acknowledged", error.Error);

        // The warnings are in the refusal, because a refusal that only said "not acknowledged"
        // would be a machine telling a person to tick a box they had not been shown.
        Assert.Contains("Mains loss during the write is unguardable", error.Detail, StringComparison.Ordinal);

        // And nothing was armed. A half-authorised frame is the state this refusal exists to avoid.
        Assert.Null(await server.EffectiveAsync(deviceId, ArrayFirmwareFlash.AuthorisationKey));
    }

    [Fact]
    public async Task A_note_carrying_the_bypass_token_is_refused_before_anything_is_written()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var response = await server.Client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/array-flash",
            new ArrayFlashRequest
            {
                Unattended = false,
                Note = "please " + ArrayFirmwareFlash.UnattendedPrefix + deviceId,
            },
            ControlJson.Default.ArrayFlashRequest,
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad-note", (await response.ReadAsync(ControlJson.Default.ApiError)).Error);
        Assert.Null(await server.EffectiveAsync(deviceId, ArrayFirmwareFlash.AuthorisationKey));
    }

    [Fact]
    public async Task A_frame_that_was_never_adopted_cannot_be_authorised()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();

        await using (var pending = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, pending.Result.Status);
        }

        await server.SignInAsync(Password);
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        var response = await server.Client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/array-flash",
            new ArrayFlashRequest(),
            ControlJson.Default.ArrayFlashRequest,
            Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not-adopted", (await response.ReadAsync(ControlJson.Default.ApiError)).Error);
    }

    [Fact]
    public async Task A_frame_this_server_has_never_seen_is_a_404_rather_than_an_armed_row()
    {
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        var response = await server.Client.PostAsJsonAsync(
            "/api/devices/ZZZZ-ZZZZ-ZZZZ-ZZZZ/array-flash",
            new ArrayFlashRequest(),
            ControlJson.Default.ArrayFlashRequest,
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-such-device", (await response.ReadAsync(ControlJson.Default.ApiError)).Error);
    }

    [Fact]
    public async Task A_caller_with_no_session_cannot_arm_a_firmware_write()
    {
        // Inherited from `OperatorGate`, which guards the whole of /api before routing rather than
        // route by route — so this cannot be forgotten for a new route. It is asserted by name
        // anyway, because this is the one route in the product that authorises a write to hardware
        // that rewriting the SD card does not repair, and "it inherits a guard" is a claim worth
        // one test rather than a comment.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        // A client of its own, which has never signed in. Clearing the bearer header on the
        // server's own client would not do: `SignInAsync` also takes a session cookie, and the
        // handler keeps sending it — a passing test that proved nothing.
        using var stranger = new HttpClient { BaseAddress = server.BaseAddress };

        var post = await stranger.PostAsJsonAsync(
            $"/api/devices/{deviceId}/array-flash",
            new ArrayFlashRequest { Unattended = true, Acknowledged = true },
            ControlJson.Default.ArrayFlashRequest,
            Token);

        var get = await stranger.GetAsync($"/api/devices/{deviceId}/array-flash", Token);
        var delete = await stranger.DeleteAsync($"/api/devices/{deviceId}/array-flash", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }

    [Fact]
    public async Task Withdrawing_takes_the_authorisation_back_off_the_frame()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        (await server.Client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/array-flash",
            new ArrayFlashRequest(),
            ControlJson.Default.ArrayFlashRequest,
            Token)).EnsureSuccessStatusCode();

        var response = await server.Client.DeleteAsync($"/api/devices/{deviceId}/array-flash", Token);
        response.EnsureSuccessStatusCode();

        var view = await response.ReadAsync(ControlJson.Default.ArrayFlashStatusResponse);

        Assert.Null(view.Authorisation);
        Assert.Equal(ArrayFlashPhases.NotAuthorised, view.Phase);
        Assert.Null(await server.EffectiveAsync(deviceId, ArrayFirmwareFlash.AuthorisationKey));
    }

    [Fact]
    public async Task The_status_serves_the_pin_and_the_warnings_so_the_console_never_holds_its_own_copy()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var response = await server.Client.GetAsync($"/api/devices/{deviceId}/array-flash", Token);
        response.EnsureSuccessStatusCode();
        var view = await response.ReadAsync(ControlJson.Default.ArrayFlashStatusResponse);

        Assert.Equal(XvfFirmwarePin.Current.Target.Sha256, view.Target.Sha256);
        Assert.Equal(XvfFirmwarePin.Current.Target.Name, view.Target.Name);
        Assert.Equal(ArrayFirmwareFlash.UnattendedPrefix, view.UnattendedPrefix);
        Assert.Equal(ArrayFirmwareFlash.UnattendedWarning, view.UnattendedWarning);
        Assert.Equal(ArrayFlashPhases.NotAuthorised, view.Phase);
        Assert.Empty(view.Events);
    }

    [Fact]
    public async Task A_hand_written_authorisation_naming_another_image_is_shown_as_one_the_frame_will_refuse()
    {
        // §3.4 keeps the settings mechanism generic and an operator may still write this key by
        // hand. What the console must not do is present that as an armed write when the frame is
        // going to answer `NotThePinnedImage`.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        (await server.SetDeviceSettingAsync(
            deviceId,
            ArrayFirmwareFlash.AuthorisationKey,
            new string('a', 64) + ":by hand")).EnsureSuccessStatusCode();

        var response = await server.Client.GetAsync($"/api/devices/{deviceId}/array-flash", Token);
        var view = await response.ReadAsync(ControlJson.Default.ArrayFlashStatusResponse);

        Assert.NotNull(view.Authorisation);
        Assert.False(view.Authorisation.NamesTheTarget);
        Assert.Null(view.Authorisation.IssuedUtc);
    }

    [Fact]
    public async Task The_status_carries_the_frame_s_own_firmware_events_and_leaves_the_rest_out()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        await using var agent = await server.ConnectAgentAsync(key);
        var at = DateTimeOffset.UtcNow;

        await agent.SendEventAsync(new DeviceEvent
        {
            DeviceId = deviceId,
            Kind = DeviceEventKinds.Drift,
            OccurredUtc = at,
            Resource = "identity.hostname",
            Summary = "not about firmware at all",
        });

        await agent.SendEventAsync(new DeviceEvent
        {
            DeviceId = deviceId,
            Kind = DeviceEventKinds.ArrayFirmware,
            OccurredUtc = at.AddSeconds(1),
            Summary = "The microphone unit reports USB 1-1 bcdDevice 0206 = firmware 2 0 6.",
        });

        await agent.SendEventAsync(new DeviceEvent
        {
            DeviceId = deviceId,
            Kind = DeviceEventKinds.ArrayFlash,
            OccurredUtc = at.AddSeconds(2),
            Summary = "A firmware write is authorised on this frame and is waiting for somebody standing at it.",
            Delta = ArrayFlashReading.RefusalDeltaPrefix + ArrayFlashReading.AwaitingLocalApproval + "'",
        });

        await server.WaitForEventsAsync(deviceId, events => events.Count >= 3);

        var view = await (await server.Client.GetAsync($"/api/devices/{deviceId}/array-flash", Token))
            .ReadAsync(ControlJson.Default.ArrayFlashStatusResponse);

        Assert.Equal(2, view.Events.Count);
        Assert.DoesNotContain(view.Events, moment => moment.Kind == DeviceEventKinds.Drift);
        Assert.Equal(ArrayFlashPhases.AwaitingHousehold, view.Phase);
        Assert.Equal(ArrayFlashReading.AwaitingLocalApproval, view.Refusal);
        Assert.StartsWith("The microphone unit reports", view.RunningFirmware, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Reading events the real agent really produced
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refusal_the_agent_actually_emitted_is_read_as_the_interlock_that_fired()
    {
        // The tie between the two halves. The delta is not a shape this test invented: it is what
        // `ArrayFirmwareFlash.RefuseAsync` wrote while refusing a real, fully-interlocked flash.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();

        // An authorisation naming an image this build may not write.
        fixture.Settings[ArrayFirmwareFlash.AuthorisationKey] = new string('b', 64) + ":by hand";

        await fixture.Flash().TickAsync(Token);

        var emitted = Assert.Single(fixture.Telemetry.Events);
        Assert.Equal(DeviceEventKinds.ArrayFlash, emitted.Kind);

        var reading = ArrayFlashReading.From([emitted], fixture.Settings[ArrayFirmwareFlash.AuthorisationKey]);

        Assert.Equal(ArrayFlashPhases.Refused, reading.Phase);
        Assert.Equal(nameof(ArrayFlashRefusal.NotThePinnedImage), reading.Refusal);
        Assert.Equal(emitted.Summary, reading.Detail);
    }

    [Fact]
    public async Task Waiting_on_the_household_is_its_own_phase_rather_than_a_refusal()
    {
        // The one refusal in the agent's enum that is waiting on a person in the room. It is not a
        // fault and it is not an interlock the operator can clear from here, so it reads as its own
        // phase — which is also what stops it being painted the colour of a problem.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();

        // The server's own ticket text, over the fixture's synthetic digest — so what the agent
        // parses here is the string this server composes, not a hand-written stand-in.
        // Composed on the frame's own clock, because the reader treats an event older than the
        // authorisation as history — see `An_authorisation_armed_after_an_old_write…`.
        var composed = ArrayFlashTicket.Compose(
            "TEST-DEVICE",
            unattended: false,
            note: "bench",
            fixture.Clock.UtcNow);

        var authorisation = fixture.Pin.Target.Sha256 + ":" + ArrayFlashTicket.TicketOf(composed);
        fixture.Settings[ArrayFirmwareFlash.AuthorisationKey] = authorisation;

        await fixture.Flash().TickAsync(Token);

        var emitted = Assert.Single(fixture.Telemetry.Events);
        var reading = ArrayFlashReading.From([emitted], authorisation);

        Assert.Equal(ArrayFlashPhases.AwaitingHousehold, reading.Phase);
        Assert.Equal(nameof(ArrayFlashRefusal.AwaitingLocalApproval), reading.Refusal);
        Assert.DoesNotContain(
            fixture.Processes.Commands,
            command => command.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_ticket_this_server_composes_is_a_bypass_the_agent_honours_on_that_frame()
    {
        // End to end across the seam: the server composes, the real agent parses, and the write
        // starts with nobody having been asked — on the frame the ticket names.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();

        var composed = ArrayFlashTicket.Compose(
            "TEST-DEVICE",
            unattended: true,
            note: null,
            fixture.Clock.UtcNow);

        fixture.Settings[ArrayFirmwareFlash.AuthorisationKey] =
            fixture.Pin.Target.Sha256 + ":" + ArrayFlashTicket.TicketOf(composed);

        var outcome = await fixture.Flash().TickAsync(Token);

        Assert.True(outcome.Flashed);
        Assert.True(outcome.Succeeded);

        var emitted = Assert.Single(fixture.Telemetry.Events);
        var reading = ArrayFlashReading.From([emitted], authorisation: null);

        Assert.Equal(ArrayFlashPhases.Flashed, reading.Phase);
        Assert.Null(reading.Refusal);

        // And the record says who agreed to it, in the words the operator accepted.
        Assert.Contains("Nobody at the frame was asked", emitted.Summary, StringComparison.Ordinal);
        Assert.Contains(ArrayFlashPin.UnattendedWarning[0], emitted.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_that_did_not_produce_the_pinned_firmware_reads_as_failed()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.ReEnumerate = false;
        fixture.Authorise();

        await fixture.Flash().TickAsync(Token);

        var emitted = Assert.Single(fixture.Telemetry.Events);
        var reading = ArrayFlashReading.From([emitted], authorisation: null);

        Assert.Equal(ArrayFlashPhases.Failed, reading.Phase);
        Assert.Contains("somebody has to look at the unit", reading.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_interlock_the_agent_has_survives_the_trip_to_this_console()
    {
        // Coverage over the agent's own enum rather than over a list written here, so a refusal
        // added to the frame cannot arrive as an unnamed one. The delta is built exactly as
        // `RefuseAsync` builds it — the shape of which the two tests above pin against real output.
        foreach (var name in Enum.GetNames<ArrayFlashRefusal>())
        {
            var delta = "expected 'a firmware write', observed 'refused: " + name + "'";

            Assert.Equal(name, ArrayFlashReading.RefusalIn(delta));
        }

        Assert.Null(ArrayFlashReading.RefusalIn(null));
        Assert.Null(ArrayFlashReading.RefusalIn("expected 'a firmware write', observed 'something else'"));
    }

    [Fact]
    public void An_authorisation_armed_after_an_old_write_does_not_inherit_its_outcome()
    {
        // A frame flashed last month keeps "Wrote …" as its newest array-flash event for a month.
        // Reading the phase off that alone would tell an operator their brand new authorisation had
        // already succeeded.
        var flashedAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var armedAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        DeviceEvent[] history =
        [
            new DeviceEvent
            {
                DeviceId = Device,
                Kind = DeviceEventKinds.ArrayFlash,
                OccurredUtc = flashedAt,
                Summary = "Wrote respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin to the microphone unit.",
                Delta = "dfu-util said something",
                Attempts = 1,
            },
        ];

        var stale = ArrayFlashReading.From(history, ArrayFlashTicket.Compose(Device, false, null, armedAt));
        var none = ArrayFlashReading.From(history, authorisation: null);

        Assert.Equal(ArrayFlashPhases.Authorised, stale.Phase);
        Assert.Equal(ArrayFlashPhases.Flashed, none.Phase);
    }
}
