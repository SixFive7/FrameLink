using FrameLink.Protocol;

namespace FrameLink.Agent.State;

/// <summary>
/// The device state ladder of §2.6, outermost rung first.
/// </summary>
/// <remarks>
/// There is no <c>Error</c> rung and there never will be. §1.2.3: every abnormal state is
/// named, on the frame and in the Fleet Manager. A generic failure bucket is how a frame ends
/// up quietly wrong.
/// </remarks>
public enum DeviceState
{
    /// <summary>Fleet Manager unreachable — silence, not an answer.</summary>
    NoContact,

    /// <summary>Server reachable but no admin credential set (§3.2).</summary>
    ControlNotConfigured,

    /// <summary>New, blocked, or orphaned by a rebuilt server (§3.3).</summary>
    NotAdopted,

    /// <summary>Agent version differs from the version this server serves (§2.8).</summary>
    VersionMismatch,

    /// <summary>A resource drifted or was never applied.</summary>
    Reconciling,

    /// <summary>Everything verified.</summary>
    InSync,
}

/// <summary>
/// One rung of the ladder, resolved for a specific cause and ready to render.
/// </summary>
/// <remarks>
/// The ladder has six rungs but the handshake has more outcomes than that — <c>pending</c>,
/// <c>blocked</c> and <c>bad-signature</c> all sit on <see cref="DeviceState.NotAdopted"/>.
/// They are still three different things to a person standing in front of the frame, so the
/// rung is the <i>coarse</i> classification and this record carries the distinct wording that
/// goes on the screen. <see cref="Cause"/> keeps them separable in a test and in telemetry.
/// </remarks>
public sealed record DeviceCondition
{
    /// <summary>Which rung of §2.6's ladder this is.</summary>
    public required DeviceState State { get; init; }

    /// <summary>The handshake status, or a synthetic cause, that produced this condition.</summary>
    public required string Cause { get; init; }

    /// <summary>One line for a reader with no computer experience.</summary>
    public required string Headline { get; init; }

    /// <summary>A second line saying why it matters.</summary>
    public required string Detail { get; init; }

    /// <summary>Whether the product app may run in this condition.</summary>
    public required bool ProductRuns { get; init; }

    /// <summary>
    /// Whether the server answered, as opposed to saying nothing.
    /// </summary>
    /// <remarks>
    /// §2.6's load-bearing distinction: <b>rejection is an answer, silence is not</b>. Only an
    /// authoritative condition may stop the product; an outage in the operator's house must
    /// never blank a frame in someone else's.
    /// </remarks>
    public required bool IsAuthoritative { get; init; }

    /// <summary>Verbatim elaboration from the server, when it sent one.</summary>
    public string? ServerMessage { get; init; }
}

/// <summary>Maps handshake outcomes and silence onto the ladder.</summary>
public static class DeviceStateLadder
{
    /// <summary>Cause value for a connection that never produced an answer.</summary>
    public const string SilenceCause = "silence";

    /// <summary>Cause value for a status this agent build has never heard of.</summary>
    public const string UnknownStatusCause = "unknown-status";

    /// <summary>Cause value for the state before the first connection attempt completes.</summary>
    public const string StartingCause = "starting";

    /// <summary>The condition the agent holds before it has spoken to anything.</summary>
    public static DeviceCondition Starting { get; } = new()
    {
        State = DeviceState.NoContact,
        Cause = StartingCause,
        Headline = "Starting up",
        Detail = "Looking for the Fleet Manager this frame belongs to.",
        ProductRuns = false,
        IsAuthoritative = false,
    };

