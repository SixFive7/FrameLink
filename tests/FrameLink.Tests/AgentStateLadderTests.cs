using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// The device state ladder of version2.md §2.6.
/// </summary>
/// <remarks>
/// The rule these tests exist to hold in place is the one sentence the whole offline story rests
/// on: <b>rejection is an answer; silence is not</b>. An authoritative "you are not adopted" stops
/// the product on a frame; an unreachable server does not, provided the frame was fully green when
/// contact dropped — because an outage in the operator's house must never blank a frame in someone
/// else's.
/// </remarks>
public sealed class AgentStateLadderTests
{
    /// <summary>Every outcome the frozen handshake defines (<c>HandshakeStatus</c>).</summary>
    private static readonly string[] AllStatuses =
    [
        HandshakeStatus.Ok,
        HandshakeStatus.Pending,
        HandshakeStatus.Blocked,
        HandshakeStatus.NotConfigured,
        HandshakeStatus.VersionMismatch,
        HandshakeStatus.BadSignature,
    ];

    public static TheoryData<string> EveryStatus() => [.. AllStatuses];

    [Theory]
    [MemberData(nameof(EveryStatus))]
    public void Every_handshake_status_lands_on_a_named_rung_with_its_own_wording(string status)
    {
        var condition = DeviceStateLadder.FromHandshake(Result(status));

        Assert.Equal(status, condition.Cause);
        Assert.NotEmpty(condition.Headline);
        Assert.NotEmpty(condition.Detail);
        Assert.True(condition.IsAuthoritative);
    }

    [Fact]
    public void No_two_statuses_produce_the_same_thing_on_screen()
    {
        // §1.2.3: every abnormal state is named. Two statuses rendering identically would be a
        // generic error by accident — the frame would say the same thing for "adopt me" as for
        // "your key was refused", and the operator would have no way to tell them apart.
        var headlines = AllStatuses
            .Select(status => DeviceStateLadder.FromHandshake(Result(status)).Headline)
            .ToList();

        Assert.Equal(headlines.Count, headlines.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(HandshakeStatus.Ok, DeviceState.InSync)]
    [InlineData(HandshakeStatus.Pending, DeviceState.NotAdopted)]
    [InlineData(HandshakeStatus.Blocked, DeviceState.NotAdopted)]
    [InlineData(HandshakeStatus.NotConfigured, DeviceState.ControlNotConfigured)]
    [InlineData(HandshakeStatus.VersionMismatch, DeviceState.VersionMismatch)]
    [InlineData(HandshakeStatus.BadSignature, DeviceState.NotAdopted)]
    public void Each_status_maps_to_the_rung_the_specification_names(string status, DeviceState expected)
    {
        Assert.Equal(expected, DeviceStateLadder.FromHandshake(Result(status)).State);
    }

    [Fact]
    public void Only_a_green_answer_lets_the_product_run()
    {
        foreach (var status in AllStatuses)
        {
            var condition = DeviceStateLadder.FromHandshake(Result(status));

            Assert.Equal(
                string.Equals(status, HandshakeStatus.Ok, StringComparison.Ordinal),
                condition.ProductRuns);
        }
    }

    [Fact]
    public void A_status_from_a_newer_server_is_reported_rather_than_swallowed()
    {
        // The handshake statuses are frozen string constants rather than an enum precisely so an
        // unknown value is reportable. An agent that cannot understand the answer is an agent that
        // needs updating, and it says which word it did not understand.
        var condition = DeviceStateLadder.FromHandshake(Result("quarantined-pending-audit"));

        Assert.Equal(DeviceState.VersionMismatch, condition.State);
        Assert.Equal(DeviceStateLadder.UnknownStatusCause, condition.Cause);
        Assert.Contains("quarantined-pending-audit", condition.Detail, StringComparison.Ordinal);
        Assert.False(condition.ProductRuns);
    }

    [Fact]
    public void Silence_after_a_green_answer_keeps_the_photos_on_screen()
    {
        var green = DeviceStateLadder.FromHandshake(Result(HandshakeStatus.Ok));

        var silence = DeviceStateLadder.NoContact(green, "connection reset");

        Assert.Equal(DeviceState.NoContact, silence.State);
        Assert.True(silence.ProductRuns);
        Assert.False(silence.IsAuthoritative);
    }

    [Theory]
    [InlineData(HandshakeStatus.Pending)]
    [InlineData(HandshakeStatus.Blocked)]
    [InlineData(HandshakeStatus.NotConfigured)]
    [InlineData(HandshakeStatus.VersionMismatch)]
    [InlineData(HandshakeStatus.BadSignature)]
    public void Silence_after_anything_else_leaves_the_frame_exactly_where_it_was(string status)
    {
        var lastAnswer = DeviceStateLadder.FromHandshake(Result(status));

        var silence = DeviceStateLadder.NoContact(lastAnswer, "connection reset");

        Assert.False(silence.ProductRuns);
    }

    [Fact]
    public void Silence_from_a_frame_that_never_reached_a_server_shows_nothing()
    {
        var silence = DeviceStateLadder.NoContact(lastAuthoritative: null, "no route to host");

        Assert.False(silence.ProductRuns);
        Assert.Equal(DeviceStateLadder.SilenceCause, silence.Cause);
        Assert.Contains("no route to host", silence.ServerMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fresh_agent_starts_on_the_silence_rung_with_the_product_stopped()
    {
        Assert.Equal(DeviceState.NoContact, DeviceStateLadder.Starting.State);
        Assert.False(DeviceStateLadder.Starting.ProductRuns);
        Assert.False(DeviceStateLadder.Starting.IsAuthoritative);
    }

    [Fact]
    public void The_servers_own_words_are_carried_through_verbatim()
    {
        var condition = DeviceStateLadder.FromHandshake(new HandshakeResult
        {
            Status = HandshakeStatus.Blocked,
            ProtocolVersion = ProtocolConstants.Version,
            Message = "Blocked by jori on 2026-08-14.",
        });

        Assert.Equal("Blocked by jori on 2026-08-14.", condition.ServerMessage);
    }

    private static HandshakeResult Result(string status) => new()
    {
        Status = status,
        ProtocolVersion = ProtocolConstants.Version,
        ServedAgentVersion = "0.2.0",
    };
}
