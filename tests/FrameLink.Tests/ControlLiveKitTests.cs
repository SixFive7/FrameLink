using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FrameLink.Agent.Resources;
using FrameLink.Control;
using FrameLink.Control.LiveKit;
using FrameLink.Control.Storage;
using FrameLink.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameLink.Tests;

/// <summary>
/// The call token itself: how it is built, and when it is replaced (§3.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape of the token is asserted against a recorded live result, not against itself.</b>
/// A minter tested only by its own reader passes with any claim names at all, including wrong
/// ones — so the values these tests pin are the ones a real <c>livekit-server</c> 1.13.5 accepted
/// when a token from this exact code was offered to its <c>/rtc/validate</c> endpoint, which
/// answered <c>200 success</c>. An expired token and one signed with a rotated secret were both
/// answered <c>401</c> by the same server, which is what makes rotation a revocation rather than
/// a hope.
/// </para>
/// <para>
/// That live check cannot run in the suite: it needs a 53 MB Linux binary and a container. What
/// runs here is everything downstream of it — that the claim names have not been changed since,
/// that the grant stays narrow, and that the renewal policy fires when it should.
/// </para>
/// </remarks>
public sealed class LiveKitTokenTests
{
    private static readonly LiveKitCredential Credential = new(
        "APItestkey123",
        "a-secret-at-least-thirty-two-characters",
        new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero));

    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_minted_token_carries_the_claims_livekit_verifies()
    {
        var token = LiveKitToken.Mint(Credential, "FL7Q2K9XMNP4RTVW", "family", "Douwe", Now, TimeSpan.FromDays(365));

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);

        var header = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]));
        var body = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1]));

        // HS256 is the only algorithm LiveKit accepts, and the claim names are its Go structs'
        // JSON tags. Every string here was read back out of a token the real server said yes to.
        Assert.Equal("""{"alg":"HS256","typ":"JWT"}""", header);
        Assert.Contains("\"iss\":\"APItestkey123\"", body, StringComparison.Ordinal);
        Assert.Contains("\"sub\":\"FL7Q2K9XMNP4RTVW\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Douwe\"", body, StringComparison.Ordinal);
        Assert.Contains("\"video\":{\"roomJoin\":true,\"room\":\"family\"", body, StringComparison.Ordinal);
        Assert.Contains("\"canPublish\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"canSubscribe\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"canPublishData\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_grant_is_only_ever_join_publish_and_subscribe()
    {
        // A leaked frame token joins one room and can do nothing to the server. Asserted as an
        // absence, because the failure mode is somebody adding a convenience claim years from now
        // and nobody noticing that every frame in every household became a room administrator.
        var body = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(
            LiveKitToken.Mint(Credential, "id", "family", null, Now, TimeSpan.FromDays(1)).Split('.')[1]));

        foreach (var forbidden in (string[])["roomCreate", "roomList", "roomAdmin", "roomRecord", "ingressAdmin", "recorder"])
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_signature_is_hmac_sha256_over_the_two_encoded_segments()
    {
        var token = LiveKitToken.Mint(Credential, "id", "family", null, Now, TimeSpan.FromDays(1));
        var parts = token.Split('.');

        var expected = System.Buffers.Text.Base64Url.EncodeToString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Credential.Secret),
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1])));

        Assert.Equal(expected, parts[2]);
    }

    [Fact]
    public void A_token_is_backdated_a_minute_so_a_clock_skew_is_not_a_dead_frame()
    {
        // A not-yet-valid token is refused exactly as an expired one is, with the extra cruelty
        // that it starts working later — the hardest possible fault to diagnose from a frame on
        // somebody's wall.
        var body = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(
            LiveKitToken.Mint(Credential, "id", "family", null, Now, TimeSpan.FromDays(1)).Split('.')[1]));

        Assert.Contains(
            $"\"nbf\":{(Now - TimeSpan.FromMinutes(1)).ToUnixTimeSeconds()}",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Inspecting_a_token_reads_back_what_was_minted()
    {
        var token = LiveKitToken.Mint(Credential, "FL7Q2K9XMNP4RTVW", "kitchen", " Douwe ", Now, TimeSpan.FromDays(30));
        var facts = LiveKitToken.Inspect(token);

        Assert.NotNull(facts);
        Assert.Equal("APItestkey123", facts.Issuer);
        Assert.Equal("FL7Q2K9XMNP4RTVW", facts.Identity);
        Assert.Equal("kitchen", facts.Room);

        // Trimmed, so that a name with a stray space does not re-mint on every review forever.
        Assert.Equal("Douwe", facts.Name);
        Assert.Equal((Now + TimeSpan.FromDays(30)).ToUnixTimeSeconds(), facts.Expires!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void A_blank_name_and_no_name_are_the_same_token()
    {
        var blank = LiveKitToken.Mint(Credential, "id", "family", "   ", Now, TimeSpan.FromDays(1));
        var absent = LiveKitToken.Mint(Credential, "id", "family", null, Now, TimeSpan.FromDays(1));

        Assert.Equal(absent, blank);
        Assert.Null(LiveKitToken.Inspect(blank)!.Name);
    }

    [Fact]
    public void Anything_that_is_not_a_token_is_read_as_nothing_rather_than_thrown()
    {
        // The reader is fed whatever is in the settings store, which an operator can edit.
        Assert.Null(LiveKitToken.Inspect(null));
        Assert.Null(LiveKitToken.Inspect(string.Empty));
        Assert.Null(LiveKitToken.Inspect("not-a-token"));
        Assert.Null(LiveKitToken.Inspect("a.b.c"));
        Assert.Null(LiveKitToken.Inspect("x." + System.Buffers.Text.Base64Url.EncodeToString("[]"u8) + ".y"));
    }

    [Fact]
    public void Renewal_fires_inside_the_last_third_and_not_before()
    {
        var threshold = TimeSpan.FromDays(365) / 3;
        var token = LiveKitToken.Mint(Credential, "id", "family", null, Now, TimeSpan.FromDays(365));

        Assert.False(LiveKitToken.NeedsRenewal(token, Now, threshold));
        Assert.False(LiveKitToken.NeedsRenewal(token, Now + TimeSpan.FromDays(200), threshold));
        Assert.True(LiveKitToken.NeedsRenewal(token, Now + TimeSpan.FromDays(250), threshold));
        Assert.True(LiveKitToken.NeedsRenewal(token, Now + TimeSpan.FromDays(400), threshold));
    }

    [Fact]
    public void An_absent_or_unreadable_token_always_needs_renewing()
    {
        // Being wrong in this direction costs one signature. Being wrong in the other direction
        // is the July-23 incident.
        var threshold = TimeSpan.FromDays(120);

        Assert.True(LiveKitToken.NeedsRenewal(null, Now, threshold));
        Assert.True(LiveKitToken.NeedsRenewal(string.Empty, Now, threshold));
        Assert.True(LiveKitToken.NeedsRenewal("nonsense", Now, threshold));
    }

    [Fact]
    public void The_fleet_manager_and_the_agent_agree_on_the_default_room()
    {
        // The token's `video.room` claim and the room the frame actually joins are produced by
        // two separate programs from two separate constants, and a disagreement is a frame that
        // is refused at the door with a token that looks perfectly valid.
        var agentFallback = AppConfigCatalog.Specs
            .Single(spec => string.Equals(spec.SettingKey, CallProvisioning.RoomKey, StringComparison.Ordinal))
            .Fallback;

        Assert.Equal(CallProvisioning.DefaultRoom, agentFallback);
    }

    [Fact]
    public void The_agent_consumes_exactly_the_four_setting_keys_the_fleet_manager_issues()
    {
        // The other half of the same seam. The Fleet Manager writes four keys and the agent's
        // app.config.* resources read them; a rename on either side is a frame that silently
        // never receives a value it is waiting for.
        var agentKeys = AppConfigCatalog.Specs.Select(spec => spec.SettingKey).ToList();

        Assert.Contains(CallProvisioning.IdentityKey, agentKeys, StringComparer.Ordinal);
        Assert.Contains(CallProvisioning.RoomKey, agentKeys, StringComparer.Ordinal);
        Assert.Contains(CallProvisioning.UrlKey, agentKeys, StringComparer.Ordinal);
        Assert.Contains(CallProvisioning.TokenKey, agentKeys, StringComparer.Ordinal);

        // And the one the frame keeps root-only is the token, never the URL or the identity.
        Assert.True(AppConfigCatalog.Specs
            .Single(spec => string.Equals(spec.SettingKey, CallProvisioning.TokenKey, StringComparison.Ordinal))
            .Secret);
    }
}

/// <summary>The generated <c>livekit.yaml</c> and the credential behind it (§3.2, §3.7).</summary>
public sealed class LiveKitConfigurationTests
{
    private static LiveKitOptions Options(string directory) => new()
    {
        Directory = directory,
        Mode = LiveKitMode.Bundled,
        PublicUrl = "ws://frames.invalid:7880",
    };

    [Fact]
    public void The_rendered_configuration_is_the_document_livekit_accepted()
    {
        // Byte-for-byte the file that livekit-server 1.13.5 was started with, which answered
        // `ports` with 7880 HTTP, 7881 ICE/TCP and 50000-50059 ICE/UDP. LiveKit parses its
        // configuration with unknown fields treated as errors — a file with one extra key is
        // refused outright and the server does not start — so this is not a formatting
        // preference, it is the contract.
        var rendered = LiveKitConfigFile.Render(
            Options("/tmp/livekit"),
            new LiveKitCredential("APIkey", "secret-value", DateTimeOffset.UnixEpoch));

        Assert.Contains("port: 7880\n", rendered, StringComparison.Ordinal);
        Assert.Contains("bind_addresses:\n  - 0.0.0.0\n", rendered, StringComparison.Ordinal);
        Assert.Contains("  tcp_port: 7881\n", rendered, StringComparison.Ordinal);
        Assert.Contains("  port_range_start: 50000\n", rendered, StringComparison.Ordinal);
        Assert.Contains("  port_range_end: 50059\n", rendered, StringComparison.Ordinal);
        Assert.Contains("  APIkey: secret-value\n", rendered, StringComparison.Ordinal);
        Assert.Contains("  auto_create: true\n", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lan_setting_is_written_and_a_name_produces_no_advertised_address()
    {
        // §3.7: "Set use_external_ip: false for LAN" — external-IP discovery asks the internet
        // what this host's public address is, which is the wrong answer for peers on the same
        // network and a third party in the path of a call that never leaves the house. The
        // absence of node_ip here is the other half: this options set names a host rather than an
        // address, and an address the call server would advertise cannot be invented from a name
        // without resolving it. TURN/TLS stays deferred within v2 and is asserted as an absence,
        // because half of it is worse than none.
        var rendered = LiveKitConfigFile.Render(
            Options("/tmp/livekit"),
            new LiveKitCredential("APIkey", "secret", DateTimeOffset.UnixEpoch));

        Assert.Contains("  use_external_ip: false\n", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("node_ip", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("turn", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_address_frames_dial_is_the_address_media_is_advertised_on()
    {
        // The fault this closes is a container publishing its ports one-to-one onto a LAN host:
        // signalling arrives on the published address and works, while the ICE candidates carry
        // the container's own bridge address and the call connects and carries nothing.
        // `node_ip` is the key that says otherwise in livekit-server 1.13.5 — its own
        // config-sample says "use_external_ip takes precedence, for this to take effect, set
        // use_external_ip to false", and the pinned binary carries the yaml tag `node_ip,omitempty`
        // — so both lines are asserted together, in that order of dependence.
        var options = Options("/tmp/livekit") with { PublicUrl = "ws://10.20.30.200:7880" };

        var rendered = LiveKitConfigFile.Render(
            options,
            new LiveKitCredential("APIkey", "secret", DateTimeOffset.UnixEpoch));

        Assert.Contains("  use_external_ip: false\n  node_ip: 10.20.30.200\n", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:7880")]
    [InlineData("ws://0.0.0.0:7880")]
    [InlineData("wss://framelink.huisman.io")]
    [InlineData("")]
    [InlineData("not a url")]
    public void An_address_that_would_advertise_nothing_reachable_is_left_out(string publicUrl)
    {
        // A bad node_ip is not a bad line in a file — LiveKit refuses a configuration it cannot
        // parse and the call server does not start, so the choice is between an address that can
        // be advertised and no line at all. Loopback advertises a frame back to itself, the
        // unspecified address advertises nothing, and a name is not an address this container may
        // resolve on a status read. Each leaves the server on the addresses it is locally on,
        // which is where it was before.
        var options = Options("/tmp/livekit") with { PublicUrl = publicUrl };

        var rendered = LiveKitConfigFile.Render(
            options,
            new LiveKitCredential("APIkey", "secret", DateTimeOffset.UnixEpoch));

        Assert.DoesNotContain("node_ip", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operators_own_livekit_is_never_told_which_address_to_advertise()
    {
        // The escape hatch of §3.7 points at a server on somebody else's host, which this Fleet
        // Manager does not configure and whose media path it cannot see. Deriving an address for
        // it from the URL frames dial would be a configuration written for a machine this process
        // does not run.
        var external = new LiveKitOptions
        {
            Directory = "/tmp/livekit",
            Mode = LiveKitMode.External,
            PublicUrl = "ws://10.20.30.200:7880",
            ExternalUrl = "ws://192.0.2.10:7880",
        };

        Assert.Null(external.MediaAddress);
    }

    [Fact]
    public void Nothing_in_the_generated_file_configures_telemetry()
    {
        // §3.7 describes LiveKit as having "no telemetry unless configured". The way to keep that
        // true forever is a generated file with no section for anyone to fill in.
        var rendered = LiveKitConfigFile.Render(
            Options("/tmp/livekit"),
            new LiveKitCredential("APIkey", "secret", DateTimeOffset.UnixEpoch));

        Assert.DoesNotContain("telemetry", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("webhook", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prometheus", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writing_the_configuration_twice_changes_nothing_the_second_time()
    {
        using var workspace = new TempWorkspace();
        var options = Options(Path.Combine(workspace.Root, "livekit"));
        var credential = new LiveKitCredential("APIkey", "secret", DateTimeOffset.UnixEpoch);

        Assert.True(LiveKitConfigFile.Write(options, credential));
        Assert.False(LiveKitConfigFile.Write(options, credential));

        // A changed secret is a changed file, which is the caller's cue to restart the server.
        Assert.True(LiveKitConfigFile.Write(options, credential with { Secret = "another-secret" }));
    }

    [Fact]
    public void The_configuration_is_staged_beside_itself_and_renamed_into_place()
    {
        // FileMode.Create truncates the target and then writes it, which on this file opens two
        // windows: a power cut inside it leaves a document LiveKit refuses to parse — and a
        // configuration it cannot parse is a call server that does not start, not one that starts
        // degraded — and a reader that opens the file during it sees half of one. The bytes
        // therefore go to a sibling and reach the real name by rename(2). The stale sibling an
        // interrupted write would have left is consumed by the next one.
        using var workspace = new TempWorkspace();
        var options = Options(Path.Combine(workspace.Root, "livekit"));
        var credential = new LiveKitCredential("APIkey", "secret", DateTimeOffset.UnixEpoch);
        var staging = options.ConfigPath + LiveKitConfigFile.StagingSuffix;

        Directory.CreateDirectory(options.Directory);
        File.WriteAllText(staging, "port: 78");

        Assert.True(LiveKitConfigFile.Write(options, credential));

        Assert.False(File.Exists(staging));
        Assert.Equal(Path.GetDirectoryName(options.ConfigPath), Path.GetDirectoryName(staging));
        Assert.Equal(LiveKitConfigFile.Render(options, credential), File.ReadAllText(options.ConfigPath));
    }

    [Fact]
    public void A_write_that_cannot_even_start_leaves_the_last_good_configuration_exactly_as_it_was()
    {
        // The half the rename buys that the fsync never did. Writing in place destroyed the
        // working configuration the moment anything went wrong with the new one; staging means a
        // failure before the rename cannot touch the live file at all. The failure is produced by
        // putting a directory where the staging file wants to be.
        using var workspace = new TempWorkspace();
        var options = Options(Path.Combine(workspace.Root, "livekit"));
        var credential = new LiveKitCredential("APIkey", "secret", DateTimeOffset.UnixEpoch);

        Assert.True(LiveKitConfigFile.Write(options, credential));
        var before = File.ReadAllBytes(options.ConfigPath);

        Directory.CreateDirectory(options.ConfigPath + LiveKitConfigFile.StagingSuffix);

        var failure = Record.Exception(
            () => LiveKitConfigFile.Write(options, credential with { Secret = "a-rotated-secret" }));

        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"Expected the blocked staging path to fail the write; got {failure?.GetType().Name ?? "no exception"}.");

        Assert.Equal(before, File.ReadAllBytes(options.ConfigPath));
    }

    [Fact]
    public async Task The_key_and_secret_are_generated_once_and_survive_a_restart()
    {
        // §3.2: "LiveKit's key and secret are generated automatically." Generated, and then
        // never regenerated — a second secret silently invalidates every token already minted
        // from the first, which is exactly the failure guide 7's guarded printf existed to avoid.
        using var fixture = new StorageFixture();
        var store = new SqliteLiveKitStore(fixture.Database, fixture.Clock);

        Assert.Null(await store.FindAsync(TestContext.Current.CancellationToken));

        var first = await store.EnsureAsync(TestContext.Current.CancellationToken);
        var again = await store.EnsureAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first, again);
        Assert.StartsWith(SqliteLiveKitStore.KeyPrefix, first.Key, StringComparison.Ordinal);
        Assert.Equal(SqliteLiveKitStore.KeyPrefix.Length + SqliteLiveKitStore.KeyRandomLength, first.Key.Length);

        // LiveKit refuses a secret shorter than 32 characters.
        Assert.True(first.Secret.Length >= 32, first.Secret.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // A fresh store over the same file reads it back rather than making a new one.
        var reopened = new SqliteLiveKitStore(fixture.Database, fixture.Clock);
        Assert.Equal(first, await reopened.FindAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rotating_replaces_the_secret_and_keeps_the_key()
    {
        using var fixture = new StorageFixture();
        var store = new SqliteLiveKitStore(fixture.Database, fixture.Clock);

        var before = await store.EnsureAsync(TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        var after = await store.RotateSecretAsync(TestContext.Current.CancellationToken);

        Assert.Equal(before.Key, after.Key);
        Assert.NotEqual(before.Secret, after.Secret);
        Assert.True(after.IssuedUtc > before.IssuedUtc);
    }

    [Fact]
    public void A_generated_secret_is_safe_to_paste_anywhere()
    {
        // URL-safe base64 with no padding: no '+', '/' or '=' to be quoted differently by YAML,
        // a shell and a URL. The file it lands in is parsed by a Go YAML reader as a bare scalar.
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var secret = SqliteLiveKitStore.NewSecret();

            Assert.Equal(43, secret.Length);
            Assert.All(secret, character => Assert.True(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_',
                $"'{character}' is not URL-safe base64."));
        }
    }
}

/// <summary>
/// Whether the options say what §3.7 says, including the escape hatch.
/// </summary>
public sealed class LiveKitOptionsTests
{
    [Fact]
    public void Naming_an_existing_livekit_is_the_whole_of_the_escape_hatch()
    {
        // §3.7: "an operator with an existing LiveKit can point the Fleet Manager at it." One
        // variable switches the mode; a second one whose only job is to agree with the first
        // would be a thing to forget.
        var external = new LiveKitOptions
        {
            Directory = "/tmp",
            Mode = LiveKitMode.External,
            ExternalUrl = "wss://livekit.example.org",
            ExternalKey = "APIsomething",
            ExternalSecret = "a-secret-at-least-thirty-two-characters",
        };

        Assert.Empty(external.Problems());
        Assert.Equal("wss://livekit.example.org", external.EffectiveUrl);
        Assert.True(external.IsCallingConfigured);
    }

    [Fact]
    public void An_external_server_named_without_credentials_says_which_ones_are_missing()
    {
        var external = new LiveKitOptions
        {
            Directory = "/tmp",
            Mode = LiveKitMode.External,
            ExternalUrl = "wss://livekit.example.org",
        };

        var problem = Assert.Single(external.Problems());
        Assert.Contains(LiveKitOptions.ExternalKeyVariable, problem, StringComparison.Ordinal);
        Assert.Contains(LiveKitOptions.ExternalSecretVariable, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bundled_deployment_with_no_public_url_names_the_variable_rather_than_guessing()
    {
        // A container knows what it is bound to and nothing about the address a frame will reach
        // it on. §3.2's rule for exactly this shape of gap is that the instance explains itself.
        var bundled = new LiveKitOptions { Directory = "/tmp", Mode = LiveKitMode.Bundled };

        var problem = Assert.Single(bundled.Problems());
        Assert.Contains(LiveKitOptions.PublicUrlVariable, problem, StringComparison.Ordinal);
        Assert.Empty(bundled.EffectiveUrl);
    }

    [Fact]
    public void A_disabled_deployment_complains_about_nothing()
    {
        var off = new LiveKitOptions { Directory = "/tmp", Mode = LiveKitMode.Disabled };

        Assert.Empty(off.Problems());
        Assert.False(off.IsCallingConfigured);
    }

    [Fact]
    public void The_ports_are_the_two_halves_the_specification_splits_the_exposure_into()
    {
        // §3.7: signalling can ride Traefik as a WebSocket over TLS; WebRTC media cannot, so the
        // stack publishes the TCP fallback and the UDP range directly.
        var options = new LiveKitOptions { Directory = "/tmp", Mode = LiveKitMode.Bundled };

        Assert.Equal(7880, options.SignalPort);
        Assert.Equal(7881, options.TcpMediaPort);
        Assert.Equal(50_000, options.UdpPortStart);
        Assert.Equal(50_059, options.UdpPortEnd);
    }

    [Fact]
    public void The_token_lifetime_leaves_a_frame_months_of_calling_after_the_server_dies()
    {
        // §1.2 principle 2: a frame must keep working with the server unreachable. Renewal at a
        // third means a frame in contact carries a token minted within the last four months, so
        // a Fleet Manager that dies leaves the fleet eight months rather than v1's ten years or
        // a monthly token's four weeks.
        var options = new LiveKitOptions { Directory = "/tmp", Mode = LiveKitMode.Bundled };

        Assert.Equal(TimeSpan.FromDays(365), options.TokenLifetime);
        Assert.InRange(options.RenewalThreshold, TimeSpan.FromDays(120), TimeSpan.FromDays(123));
    }
}

/// <summary>
/// The installer that fetches the pinned release (§7.1's "reviewable facts, not memory").
/// </summary>
public sealed class LiveKitInstallerTests
{
    /// <summary>A tar.gz holding one member, so an archive can be built without a network.</summary>
    private static byte[] Archive(string memberName, byte[] payload)
    {
        using var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, memberName)
            {
                DataStream = new MemoryStream(payload),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            };

            writer.WriteEntry(entry);
        }

        return buffer.ToArray();
    }

    private sealed class FixedDownload(byte[] body) : ILiveKitDownload
    {
        public int Requests { get; private set; }

        public Task<Stream?> OpenAsync(Uri url, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult<Stream?>(new MemoryStream(body));
        }
    }

    private static (LiveKitReleasePin Pin, byte[] Archive) Fixture(string binaryContent = "the livekit server")
    {
        var payload = Encoding.UTF8.GetBytes(binaryContent);
        var archive = Archive("livekit-server", payload);

        var pin = new LiveKitReleasePin
        {
            Version = "9.9.9",
            ChecksumsUrl = new Uri("https://example.invalid/checksums.txt"),
            BinaryMemberName = "livekit-server",
            ReviewedUtc = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
            Assets = new Dictionary<Architecture, LiveKitAsset>
            {
                [RuntimeInformation.OSArchitecture] = new LiveKitAsset
                {
                    FileName = "livekit.tar.gz",
                    ArchiveUrl = new Uri("https://example.invalid/livekit.tar.gz"),
                    ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(archive)),
                    ArchiveSizeBytes = archive.Length,
                    BinarySha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
                    BinarySizeBytes = payload.Length,
                },
            },
        };

        return (pin, archive);
    }

    [Fact]
    public async Task A_release_that_matches_the_pin_is_installed_and_not_fetched_twice()
    {
        using var workspace = new TempWorkspace();
        var (pin, archive) = Fixture();
        var download = new FixedDownload(archive);
        var target = Path.Combine(workspace.Root, "livekit", "livekit-server");

        var installer = new LiveKitInstaller(
            target,
            RuntimeInformation.OSArchitecture,
            download,
            NullLogger.Instance,
            pin);

        Assert.Equal(
            LiveKitInstallResult.Installed,
            await installer.InstallAsync(TestContext.Current.CancellationToken));

        Assert.True(File.Exists(target));

        // Idempotent: the second call hashes what is on disk and fetches nothing. That is what
        // makes starting the container a hundred times cost one download.
        Assert.Equal(
            LiveKitInstallResult.AlreadyInstalled,
            await installer.InstallAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, download.Requests);
    }

    [Fact]
    public async Task A_tampered_archive_is_refused_before_it_is_decompressed()
    {
        // The whole reason the download is hashed to a staging file first: a gzip stream from an
        // unverified source is a decompressor being fed by whoever answered the URL.
        using var workspace = new TempWorkspace();
        var (pin, archive) = Fixture();
        var tampered = pin with
        {
            Assets = new Dictionary<Architecture, LiveKitAsset>
            {
                [RuntimeInformation.OSArchitecture] = pin.Assets[RuntimeInformation.OSArchitecture] with
                {
                    ArchiveSha256 = new string('0', 64),
                },
            },
        };

        var target = Path.Combine(workspace.Root, "livekit", "livekit-server");
        var installer = new LiveKitInstaller(
            target,
            RuntimeInformation.OSArchitecture,
            new FixedDownload(archive),
            NullLogger.Instance,
            tampered);

        Assert.Equal(
            LiveKitInstallResult.ArchiveChecksumMismatch,
            await installer.InstallAsync(TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + LiveKitInstaller.ArchiveStagingSuffix));
    }

    [Fact]
    public async Task An_archive_holding_the_wrong_executable_is_refused_after_it_is_unpacked()
    {
        // Verifying only the published digest would leave the installed artifact unchecked from
        // the second start onwards; this is the half that catches a pin whose two digests
        // disagree with each other.
        using var workspace = new TempWorkspace();
        var (pin, archive) = Fixture();
        var wrongBinary = pin with
        {
            Assets = new Dictionary<Architecture, LiveKitAsset>
            {
                [RuntimeInformation.OSArchitecture] = pin.Assets[RuntimeInformation.OSArchitecture] with
                {
                    BinarySha256 = new string('1', 64),
                },
            },
        };

        var target = Path.Combine(workspace.Root, "livekit", "livekit-server");
        var installer = new LiveKitInstaller(
            target,
            RuntimeInformation.OSArchitecture,
            new FixedDownload(archive),
            NullLogger.Instance,
            wrongBinary);

        Assert.Equal(
            LiveKitInstallResult.BinaryChecksumMismatch,
            await installer.InstallAsync(TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + LiveKitInstaller.BinaryStagingSuffix));
    }

    [Fact]
    public async Task An_unreachable_upstream_is_reported_rather_than_thrown()
    {
        using var workspace = new TempWorkspace();
        var (pin, _) = Fixture();

        var installer = new LiveKitInstaller(
            Path.Combine(workspace.Root, "livekit", "livekit-server"),
            RuntimeInformation.OSArchitecture,
            UnreachableLiveKitDownload.Instance,
            NullLogger.Instance,
            pin);

        Assert.Equal(
            LiveKitInstallResult.Unreachable,
            await installer.InstallAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_architecture_the_pin_does_not_name_is_refused_by_name()
    {
        using var workspace = new TempWorkspace();
        var (pin, archive) = Fixture();

        var installer = new LiveKitInstaller(
            Path.Combine(workspace.Root, "livekit", "livekit-server"),
            Architecture.LoongArch64,
            new FixedDownload(archive),
            NullLogger.Instance,
            pin);

        Assert.Equal(
            LiveKitInstallResult.UnsupportedArchitecture,
            await installer.InstallAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_shipped_pin_names_both_linux_architectures_and_nothing_else()
    {
        // §3.1 ships one Linux container; whether it runs on x64 or arm64 is the operator's
        // choice, and a pin listing artifacts nothing will ever fetch is a cost with no reader.
        var pin = LiveKitReleasePin.Current;

        Assert.Equal(
            [Architecture.X64, Architecture.Arm64],
            pin.Assets.Keys.OrderBy(architecture => architecture.ToString(), StringComparer.Ordinal).Reverse());

        foreach (var (architecture, asset) in pin.Assets)
        {
            Assert.Contains(pin.Version, asset.FileName, StringComparison.Ordinal);
            Assert.Contains(pin.Tag, asset.ArchiveUrl.ToString(), StringComparison.Ordinal);
            Assert.Equal(64, asset.ArchiveSha256.Length);
            Assert.Equal(64, asset.BinarySha256.Length);
            Assert.True(asset.ArchiveSizeBytes > 0);
            Assert.True(asset.BinarySizeBytes > asset.ArchiveSizeBytes, architecture.ToString());
        }
    }
}

/// <summary>
/// Adoption, renewal and rotation through the real server (§3.3, §3.7).
/// </summary>
/// <remarks>
/// Against the whole pipeline rather than against <c>CallProvisioning</c> alone, because the
/// property that matters is the one §7.2 asks for — what a frame is actually <i>told</i>. That
/// only exists once a real socket has been through the real handshake and collected a real
/// settings frame.
/// </remarks>
public sealed class ControlCallTokenTests
{
    private const string Password = "a-very-long-operator-password";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Adoption_issues_the_token_and_a_pending_device_gets_nothing()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        // Pending. §3.3: "A pending device receives nothing — no configuration, no token."
        await using (var pending = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, pending.Result.Status);
        }

        await server.SignInAsync(Password);
        Assert.Null(await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));

        await server.AdoptAsync(deviceId, "Douwe");

        var token = await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey);
        Assert.NotNull(token);

        var facts = LiveKitToken.Inspect(token);
        Assert.NotNull(facts);
        Assert.Equal(deviceId, facts.Identity);
        Assert.Equal(CallProvisioning.DefaultRoom, facts.Room);
        Assert.Equal("Douwe", facts.Name);

        // And the three values the token is bound to travel with it, because a token alone tells
        // the frame nothing about where to present it.
        Assert.Equal(deviceId, await server.EffectiveAsync(deviceId, CallProvisioning.IdentityKey));
        Assert.Equal("ws://livekit.invalid:7880", await server.EffectiveAsync(deviceId, CallProvisioning.UrlKey));
    }

    [Fact]
    public async Task An_adopted_frame_receives_its_token_in_the_settings_frame_on_connect()
    {
        // The answer to "how does the token reach the frame": it is a §3.4 setting, so it arrives
        // on the mechanism that already re-sends everything in full on every reconnect.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        await using var agent = await server.ConnectAgentAsync(key);
        Assert.Equal(HandshakeStatus.Ok, agent.Result.Status);

        var frames = await agent.AnswerPingsAsync(TimeSpan.FromMilliseconds(600));
        var push = frames
            .Where(frame => string.Equals(frame.Kind, ControlWire.KindSettings, StringComparison.Ordinal))
            .Select(frame => frame.PayloadAs(ProtocolJson.Default.SettingsPush))
            .LastOrDefault(payload => payload is not null);

        Assert.NotNull(push);
        Assert.True(push.Values.ContainsKey(CallProvisioning.TokenKey));
        Assert.Equal(deviceId, LiveKitToken.Inspect(push.Values[CallProvisioning.TokenKey])!.Identity);
    }

    [Fact]
    public async Task Reconnecting_does_not_mint_a_new_token_when_the_old_one_is_fine()
    {
        // Renewal has to be free in the common case, or every reconnect writes to the database
        // and bumps the settings revision for nothing.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var first = await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var agent = await server.ConnectAgentAsync(key);
            await agent.AnswerPingsAsync(TimeSpan.FromMilliseconds(200));
        }

        Assert.Equal(first, await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));
    }

    [Fact]
    public async Task Moving_the_fleet_room_re_mints_every_frame_bound_to_the_old_one()
    {
        // A token names a room. Changing the fleet default therefore invalidates every token in
        // the fleet, silently, until somebody presses a call button — so the write that causes it
        // is also the write that repairs it.
        await using var server = await ControlServer.StartAsync(Password);
        using var first = DeviceIdentity.CreateKeyPair();
        using var second = DeviceIdentity.CreateKeyPair();

        var one = await server.EnrolAsync(first, Password);
        var two = await server.EnrolAsync(second);

        Assert.Equal(CallProvisioning.DefaultRoom, LiveKitToken.Inspect(
            await server.EffectiveAsync(one, CallProvisioning.TokenKey))!.Room);

        var response = await server.SetFleetSettingAsync(CallProvisioning.RoomKey, "huisman");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        foreach (var deviceId in (string[])[one, two])
        {
            Assert.Equal("huisman", LiveKitToken.Inspect(
                await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey))!.Room);
        }
    }

    [Fact]
    public async Task A_per_device_room_re_mints_only_that_frame()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var first = DeviceIdentity.CreateKeyPair();
        using var second = DeviceIdentity.CreateKeyPair();

        var one = await server.EnrolAsync(first, Password);
        var two = await server.EnrolAsync(second);
        var untouched = await server.EffectiveAsync(two, CallProvisioning.TokenKey);

        var response = await server.SetDeviceSettingAsync(one, CallProvisioning.RoomKey, "kitchen");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal("kitchen", LiveKitToken.Inspect(
            await server.EffectiveAsync(one, CallProvisioning.TokenKey))!.Room);

        Assert.Equal(untouched, await server.EffectiveAsync(two, CallProvisioning.TokenKey));
    }

    [Fact]
    public async Task Rotating_the_secret_re_mints_the_whole_fleet_under_a_new_key()
    {
        // §3.7's "rotate at will", and the thing v1 could only do by hand at a workstation.
        await using var server = await ControlServer.StartAsync(Password);
        using var first = DeviceIdentity.CreateKeyPair();
        using var second = DeviceIdentity.CreateKeyPair();

        var one = await server.EnrolAsync(first, Password);
        var two = await server.EnrolAsync(second);

        var before = await server.EffectiveAsync(one, CallProvisioning.TokenKey);
        var status = await server.GetLiveKitAsync();

        var response = await server.Client.PostAsync("/api/livekit/rotate", content: null, Token);
        response.EnsureSuccessStatusCode();

        var rotated = await response.ReadAsync(ControlJson.Default.LiveKitRotateResponse);
        Assert.Equal(2, rotated.Issued);

        // The key is kept and the secret replaced, so every previously minted signature is now
        // refused by a server that has reloaded — which is what revocation means here.
        Assert.Equal(status.ApiKey, rotated.ApiKey);

        var after = await server.EffectiveAsync(one, CallProvisioning.TokenKey);
        Assert.NotEqual(before, after);
        Assert.NotNull(await server.EffectiveAsync(two, CallProvisioning.TokenKey));
    }

    [Fact]
    public async Task One_frame_can_be_re_issued_on_demand()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/call-token", content: null, Token);
        response.EnsureSuccessStatusCode();

        var issued = await response.ReadAsync(ControlJson.Default.CallTokenResponse);
        Assert.Equal("issued", issued.Outcome);
        Assert.Equal(deviceId, issued.Identity);
        Assert.Equal(CallProvisioning.DefaultRoom, issued.Room);

        var stored = await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey);
        Assert.Equal(deviceId, LiveKitToken.Inspect(stored)!.Identity);
        Assert.Equal(issued.ExpiresUtc!.Value.ToUnixTimeSeconds(), LiveKitToken.ExpiryOf(stored)!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Minting_is_deterministic_within_a_second_and_that_is_not_revocation()
    {
        // Two mints of the same identity, room, name and second produce the same bytes, because
        // every input to the signature is the same — `nbf` and `exp` are whole seconds. Worth
        // pinning rather than papering over with a random claim, because it names the real
        // property: <b>re-issuing does not revoke</b>. Nothing invalidates an old token except
        // replacing the secret it was signed with, so an operator who thinks a token has leaked
        // must rotate, and a button that handed back different-looking bytes would suggest
        // otherwise.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var first = await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey);

        (await server.Client.PostAsync($"/api/devices/{deviceId}/call-token", content: null, Token))
            .EnsureSuccessStatusCode();

        var second = await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey);

        // Rotation is the operation that actually changes the artifact, because it changes what
        // signs it.
        (await server.Client.PostAsync("/api/livekit/rotate", content: null, Token)).EnsureSuccessStatusCode();

        Assert.NotEqual(first, await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));
        Assert.Equal(LiveKitToken.Inspect(first)!.Identity, LiveKitToken.Inspect(second)!.Identity);
    }

    [Fact]
    public async Task A_pending_device_cannot_be_issued_a_token_by_asking()
    {
        // The route is refused, and underneath it the settings store's adopted-only guard means
        // there is structurally nowhere for a token to be written even if it were not.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = DeviceIdentity.FingerprintOf(key.ExportSubjectPublicKeyInfo());

        await using (var pending = await server.ConnectAgentAsync(key))
        {
            Assert.Equal(HandshakeStatus.Pending, pending.Result.Status);
        }

        await server.SignInAsync(Password);
        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/call-token", content: null, Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not-adopted", (await response.ReadAsync(ControlJson.Default.CallTokenResponse)).Outcome);
        Assert.Null(await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));
    }

    [Fact]
    public async Task A_public_url_set_after_adoption_still_reaches_the_frames()
    {
        // The address is not in the token, so a review that only asked the token whether it was
        // happy would never write it. An operator who adopts frames first and sets
        // FRAMELINK_LIVEKIT_PUBLIC_URL afterwards is the ordinary case, not a mistake — the
        // variable is one they may only think about once a frame is asking for a call.
        await using var server = await ControlServer.StartAsync(
            Password,
            livekit: options => options with { PublicUrl = string.Empty });

        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        Assert.NotNull(await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));
        Assert.Null(await server.EffectiveAsync(deviceId, CallProvisioning.UrlKey));

        var status = await server.GetLiveKitAsync();
        Assert.False(status.Ready);
        Assert.Contains(
            status.Problems,
            problem => problem.Contains(LiveKitOptions.PublicUrlVariable, StringComparison.Ordinal));

        // A restart with the variable set. The frame reconnects with a token whose every claim is
        // still correct, and must be told the address all the same.
        await using var restarted = await ControlServer.StartAsync(
            Password,
            configure: options => options with { DataDirectory = server.Workspace.Root },
            livekit: options => options with
            {
                Directory = Path.Combine(server.Workspace.Root, "livekit"),
                PublicUrl = "ws://10.20.30.250:7880",
            });

        await restarted.SignInAsync(Password);
        var before = await restarted.EffectiveAsync(deviceId, CallProvisioning.TokenKey);

        await using (var agent = await restarted.ConnectAgentAsync(key))
        {
            await agent.AnswerPingsAsync(TimeSpan.FromMilliseconds(300));
        }

        Assert.Equal("ws://10.20.30.250:7880", await restarted.EffectiveAsync(deviceId, CallProvisioning.UrlKey));

        // And the token is left alone, because nothing it names has changed.
        Assert.Equal(before, await restarted.EffectiveAsync(deviceId, CallProvisioning.TokenKey));
    }

    [Fact]
    public async Task Unblocking_a_frame_takes_its_token_away()
    {
        // Not a separate revocation step. §3.3 makes unblocking drop everything adoption granted,
        // which the settings store implements by deleting the overrides — and the token is one.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        Assert.NotNull(await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));

        (await server.Client.PostAsync($"/api/devices/{deviceId}/block", content: null, Token))
            .EnsureSuccessStatusCode();
        (await server.Client.PostAsync($"/api/devices/{deviceId}/unblock", content: null, Token))
            .EnsureSuccessStatusCode();

        Assert.Null(await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));
    }

    [Fact]
    public async Task Renaming_a_frame_re_mints_so_the_household_sees_the_new_name()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        Assert.Null(LiveKitToken.Inspect(await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey))!.Name);

        await server.AdoptAsync(deviceId, "Oma");

        Assert.Equal("Oma", LiveKitToken.Inspect(
            await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey))!.Name);
    }

    [Fact]
    public async Task A_disabled_deployment_adopts_frames_and_issues_no_token()
    {
        // Everything else has to keep working. A Fleet Manager with calling switched off is still
        // a Fleet Manager, and the frames it adopts still get photos.
        await using var server = await ControlServer.StartAsync(
            Password,
            livekit: options => options with { Mode = LiveKitMode.Disabled });

        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        Assert.Null(await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));

        var status = await server.GetLiveKitAsync();
        Assert.Equal("disabled", status.Mode);
        Assert.False(status.Ready);
        Assert.Empty(status.Problems);

        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/call-token", content: null, Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task An_external_livekit_is_used_verbatim_and_cannot_be_rotated_here()
    {
        // §3.7's escape hatch, end to end: the operator's own URL and secret sign the token, and
        // the rotate button refuses rather than pretending to have rotated somebody else's server.
        await using var server = await ControlServer.StartAsync(
            Password,
            livekit: options => options with
            {
                Mode = LiveKitMode.External,
                ExternalUrl = "wss://livekit.example.org",
                ExternalKey = "APIoperators",
                ExternalSecret = "an-operators-own-secret-thirty-two-plus",
            });

        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var facts = LiveKitToken.Inspect(await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));
        Assert.NotNull(facts);
        Assert.Equal("APIoperators", facts.Issuer);
        Assert.Equal("wss://livekit.example.org", await server.EffectiveAsync(deviceId, CallProvisioning.UrlKey));

        var status = await server.GetLiveKitAsync();
        Assert.Equal("external", status.Mode);
        Assert.Equal("APIoperators", status.ApiKey);

        var refused = await server.Client.PostAsync("/api/livekit/rotate", content: null, Token);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("not-rotatable", (await refused.ReadAsync(ControlJson.Default.ApiError)).Error);
    }

    [Fact]
    public async Task The_status_route_shows_the_key_and_never_the_secret()
    {
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        var status = await server.GetLiveKitAsync();
        Assert.Equal("bundled", status.Mode);
        Assert.Equal(LiveKitReleasePin.Current.Version, status.Version);
        Assert.StartsWith(SqliteLiveKitStore.KeyPrefix, status.ApiKey!, StringComparison.Ordinal);
        Assert.Equal(365, status.TokenLifetimeDays);

        // The whole response body, not just the field. §3.7 makes the Fleet Manager the owner of
        // the secret precisely so that nothing else holds it, and a browser is something else.
        var body = await (await server.Client.GetAsync("/api/livekit", Token)).Content.ReadAsStringAsync(Token);
        var secret = await new SqliteLiveKitStore(
            new SqliteDatabase(Path.Combine(server.Workspace.Root, "framelink.db")),
            TimeProvider.System).FindAsync(Token);

        Assert.NotNull(secret);
        Assert.DoesNotContain(secret.Secret, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_call_routes_are_behind_the_operator_password()
    {
        // /api/status and /api/session are the only two exempt routes. A rotate button anybody
        // could press would be a fleet-wide denial of service on an internet-exposed server.
        await using var server = await ControlServer.StartAsync(Password);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.GetAsync("/api/livekit", Token)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.PostAsync("/api/livekit/rotate", content: null, Token)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.PostAsync("/api/devices/anything/call-token", content: null, Token)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.PostAsync("/api/livekit/guest-token?identity=jori", content: null, Token)).StatusCode);
    }
}

/// <summary>
/// The token a person joins with (§3.7, decision 86).
/// </summary>
/// <remarks>
/// <para>
/// Every test here is about a boundary rather than about minting, because minting is
/// <c>LiveKitToken</c>'s and is already pinned against a live server above. What this route adds
/// is four refusals and one guarantee: the namespace a frame can never occupy, the room the fleet
/// actually uses, a lifetime measured in hours, and — the one that would be discovered in front of
/// a family rather than here — that asking for a person's token does not disturb a frame's.
/// </para>
/// </remarks>
public sealed class ControlGuestTokenTests
{
    private const string Password = "a-very-long-operator-password";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_person_is_minted_a_joinable_token_under_a_name_of_their_own()
    {
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        var minted = await MintAsync(server, "identity=jori");

        // The prefix is the route's, not the caller's: they asked for `jori` and are told exactly
        // what LiveKit will call them, because the token is useless to anyone who has to guess.
        Assert.Equal("guest:jori", minted.Identity);
        Assert.Equal(CallProvisioning.DefaultRoom, minted.Room);
        Assert.Equal("ws://livekit.invalid:7880", minted.Url);

        var facts = LiveKitToken.Inspect(minted.Token);
        Assert.NotNull(facts);
        Assert.Equal("guest:jori", facts.Identity);
        Assert.Equal(CallProvisioning.DefaultRoom, facts.Room);

        // The bare name is the display name, so what the household sees on screen is `jori` while
        // what LiveKit keys on is the namespaced form that cannot collide with a frame.
        Assert.Equal("jori", facts.Name);
        Assert.Equal(minted.ExpiresUtc.ToUnixTimeSeconds(), facts.Expires!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Minting_for_a_person_leaves_every_frames_credentials_untouched()
    {
        // The property the device route cannot offer. `/api/devices/{id}/call-token` runs a forced
        // review, which re-mints and pushes — so using it to get into a call would rotate the live
        // token of the frame whose id was borrowed. This route writes nothing at all, which is
        // what makes it safe to call while a frame is mid-call.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var before = await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey);
        var identityBefore = await server.EffectiveAsync(deviceId, CallProvisioning.IdentityKey);
        var revisionBefore = (await server.GetDeviceSettingsAsync(deviceId)).Revision;

        await MintAsync(server, "identity=jori");

        Assert.Equal(before, await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));
        Assert.Equal(identityBefore, await server.EffectiveAsync(deviceId, CallProvisioning.IdentityKey));

        // The revision counter moves on every settings write anywhere in the fleet, so an
        // unchanged one is the assertion that nothing was written rather than that nothing
        // important was.
        Assert.Equal(revisionBefore, (await server.GetDeviceSettingsAsync(deviceId)).Revision);
    }

    [Fact]
    public async Task A_frame_is_never_given_an_identity_from_the_reserved_namespace()
    {
        // The other half of the partition. Settings are generic (§3.4), so nothing stops an
        // operator writing `guest:jori` into call.identity — and if that reached a token, the next
        // person minted as `guest:jori` and that frame would kick each other out of the call. The
        // value is ignored where a setting becomes a participant identity, and the corrected one
        // is written back, so the setting heals rather than staying quietly wrong.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        (await server.SetDeviceSettingAsync(deviceId, CallProvisioning.IdentityKey, "guest:jori"))
            .EnsureSuccessStatusCode();

        var response = await server.Client.PostAsync($"/api/devices/{deviceId}/call-token", content: null, Token);
        response.EnsureSuccessStatusCode();

        var issued = await response.ReadAsync(ControlJson.Default.CallTokenResponse);
        Assert.Equal(deviceId, issued.Identity);
        Assert.Equal(deviceId, await server.EffectiveAsync(deviceId, CallProvisioning.IdentityKey));

        var stored = await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey);
        Assert.Equal(deviceId, LiveKitToken.Inspect(stored)!.Identity);
    }

    [Fact]
    public async Task An_operator_set_identity_outside_the_namespace_is_still_honoured()
    {
        // The reservation is one string, not a policy against hand-set identities. Narrowing it
        // further would be a behaviour change to converged fleets dressed up as a safety measure.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        (await server.SetDeviceSettingAsync(deviceId, CallProvisioning.IdentityKey, "kitchen-frame"))
            .EnsureSuccessStatusCode();

        var stored = await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey);
        Assert.Equal("kitchen-frame", LiveKitToken.Inspect(stored)!.Identity);
    }

    [Fact]
    public async Task A_room_no_frame_is_in_is_refused_and_the_real_ones_are_named()
    {
        // room.auto_create is on, so a mistyped room is not an error anywhere downstream: it is a
        // brand-new empty room, a valid token, and one participant sitting alone. The refusal
        // happens here or it does not happen at all.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        await server.EnrolAsync(key, Password);

        var response = await server.Client.PostAsync(
            "/api/livekit/guest-token?identity=jori&room=famliy",
            content: null,
            Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.ReadAsync(ControlJson.Default.ApiError);
        Assert.Equal("no-such-room", error.Error);
        Assert.Contains(CallProvisioning.DefaultRoom, error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_room_a_frame_was_moved_into_is_a_room_a_person_can_be_minted_into()
    {
        // The set of rooms is read from what frames were actually issued, so moving one frame to
        // its own room makes that room mintable and does not make the fleet default unmintable.
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        (await server.SetDeviceSettingAsync(deviceId, CallProvisioning.RoomKey, "kitchen"))
            .EnsureSuccessStatusCode();

        Assert.Equal("kitchen", (await MintAsync(server, "identity=jori&room=kitchen")).Room);
        Assert.Equal(
            CallProvisioning.DefaultRoom,
            (await MintAsync(server, "identity=jori&room=family")).Room);
    }

    [Fact]
    public async Task The_fleet_default_room_is_mintable_with_no_frames_at_all()
    {
        // Proving the call server works before there is anything to call is the first thing anyone
        // does with this route, and an empty fleet is exactly when the room check would otherwise
        // have nothing to say yes to.
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        Assert.Equal(CallProvisioning.DefaultRoom, (await MintAsync(server, "identity=jori")).Room);

        (await server.SetFleetSettingAsync(CallProvisioning.RoomKey, "huisman")).EnsureSuccessStatusCode();

        // And moving the fleet default moves what an unqualified request means, rather than
        // pinning people to a room the frames have left.
        Assert.Equal("huisman", (await MintAsync(server, "identity=jori")).Room);
    }

    [Fact]
    public async Task A_name_that_could_forge_a_namespace_or_carry_a_sentence_is_refused()
    {
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        foreach (var bad in (string[])["", "   ", "guest:jori", "jori huisman", "jori/../frame", new string('j', 65)])
        {
            var response = await server.Client.PostAsync(
                $"/api/livekit/guest-token?identity={Uri.EscapeDataString(bad)}",
                content: null,
                Token);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // The colon is the character the refusal is really about: with it excluded, `guest:` is
        // the only namespace prefix a minted identity can have.
        Assert.Equal("guest:jori.h_2", (await MintAsync(server, "identity=jori.h_2")).Identity);
    }

    [Fact]
    public async Task A_persons_token_lasts_hours_where_a_frames_lasts_a_year()
    {
        await using var server = await ControlServer.StartAsync(Password);
        using var key = DeviceIdentity.CreateKeyPair();
        var deviceId = await server.EnrolAsync(key, Password);

        var minted = await MintAsync(server, "identity=jori");
        var frame = LiveKitToken.ExpiryOf(await server.EffectiveAsync(deviceId, CallProvisioning.TokenKey));

        // Four hours, not four hours give or take a configuration: there is no knob, because a
        // caller-supplied lifetime is a way to ask for a year on a credential nothing renews and
        // nothing revokes.
        var life = minted.ExpiresUtc - DateTimeOffset.UtcNow;
        Assert.InRange(life, TimeSpan.FromHours(3.9), CallProvisioning.GuestLifetime);
        Assert.Equal(TimeSpan.FromHours(4), CallProvisioning.GuestLifetime);
        Assert.True(frame!.Value - minted.ExpiresUtc > TimeSpan.FromDays(300));
    }

    [Fact]
    public async Task The_response_carries_the_token_and_never_the_secret()
    {
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        var body = await (await server.Client.PostAsync(
            "/api/livekit/guest-token?identity=jori",
            content: null,
            Token)).Content.ReadAsStringAsync(Token);

        var credential = await new SqliteLiveKitStore(
            new SqliteDatabase(Path.Combine(server.Workspace.Root, "framelink.db")),
            TimeProvider.System).FindAsync(Token);

        Assert.NotNull(credential);

        // The whole body, not just the field the secret would have had. The secret is what signs
        // every token in the fleet, and a route that handed one out would make every frame's
        // credential mintable by whoever holds this response.
        Assert.DoesNotContain(credential.Secret, body, StringComparison.Ordinal);

        // The key is a different matter and it does travel — inside the token's `iss` claim, where
        // LiveKit reads it to know which secret to check. It is base64url rather than plain text
        // there, so the assertion has to decode; the point is that it is an identifier, and the
        // status route already shows it.
        var minted = await MintAsync(server, "identity=jori");
        Assert.Equal(credential.Key, LiveKitToken.Inspect(minted.Token)!.Issuer);
    }

    [Fact]
    public async Task A_deployment_with_calling_switched_off_mints_nothing_for_anybody()
    {
        await using var server = await ControlServer.StartAsync(
            Password,
            livekit: options => options with { Mode = LiveKitMode.Disabled });

        await server.SignInAsync(Password);

        var response = await server.Client.PostAsync(
            "/api/livekit/guest-token?identity=jori",
            content: null,
            Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not-configured", (await response.ReadAsync(ControlJson.Default.ApiError)).Error);
    }

    [Fact]
    public async Task An_external_livekit_signs_a_persons_token_with_the_operators_own_secret()
    {
        // §3.7's escape hatch reaches this route by the same path a frame's token does, because
        // both ask LiveKitDeployment rather than reaching for the bundled secret themselves.
        await using var server = await ControlServer.StartAsync(
            Password,
            livekit: options => options with
            {
                Mode = LiveKitMode.External,
                ExternalUrl = "wss://livekit.example.org",
                ExternalKey = "APIborrowed",
                ExternalSecret = "a-borrowed-secret-of-at-least-thirty-two",
            });

        await server.SignInAsync(Password);

        var minted = await MintAsync(server, "identity=jori");

        Assert.Equal("wss://livekit.example.org", minted.Url);
        Assert.Equal("APIborrowed", LiveKitToken.Inspect(minted.Token)!.Issuer);
    }

    private static async Task<CallGuestTokenResponse> MintAsync(ControlServer server, string query)
    {
        var response = await server.Client.PostAsync($"/api/livekit/guest-token?{query}", content: null, Token);
        response.EnsureSuccessStatusCode();
        return await response.ReadAsync(ControlJson.Default.CallGuestTokenResponse);
    }
}
