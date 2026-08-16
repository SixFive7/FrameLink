namespace FrameLink.Control.Alerting;

/// <summary>How much a condition matters, and therefore how loudly it should arrive.</summary>
/// <remarks>
/// Two levels and no more. A three- or five-level scale invites a middle rung that nobody acts
/// on, and an alert nobody acts on is noise that trains an operator to ignore the channel — which
/// is the same outcome as having no alerting at all, arrived at more expensively.
/// </remarks>
public enum AlertSeverity
{
    /// <summary>Something is wrong and will get worse if it is left. Nothing has stopped yet.</summary>
    Warning,

    /// <summary>A capability the product exists to provide is not available right now.</summary>
    Critical,
}

/// <summary>The condition kinds this Fleet Manager watches for (§3.5).</summary>
/// <remarks>
/// <para>
/// <b>Four, and the shortness of the list is deliberate.</b> The failure this whole project was
/// started by — a LiveKit token minted with a 30-day default that expired unnoticed on
/// 2026-07-23, whose first symptom was a family finding out a frame could not call — is exactly
/// two failure classes: <i>something that was in contact went quiet</i>, and <i>something is
/// expiring</i>. Everything here is one of those two, or is the call path itself being down,
/// which is the one thing whose absence is invisible from every other surface.
/// </para>
/// <para>
/// What is deliberately <i>not</i> here: CPU, memory, disk, request rates, container restarts and
/// every other metric a monitoring stack would offer. All of it either already streams as
/// telemetry to the console (§3.5) or is somebody else's job, and none of it is a thing an
/// operator would be woken for. §3.5's sentence is "offline beyond a threshold is alertable",
/// not "observability".
/// </para>
/// </remarks>
public static class AlertKinds
{
    /// <summary>An adopted frame that was in contact has not been heard from since.</summary>
    public const string DeviceOffline = "device-offline";

    /// <summary>An adopted frame is holding a call token that runs out soon.</summary>
    public const string CallTokenExpiring = "call-token-expiring";

    /// <summary>The bundled call server is not answering, so no frame can place a call.</summary>
    public const string CallServerDown = "call-server-down";

    /// <summary>A frame has stopped reconciling and is waiting for a person (§2.5 rung 4).</summary>
    /// <remarks>
    /// Spelled <c>device-stopped</c> rather than <c>device-halted</c> since decision 66 removed
    /// <c>Halted</c>. An open row carrying the old key simply leaves the computed set on the next
    /// pass and is delivered as resolved, which is what level-triggered alerting does with any
    /// condition that stops being true.
    /// </remarks>
    public const string DeviceStopped = "device-stopped";
}

/// <summary>
/// One condition that is true right now and that an operator would want told about.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Key"/> is identity, and that is the whole of the de-duplication design.</b> An
/// evaluation pass produces the complete set of conditions currently true; the set is compared
/// against what is already open in the database; the difference is what gets delivered. So a
/// frame that has been offline for three weeks produces one notification rather than six thousand,
/// and no rule needs to remember anything about the last time it ran.
/// </para>
/// <para>
/// The record carries the rendered sentences rather than the raw numbers behind them, because the
/// thing on the other end of the webhook is a phone notification and not a query engine. A rule
/// that cannot say in one sentence what is wrong is a rule that should not fire.
/// </para>
/// </remarks>
public sealed record FleetAlert
{
    /// <summary>Stable identity of this condition. Same condition, same key, forever.</summary>
    public required string Key { get; init; }

    /// <summary>One of <see cref="AlertKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>How loudly it should arrive.</summary>
    public required AlertSeverity Severity { get; init; }

    /// <summary>One line, fit to be the title of a phone notification.</summary>
    public required string Subject { get; init; }

    /// <summary>The detail behind the subject, in plain sentences.</summary>
    public required string Detail { get; init; }

    /// <summary>The frame this is about, when it is about one.</summary>
    public string? DeviceId { get; init; }

    /// <summary>That frame's operator-assigned name, when it has one.</summary>
    public string? DeviceName { get; init; }
}

/// <summary>An alert that is currently open, with the times that matter about it.</summary>
/// <param name="Alert">The condition.</param>
/// <param name="OpenedUtc">When this Fleet Manager first observed it.</param>
/// <param name="NotifiedUtc">When it was successfully delivered, or null if delivery has not
/// succeeded yet — which is what makes an unreachable notification channel a retry rather than a
/// lost alert.</param>
public sealed record OpenAlert(FleetAlert Alert, DateTimeOffset OpenedUtc, DateTimeOffset? NotifiedUtc);

/// <summary>Whether a delivery is telling somebody a condition started or that it ended.</summary>
public enum AlertTransition
{
    /// <summary>The condition has just become true.</summary>
    Opened,

    /// <summary>The condition was true and is no longer.</summary>
    Cleared,
}

/// <summary>
/// One thing to deliver: a condition and which way it just moved.
/// </summary>
/// <remarks>
/// <b>Clearing is delivered, not merely recorded.</b> The 2026-07-23 post-mortem's real lesson is
/// that silence is ambiguous — a channel that only ever says "broken" leaves an operator unable to
/// tell a fixed fault from a dead alerter. So a resolution is a message in its own right, and a
/// channel that has said nothing for a month has genuinely had nothing to say.
/// </remarks>
public sealed record AlertNotification
{
    /// <summary>Opened or cleared.</summary>
    public required AlertTransition Transition { get; init; }

    /// <summary>The condition.</summary>
    public required FleetAlert Alert { get; init; }

    /// <summary>When the condition was first observed.</summary>
    public required DateTimeOffset OpenedUtc { get; init; }
}
