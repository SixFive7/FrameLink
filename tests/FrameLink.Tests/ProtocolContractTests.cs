using System.Text.Json;
using FrameLink.Agent;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The shared wire contract, and the two properties that make sharing it safe.
/// </summary>
/// <remarks>
/// <para>
/// <c>ping</c>, <c>pong</c> and <c>settings</c> used to exist twice — once in
/// <c>FrameLink.Control.ControlWire</c>, once in <c>FrameLink.Agent.Link.ControlChannel</c> —
/// and agreed only because one integration test said so. The reason given for the duplication
/// was real but misapplied: the agent must not acquire the Fleet Manager's SQLite and ASP.NET
/// dependencies, because §2.1 requires it to be one self-contained ELF. That is an argument
/// against referencing the <i>server</i>, not against a shared <i>contract</i>.
/// </para>
/// <para>
/// So the constraint moved here, where it is checked rather than remembered.
/// </para>
/// </remarks>
public sealed class ProtocolContractTests
{
    /// <summary>Assemblies a frame must never end up carrying.</summary>
    /// <remarks>
    /// Not an exhaustive list of everything bad, but the exact list of what the Fleet Manager
    /// pulls in that a 1.35 MB headless binary has no business linking.
    /// </remarks>
    private static readonly string[] ServerOnlyAssemblies =
    [
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw",
        "Microsoft.AspNetCore",
        "FrameLink.Control",
    ];

    [Fact]
    public void The_shared_contract_depends_on_nothing_at_all()
    {
        var referenced = typeof(WireEnvelope).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        // This is what makes FrameLink.Protocol a safe home for both programs. The moment it
        // acquires a package reference, promoting a type into it stops being free for the agent.
        Assert.DoesNotContain(referenced, name => ServerOnlyAssemblies.Any(
            server => name.StartsWith(server, StringComparison.Ordinal)));
    }

    [Fact]
    public void The_agent_gains_no_server_dependency_by_sharing_the_contract()
    {
        var referenced = typeof(AgentHost).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("FrameLink.Protocol", referenced);
        Assert.DoesNotContain(referenced, name => ServerOnlyAssemblies.Any(
            server => name.StartsWith(server, StringComparison.Ordinal)));
    }

    [Fact]
    public void The_promoted_payloads_keep_the_wire_names_they_shipped_with()
    {
        // camelCase and null-omission are part of the contract rather than a formatting
        // preference, and a renamed property is a silent protocol break: the peer deserialises
        // a default instead of failing. Asserting the bytes is the only way to notice.
        var ping = JsonSerializer.Serialize(
            new AgentPing { Sequence = 7, SentUtc = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero) },
            ProtocolJson.Default.AgentPing);

        var pong = JsonSerializer.Serialize(
            new AgentPong { Sequence = 7 },
            ProtocolJson.Default.AgentPong);

        var settings = JsonSerializer.Serialize(
            new SettingsPush
            {
                DeviceId = "AAAA-AAAA-AAAA-AAAA",
                Revision = 3,
                Values = new Dictionary<string, string> { ["audio.volume"] = "75" },
            },
            ProtocolJson.Default.SettingsPush);

        Assert.Equal("""{"sequence":7,"sentUtc":"2026-08-15T12:00:00+00:00"}""", ping);
        Assert.Equal("""{"sequence":7}""", pong);
        Assert.Equal(
            """{"deviceId":"AAAA-AAAA-AAAA-AAAA","revision":3,"values":{"audio.volume":"75"}}""",
            settings);
    }

    [Fact]
    public void A_settings_push_survives_the_round_trip_the_agent_will_make()
    {
        // The agent does not read settings yet — M2 does. Registering the type for reading now
        // is what stops that from being an AOT surprise on a frame later, since an interface-typed
        // dictionary is exactly the shape a reflection-free serialiser can refuse.
        var encoded = WireMessage.Encode(
            ControlWire.KindSettings,
            new SettingsPush
            {
                DeviceId = "AAAA-AAAA-AAAA-AAAA",
                Revision = 12,
                Values = new Dictionary<string, string> { ["call.room"] = "huisman" },
            },
            ProtocolJson.Default.SettingsPush,
            ProtocolConstants.ChannelControl);

        var envelope = WireMessage.Decode(encoded);
        Assert.NotNull(envelope);
        var push = envelope.PayloadAs(ProtocolJson.Default.SettingsPush);

        Assert.Equal(ControlWire.KindSettings, envelope.Kind);
        Assert.Equal(ProtocolConstants.ChannelControl, envelope.Channel);
        Assert.Equal(12, push!.Revision);
        Assert.Equal("huisman", push.Values["call.room"]);
    }

    [Fact]
    public void A_ping_is_answered_even_when_its_sequence_cannot_be_read()
    {
        // The server's deadline is refreshed by any inbound traffic, so silence over one
        // unreadable field would drop a working connection — the exact failure the exchange
        // exists to detect. A newer server is free to reshape its ping; the answer still goes.
        var wellFormed = WireMessage.Decode(
            WireMessage.Encode(
                ControlWire.KindPing,
                new AgentPing { Sequence = 41, SentUtc = DateTimeOffset.UnixEpoch },
                ProtocolJson.Default.AgentPing,
                ProtocolConstants.ChannelControl));

        var reshaped = WireMessage.Decode(
            """{"magic":"framelink","kind":"ping","channel":"control","payload":{"tick":9}}"""u8);

        var empty = WireMessage.Decode(
            """{"magic":"framelink","kind":"ping","channel":"control"}"""u8);

        Assert.Equal(41, ControlWire.SequenceOf(wellFormed!));
        Assert.Equal(0, ControlWire.SequenceOf(reshaped!));
        Assert.Equal(0, ControlWire.SequenceOf(empty!));
    }
}