    /// <summary>Resolves the ladder rung for a completed handshake.</summary>
    public static DeviceCondition FromHandshake(HandshakeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            HandshakeStatus.Ok => new DeviceCondition
            {
                State = DeviceState.InSync,
                Cause = result.Status,
                Headline = "Everything is working",
                Detail = "This frame is adopted, up to date and showing your photos.",
                ProductRuns = true,
                IsAuthoritative = true,
                ServerMessage = result.Message,
            },
            HandshakeStatus.Pending => new DeviceCondition
            {
                State = DeviceState.NotAdopted,
                Cause = result.Status,
                Headline = "This device is healthy — adopt it in your Fleet Manager",
                Detail = "It is waiting for someone to press Adopt. Match the code below to the row on screen.",
                ProductRuns = false,
                IsAuthoritative = true,
                ServerMessage = result.Message,
            },
            HandshakeStatus.Blocked => new DeviceCondition
            {
                State = DeviceState.NotAdopted,
                Cause = result.Status,
                Headline = "This device has been blocked",
                Detail = "Someone chose Block for this frame in the Fleet Manager. Unblock it there to continue.",
                ProductRuns = false,
                IsAuthoritative = true,
                ServerMessage = result.Message,
            },
            HandshakeStatus.NotConfigured => new DeviceCondition
            {
                State = DeviceState.ControlNotConfigured,
                Cause = result.Status,
                Headline = "Connected to a Fleet Manager, but it is not set up yet",
                Detail = "The server answered, but nobody has finished setting it up. Open it in a browser.",
                ProductRuns = false,
                IsAuthoritative = true,
                ServerMessage = result.Message,
            },
            HandshakeStatus.VersionMismatch => new DeviceCondition
            {
                State = DeviceState.VersionMismatch,
                Cause = result.Status,
                Headline = "Updating this frame's software",
                Detail = $"The Fleet Manager runs a different version ({result.ServedAgentVersion ?? "unknown"}). Fetching it now.",
                ProductRuns = false,
                IsAuthoritative = true,
                ServerMessage = result.Message,
            },
            HandshakeStatus.BadSignature => new DeviceCondition
            {
                State = DeviceState.NotAdopted,
                Cause = result.Status,
                Headline = "This frame's identity was refused",
                Detail = "The Fleet Manager did not accept this frame's key. It has to be adopted again.",
                ProductRuns = false,
                IsAuthoritative = true,
                ServerMessage = result.Message,
            },

            // A status this build has never heard of can only come from a server newer than
            // this agent, so the answer is the same as any other version skew: update, and say
            // exactly what was not understood rather than collapsing it into a generic error.
            _ => new DeviceCondition
            {
                State = DeviceState.VersionMismatch,
                Cause = UnknownStatusCause,
                Headline = "Updating this frame's software",
                Detail = $"The Fleet Manager answered '{result.Status}', which this version does not understand.",
                ProductRuns = false,
                IsAuthoritative = true,
                ServerMessage = result.Message,
            },
        };
    }

    /// <summary>
    /// Resolves the rung for a Fleet Manager that said nothing at all.
    /// </summary>
    /// <param name="lastAuthoritative">
    /// The last condition the server actually answered with, or <see langword="null"/> if it
    /// never has.
    /// </param>
    /// <param name="reason">What went wrong on this attempt, for the screen.</param>
    /// <remarks>
    /// §2.6, verbatim: an unreachable server does not stop the product — <i>provided the frame
    /// was fully green when contact dropped</i>. A frame that was pending, blocked or mid-update
    /// when the server vanished has never been cleared to show anything, so silence leaves it
    /// exactly where it was.
    /// </remarks>
    public static DeviceCondition NoContact(DeviceCondition? lastAuthoritative, string reason)
    {
        var wasFullyGreen = lastAuthoritative is { State: DeviceState.InSync };

        return new DeviceCondition
        {
            State = DeviceState.NoContact,
            Cause = SilenceCause,
            Headline = wasFullyGreen
                ? "Still showing your photos — the Fleet Manager is unreachable"
                : "Cannot reach the Fleet Manager",
            Detail = wasFullyGreen
                ? "Everything on this frame was working when contact was lost, so it carries on."
                : "This frame has not been told it is ready, so it waits here until the server answers.",
            ProductRuns = wasFullyGreen,
            IsAuthoritative = false,
            ServerMessage = reason,
        };
    }
}
