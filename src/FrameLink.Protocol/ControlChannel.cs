using System.Text.Json;

namespace FrameLink.Protocol;

/// <summary>
/// The kinds and payloads layered on the <c>control</c> channel of §4.1.
/// </summary>
/// <remarks>
/// <para>
/// These are not part of the §4.2 freeze that covers <see cref="WireEnvelope"/> and the four
/// handshake payloads — they are the first exercise of the mechanism that freeze document
/// describes: <b>a protocol version grows by adding new <see cref="WireEnvelope.Kind"/> values
/// and new payload shapes</b>. Adding them here changes nothing about the envelope.
/// </para>
/// <para>
/// They live in this project because the alternative was tried and failed. Both programs
/// carried a private copy — <c>FrameLink.Control.ControlWire</c> and
/// <c>FrameLink.Agent.Link.ControlChannel</c> — on the reasoning that the agent must not
/// reference the server assembly, since a frame would then be carrying SQLite and ASP.NET
/// inside a binary §2.1 requires to be one self-contained ELF. That reasoning is sound about
/// the <i>server</i> and wrong about the <i>contract</i>: this project has no dependencies at
/// all, so it is a home both programs can share, and the duplication bought nothing but the
/// opportunity to drift. It very nearly did: the agent ignored <c>ping</c> entirely while both
/// suites were green, and every real connection would have died on the server's missed-pong
/// deadline.
/// </para>
/// <para>
/// <b>Frozen once shipped.</b> A member here may be added, never removed, renamed or retyped —
/// the same discipline as the handshake, for the same reason: an agent that cannot update
/// itself must stay legible. A genuinely different shape gets a new <c>Kind</c>, not an edit.
/// </para>
/// </remarks>
public static class ControlWire
{
    /// <summary>Server to agent. Must be answered with <see cref="KindPong"/>.</summary>
    /// <remarks>
    /// Answering is not optional and not best-effort. §3.5 gives the server a missed-pong
    /// deadline precisely because a pulled plug leaves a half-open TCP connection that accepts
    /// writes forever; an agent that stays silent is indistinguishable from that frame and is
    /// disconnected as one.
    /// </remarks>
    public const string KindPing = "ping";

    /// <summary>Agent to server. The answer to <see cref="KindPing"/>.</summary>
    public const string KindPong = "pong";

    /// <summary>Server to agent. Effective settings for an adopted device (§3.4).</summary>
    public const string KindSettings = "settings";

    /// <summary>
    /// Server to agent. The operator's <b>retry</b> of §2.5 rung 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It has to be a message, and that is a finding rather than a design choice.</b> §2.5 rung
    /// 3 offers the operator two actions — retry, or open a remote shell — and the first of them
    /// had no implementation at all: <c>ReconcileLoop.ResetBudget</c> existed and was reachable
    /// only from tests, the Fleet Manager had no route, and the reconcile screen rendered
    /// escalations read-only. An escalated frame was stuck with no way back short of a re-flash.
    /// </para>
    /// <para>
    /// A restart is not the way back either. The attempt ledger is deliberately durable — it lives
    /// in <c>/var/lib/fl-agent/reconcile-journal.json</c>, which §2.1 keeps across an update and
    /// §2.4 needs across the reboot every resource takes, because "an attempt counter that reset on
    /// every boot could never exhaust a budget". So the budget survives exactly what an operator
    /// would reach for, and only something arriving from outside the frame can clear it.
    /// </para>
    /// <para>
    /// The second exercise of the growth rule this class documents: a new <c>Kind</c> and a new
    /// payload shape, with the frozen envelope and the four handshake payloads untouched (§4.2). An
    /// older agent ignores the kind — the agent's inbound dispatch skips what it does
    /// not know — so an operator pressing retry against a frame running an older build gets
    /// nothing rather than a broken socket.
    /// </para>
    /// </remarks>
    public const string KindRetry = "retry";

    /// <summary>
    /// Agent to server, on <see cref="ProtocolConstants.ChannelTelemetry"/>. The whole loop
    /// state and the per-resource status list (§3.5).
    /// </summary>
    public const string KindReconcileReport = "reconcile-report";

    /// <summary>
    /// Agent to server, on <see cref="ProtocolConstants.ChannelEvents"/>. Drift, escalation and
    /// boot (§4.1).
    /// </summary>
    public const string KindDeviceEvent = "device-event";

    /// <summary>
    /// Agent to server, on <see cref="ProtocolConstants.ChannelTelemetry"/>. Every installed
    /// package and its version, sent only when the set has changed.
    /// </summary>
    /// <remarks>
    /// The first exercise of the growth rule this class documents: a new <c>Kind</c> and a new
    /// payload shape (<see cref="PackageInventory"/>), with nothing above it moved. An older
    /// server ignores the kind and an older agent never sends it; neither case is a broken
    /// socket, which is the whole point of the envelope being frozen and the vocabulary not.
    /// </remarks>
    public const string KindPackageInventory = "package-inventory";

