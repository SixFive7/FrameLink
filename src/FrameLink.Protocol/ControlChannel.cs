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
    /// Server to agent. <b>Switch this frame off</b> — §2.5 rung 5's other button, pressed from the
    /// Fleet Manager (decision 94).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A kind of its own rather than a flag on <see cref="RetryRequest"/>, and the reason is
    /// what an older agent does with each.</b> <see cref="RetryRequest.Reboot"/> could be added as a
    /// member because an agent that did not understand it still did the useful half: it cleared the
    /// budget and tried again, simply without restarting first. A shutdown has no useful half. An
    /// agent that ignored a <c>Shutdown</c> member would read the rest of the message and perform a
    /// <i>retry</i> — clearing budgets and reconciling on a frame whose operator had just asked for
    /// it to be off. Ignoring an unknown kind does nothing at all, and doing nothing is the only
    /// safe way to misunderstand this message.
    /// </para>
    /// <para>
    /// <b>It is also not a retry in any other sense.</b> No budget is touched: a frame that is
    /// switched off has not been told to try again, and clearing the ledger here would mean a
    /// household that decided to stop found the frame mid-provision when it was next switched on.
    /// And it is not conditional on anything having gone wrong — the other three routes are offered
    /// against a frame that has stopped, and an off switch that only worked on broken frames would
    /// be no off switch at all.
    /// </para>
    /// <para>
    /// <b>Only ever sent down a live socket, and never queued.</b> The Fleet Manager answers 409
    /// when the frame is not holding one, for a sharper version of the retry's reason: a frame that
    /// cannot be reached is either already off or has lost its network, nothing here can tell which,
    /// and a shutdown delivered hours later would switch off a frame somebody had since started
    /// using.
    /// </para>
    /// <para>
    /// <b>The frame may refuse it, and that is deliberate.</b> A firmware write in flight turns it
    /// down, because mains loss in the middle of one destroys the microphone unit and a remote
    /// shutdown is that hazard arriving somewhere software can still block it. The refusal is
    /// recorded on the frame with what to wait for; nothing is queued behind it.
    /// </para>
    /// </remarks>
    public const string KindShutdown = "shutdown";

    /// <summary>
    /// Server to agent. Who to contact when a frame has given up (§2.7 item 8, decision 71).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third exercise of the growth rule this class documents. Two more keys in
    /// <see cref="SettingsPush"/> would have carried the same two strings with no new kind at all,
    /// and the reason not to is what the settings map <i>is</i>: the dictionary §2.2 hands to
    /// resources as values to converge on. A person's telephone number is not one, and keeping it
    /// typed and separate is what stops <c>FleetValues</c> serving a human contact detail to a
    /// reconciler.
    /// </para>
    /// <para>
    /// <b>Only ever sent to an adopted device</b>, so §3.3's "a pending device receives nothing"
    /// stands unchanged. The first shape of this sent it on the pending path too, reasoning that a
    /// frame nobody has adopted is exactly the frame whose person has nobody to ask — and it was
    /// wrong: §3.3's registration endpoint is open to the internet and answers anything that
    /// connects with <c>pending</c>, so that shape would have published the operator's name and
    /// telephone number to every anonymous caller who found the URL.
    /// </para>
    /// </remarks>
    public const string KindOperatorContact = "operator-contact";

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

    /// <summary>
    /// Agent to server, on <see cref="ProtocolConstants.ChannelTelemetry"/>. The agent's own
    /// free-text self-report, when it has changed since the hello carried it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same string, the same vocabulary and the same stored field as
    /// <see cref="HandshakeHello.AgentStatus"/>.</b> The handshake is the designed carrier of the
    /// self-report and stays it; what it cannot do is speak twice. §4.2 puts a handshake on every
    /// connect, and a converged frame does not reconnect — it holds one session for as long as it
    /// is up, measured at over an hour on the development frame — so a value that travelled only
    /// in the hello was pinned to whatever the loop happened to be doing in the seconds after the
    /// last reboot, which is never the answer an operator needs. This carries the change, so the
    /// field the Fleet Manager renders is the one the frame currently means.
    /// </para>
    /// <para>
    /// <b>Deliberately not buffered when the frame is offline</b>, unlike everything else the
    /// agent sends (§4.1). A self-report is the current picture rather than history, and the next
    /// hello carries it, so a buffered one could only ever arrive stale behind a fresher one
    /// saying the same thing or better.
    /// </para>
    /// <para>
    /// The fourth exercise of the growth rule this class documents: a new <c>Kind</c> and a new
    /// payload shape, with the frozen envelope and the four handshake payloads untouched (§4.2).
    /// An older server ignores the kind and an older agent never sends it; neither is a broken
    /// socket, and in both cases the hello's value stands exactly as it does today.
    /// </para>
    /// </remarks>
    public const string KindAgentStatus = "agent-status";

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
/// The agent's self-report, re-sent mid-session because it changed —
/// <see cref="ControlWire.KindAgentStatus"/>. <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// Two fields, and there is no third on purpose. Anything the operator needs beyond the sentence
/// itself — which resource, how many attempts, when the next one is — is already
/// <see cref="ReconcileReport"/>'s, arriving on the same channel from the same pass, and a second
/// copy here would be a second thing to keep true.
/// </remarks>
public sealed record AgentStatusUpdate
{
    /// <summary>The frame this is about.</summary>
    /// <remarks>
    /// Carried like every other agent-to-server payload's, and trusted like none of them: the
    /// server binds what it stores to the id the socket proved, never to the id in the body.
    /// </remarks>
    public required string DeviceId { get; init; }

