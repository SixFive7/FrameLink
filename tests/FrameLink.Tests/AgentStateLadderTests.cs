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
    /// <summary>
    /// Every <c>HandshakeStatus</c> that is a verdict about the frame.
    /// </summary>
    /// <remarks>
    /// <c>rate-limited</c> is deliberately absent and its absence is the design, not an oversight
    /// waiting to be tidied up. §3.3's per-device budget answers a frame "not this minute", which
    /// is a fact about the server rather than a rung this frame is standing on — so
    /// <c>HandshakeExchange</c> turns it into a failed exchange and it never reaches the ladder at
    /// all. Adding it below would break these tests correctly: it has no rung, it is not
    /// authoritative, and it must not stop the product.
    /// </remarks>
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
    public void Every_rung_the_ladder_declares_is_one_something_can_actually_reach()
    {
        // Decision 82. `Reconciling` sat in this enum with an accent of its own in StagePalette
        // and no producer anywhere: DeviceStateLadder is the only thing that builds a
        // DeviceCondition, and it resolves a handshake outcome or silence, neither of which can
        // yield it. §2.6's row of that name is AgentStatus.Drifted — orthogonal to this ladder,
        // because a frame can be unreachable-but-was-green and locally drifted at once — so the
        // member could never have been set without collapsing two facts into one.
        //
        // A dead rung is not inert. It reads as a state the frame can be in, so a later change
        // reasons about a case that does not exist, and the palette painted an accent nothing
        // could ever select. This asserts the enum against its producers rather than against a
        // list, so adding a member without a path to it turns the suite red.
        var reachable = AllStatuses
            .Append("a-status-from-a-newer-server")
            .Select(status => DeviceStateLadder.FromHandshake(Result(status)).State)
            .Append(DeviceStateLadder.NoContact(lastAuthoritative: null, "no route to host").State)
            .Append(DeviceStateLadder.Starting.State)
            .Append(DeviceStateLadder.Remembered.State)
            .ToHashSet();

        Assert.Equal(Enum.GetValues<DeviceState>().ToHashSet(), reachable);
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