    /// <summary>Property name carrying the ping's sequence number on the wire.</summary>
    private const string SequenceProperty = "sequence";

    /// <summary>
    /// Reads the sequence number out of a ping, without requiring the payload to parse.
    /// </summary>
    /// <remarks>
    /// A ping whose sequence cannot be read is still answered, with zero. The server's deadline
    /// is refreshed by <i>any</i> inbound traffic, so staying silent over one unreadable field
    /// would drop a working connection — the exact failure this whole exchange exists to
    /// detect. Deserialising <see cref="AgentPing"/> would be the obvious alternative and is
    /// strictly worse here: a newer server that made a field required, or sent a timestamp in a
    /// shape this build cannot parse, would produce silence instead of a pong.
    /// </remarks>
    public static long SequenceOf(WireEnvelope ping)
    {
        ArgumentNullException.ThrowIfNull(ping);

        return ping.Payload.ValueKind is JsonValueKind.Object
            && ping.Payload.TryGetProperty(SequenceProperty, out var value)
            && value.TryGetInt64(out var parsed)
                ? parsed
                : 0;
    }
}

/// <summary>
/// Server-to-agent liveness probe (§3.5). <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// An application-level ping rather than a WebSocket control frame, because the thing that
/// has to be observable is the <i>answer</i>. A pulled plug leaves a half-open TCP connection
/// that accepts writes forever; only a reply within a deadline proves the frame is still
/// there, and only an application-level exchange gives the deadline somewhere to live.
/// </remarks>
public sealed record AgentPing
{
    /// <summary>Monotonic per-connection counter, echoed back in the pong.</summary>
    public required long Sequence { get; init; }

    /// <summary>When the server sent it.</summary>
    public required DateTimeOffset SentUtc { get; init; }
}

/// <summary>Agent-to-server answer to <see cref="AgentPing"/>. <b>Frozen once shipped.</b></summary>
public sealed record AgentPong
{
    /// <summary>The sequence number from the ping being answered.</summary>
    public required long Sequence { get; init; }
}

/// <summary>
/// The operator pressing <b>retry</b> on an escalated or halted resource — §2.5 rung 3.
/// <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Per resource, with "everything that gave up" as a form of the same command.</b> §2.5's retry
/// is offered against a status, and a status belongs to a resource — so the resource is what the
/// operator points at. But rung 4 halts the <i>device</i>, and a halted device can have several
/// resources that gave up: the agent's halt report deliberately lists every one of
/// them, "because clearing one halt while another remains would otherwise look like it had done
/// nothing". A null or empty <see cref="Resource"/> is that second case, and it is the same verb
/// rather than a second one.
/// </para>
/// <para>
/// <b>It resets the budget; it does not force one attempt.</b> That is what §2.5 rung 3 says the
/// operator's retry does, and the difference matters: a transient that needs two attempts to clear
/// — an archive that is down, a device that is slow to appear — would defeat a single forced
/// attempt and leave the operator pressing a button in a loop with no ladder underneath it. The
/// escalation <i>count</i> is not reset, so a frame that has been given up on once halts again the
/// moment the fresh budget runs out rather than starting the ladder from the bottom. Both halves
/// of that are already the reconciler's documented behaviour; this only gives it a caller.
/// </para>
/// <para>
/// <b>Nothing is said about the resources Blocked behind it, on purpose.</b> Blocked is not a
/// persisted state — it is recomputed from this pass's statuses on every walk — and a blocked
/// resource has spent no attempt, so it has no budget to reset. Clearing the escalation is
/// therefore the whole of the fix: the dependents are simply attempted again the next time the
/// walk reaches them, which is the same pass.
/// </para>
/// </remarks>
public sealed record RetryRequest
{
    /// <summary>Device the operator was looking at, so a misrouted frame ignores it.</summary>
    /// <remarks>
    /// Checked by the agent rather than trusted. The server addresses one open socket, so a
    /// mismatch should be impossible — which is exactly why it is worth asserting: the cost of
    /// being wrong is an unrelated frame silently rebooting five more times.
    /// </remarks>
    public required string DeviceId { get; init; }

    /// <summary>
    /// The resource whose budget to reset, or null for every resource that has given up.
    /// </summary>
    public string? Resource { get; init; }

    /// <summary>When the operator asked, for the agent's log.</summary>
    public required DateTimeOffset RequestedUtc { get; init; }
}

/// <summary>
/// The effective settings pushed to an adopted device on connect and after any change (§3.4).
/// <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// Only ever sent to a device whose handshake answered <c>ok</c>. A pending device receives
/// nothing (§3.3), and configuration is the largest part of that nothing.
/// </remarks>
public sealed record SettingsPush
{
    /// <summary>Device the values were resolved for.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Settings revision, so the agent can ignore a repeat of what it already has.</summary>
    public required long Revision { get; init; }

    /// <summary>Fleet defaults with per-device overrides applied.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }
}