    /// <summary>
    /// The self-report, in the shape <see cref="AgentHealth.Describe"/> composes and
    /// <see cref="AgentHealth.Classify"/> reads.
    /// </summary>
    public required string Status { get; init; }
}

/// <summary>
/// The operator pressing <b>retry</b> on a resource that has given up — §2.5 rung 3.
/// <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Per resource, with "everything that gave up" as a form of the same command.</b> §2.5's retry
/// is offered against a status, and a status belongs to a resource — so the resource is what the
/// operator points at. But rung 4 stops the whole <i>frame</i>, and a stopped frame can have
/// several resources that gave up: the agent's report deliberately lists every one of them,
/// because clearing one while another remains would otherwise look like it had done nothing. A
/// null or empty <see cref="Resource"/> is that second case, and it is the same verb rather than a
/// second one.
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

    /// <summary>
    /// Whether the frame should <b>restart</b> after the budget is reset — the Fleet Manager's half
    /// of the stopped screen's second button.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The operator's specification, verbatim: "the reboot can also be triggered from the fleet
    /// manager given the agent is connected".</b> It is a field on this message rather than a kind
    /// of its own because it is the same command with a stronger ending — the reset is identical,
    /// the resource selection is identical, and what differs is only whether the frame comes back
    /// from a known state or waits for its next ordinary pass.
    /// </para>
    /// <para>
    /// <b>An older agent ignores it and performs the plain retry</b>, which is the correct
    /// degradation: the budget is still cleared and the frame still tries again, it simply does not
    /// restart first. That is the growth rule this class documents working as intended — a member
    /// added, nothing removed, renamed or retyped.
    /// </para>
    /// <para>
    /// <b>"Given the agent is connected" is not a property of this field.</b> It is a property of
    /// the transport: the Fleet Manager sends this down an open socket or not at all, and answers
    /// the operator 409 when there is none. Nothing replays it on reconnect, for the same reason
    /// nothing replays a retry — a restart delivered hours later to a frame whose situation has
    /// moved on is worse than one refused now.
    /// </para>
    /// </remarks>
    public bool Reboot { get; init; }
}

/// <summary>
/// <b>Switch this frame off</b> — the payload of <see cref="ControlWire.KindShutdown"/>
/// (decision 94). <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Two fields, and neither of them is an option.</b> There is nothing to parameterise: a frame
/// is off or it is not. Anything this record grew — a delay, a reason code, a "come back at" — would
/// be a promise about a machine that by then has no software running on it to keep it.
/// </para>
/// <para>
/// <b>The operator is named, and it travels rather than being inferred.</b> The frame writes who
/// asked into its own journal, and that journal is the only record that survives on a machine
/// nothing can reach afterwards. A shutdown attributed to nobody would be indistinguishable, on the
/// next boot, from a household that pulled the plug.
/// </para>
/// </remarks>
public sealed record ShutdownRequest
{
    /// <summary>Device the operator was looking at, so a misrouted frame ignores it.</summary>
    /// <remarks>
    /// Checked by the agent rather than trusted, for the same reason the retry checks it and with a
    /// worse consequence if it is wrong: the cost of being mistaken here is an unrelated frame
    /// switching itself off, in somebody's house, with nothing able to reach it afterwards.
    /// </remarks>
    public required string DeviceId { get; init; }

    /// <summary>When the operator asked, for the agent's log.</summary>
    public required DateTimeOffset RequestedUtc { get; init; }
}

/// <summary>
/// Who to contact about this fleet — §2.7 item 8, decision 71. <b>Frozen once shipped.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Two strings and a timestamp, and the shortness is the design.</b> It is not an address book
/// and it never grows into one: what a person standing in front of a stopped frame needs is a name
/// to say and one way to reach them, and every field beyond that is a field the frame would have
/// to lay out on a screen it is already using to explain a failure.
/// </para>
/// <para>
/// <b>Both fields may be empty, and empty is a real answer.</b> An operator who has configured
/// neither gets a frame that says so plainly rather than one that invents a support address, which
/// is the same discipline §2.6 applies to silence from the server.
/// </para>
/// </remarks>
public sealed record OperatorContact
{
    /// <summary>Who to ask for, as a person would say it. May be empty.</summary>
    public string? Name { get; init; }

    /// <summary>How to reach them — a phone number, an address, a room. May be empty.</summary>
    public string? Contact { get; init; }

    /// <summary>When the Fleet Manager last resolved these values.</summary>
    public required DateTimeOffset UpdatedUtc { get; init; }
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
