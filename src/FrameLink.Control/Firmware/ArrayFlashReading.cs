using FrameLink.Protocol;

namespace FrameLink.Control.Firmware;

/// <summary>What the console says a frame's firmware write is doing.</summary>
/// <remarks>
/// <para>
/// <b>Six values, and the missing one is deliberate.</b> There is no <c>writing</c>: the agent
/// emits nothing at all between the household agreeing and <c>dfu-util</c> returning — the write's
/// only live surface is the frame's own panel, which is where the person who agreed to it is
/// standing. Inventing a <c>writing</c> phase here would mean the console asserting a state
/// nothing had told it, which is the one thing this project's telemetry is not allowed to do. The
/// write's <i>record</i> arrives complete, with the elapsed time and <c>dfu-util</c>'s output, the
/// moment it is over.
/// </para>
/// <para>
/// Every value is derived from an <c>array-flash</c> event the agent actually sent, or from the
/// presence of an authorisation this server actually holds. Nothing is inferred from a timeout.
/// </para>
/// </remarks>
public static class ArrayFlashPhases
{
    /// <summary>No authorisation is held and the frame has never reported a write. The ordinary state.</summary>
    public const string NotAuthorised = "not-authorised";

    /// <summary>An authorisation is held and the frame has not yet said anything about it.</summary>
    public const string Authorised = "authorised";

    /// <summary>The frame is asking somebody standing at it to agree, and waiting.</summary>
    public const string AwaitingHousehold = "awaiting-household";

    /// <summary>An interlock stopped it. <see cref="ArrayFlashReading.Refusal"/> says which.</summary>
    public const string Refused = "refused";

    /// <summary>A write happened and the array came back on the pinned firmware.</summary>
    public const string Flashed = "flashed";

    /// <summary>A write happened and the array did not come back on the pinned firmware.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One frame's firmware-write standing, read out of the <c>array-flash</c> and
/// <c>array-firmware</c> events it has sent.
/// </summary>
/// <remarks>
/// <para>
/// <b>The agent's own sentences are carried, never re-worded.</b> Each event's <c>Summary</c> was
/// written to be read by a person and states the thing the console would otherwise have to guess
/// at — which interlock fired, what the two firmware readings were, what a person has to go and
/// do. This class decides only <i>which</i> sentence is the current one and what colour it is.
/// </para>
/// <para>
/// <b>Refusals are classified from the agent's delta, which is machine-readable on purpose.</b>
/// <c>ArrayFirmwareFlash.RefuseAsync</c> writes
/// <c>expected 'a firmware write', observed 'refused: &lt;ArrayFlashRefusal&gt;'</c>, so the
/// interlock's name survives the trip. <c>ControlArrayFlashTests</c> runs the real agent against a
/// synthetic frame and feeds the events it genuinely produced through this reader, so the two
/// halves cannot drift apart without the suite saying so.
/// </para>
/// <para>
/// <b>An unrecognised token is shown, never swallowed.</b> A frame running a newer agent may refuse
/// for a reason this build has no name for; the name it sent is carried through as
/// <see cref="Refusal"/> and rendered as it arrived, exactly as the console does with an
/// unrecognised resource status.
/// </para>
/// </remarks>
public sealed record ArrayFlashReading
{
    /// <summary>The prefix the agent's refusal delta always opens with.</summary>
    public const string RefusalDeltaPrefix = "expected 'a firmware write', observed 'refused: ";

    /// <summary>The agent's refusal token for "nobody at the frame has agreed yet".</summary>
    public const string AwaitingLocalApproval = "AwaitingLocalApproval";

    /// <summary>How a write that produced the pinned firmware opens its summary.</summary>
    public const string WroteSummaryPrefix = "Wrote ";

    /// <summary>How a write that did not opens its summary.</summary>
    public const string TriedToWriteSummaryPrefix = "Tried to write ";

    /// <summary>One of <see cref="ArrayFlashPhases"/>.</summary>
    public required string Phase { get; init; }

    /// <summary>The sentence to show. The frame's own words whenever the frame has said any.</summary>
    public required string Detail { get; init; }

    /// <summary>Which interlock refused, verbatim from the frame, or null.</summary>
    public string? Refusal { get; init; }

    /// <summary>When the frame said it, or null when nothing has been said.</summary>
    public DateTimeOffset? ReportedUtc { get; init; }

    /// <summary>The newest <c>array-firmware</c> reading — which firmware this unit runs.</summary>
    public string? RunningFirmware { get; init; }

    /// <summary>When that reading was taken.</summary>
    public DateTimeOffset? RunningFirmwareUtc { get; init; }

    /// <summary>
    /// Reads a frame's standing from its events and whatever authorisation is held for it.
    /// </summary>
    /// <param name="events">The device's events, newest first, as the store returns them.</param>
    /// <param name="authorisation">The <c>audio.arrayFirmwareFlash</c> value in force, or null.</param>
    /// <remarks>
    /// <para>
    /// <b>An armed authorisation and a stale outcome are two facts, and this keeps them apart.</b>
    /// A frame flashed last month still has "Wrote …" as its newest <c>array-flash</c> event, so an
    /// authorisation armed today would otherwise read as a write that already succeeded. The
    /// composed authorisation carries the instant this server issued it, and an event older than
    /// that instant is history: the phase is then <see cref="ArrayFlashPhases.Authorised"/> —
    /// nothing has come back yet — while the old event stays in the list where it belongs.
    /// </para>
    /// <para>
    /// The comparison is one clock against another: <c>at=</c> is this server's and
    /// <c>OccurredUtc</c> is the frame's. A frame whose clock runs behind therefore holds "nothing
    /// has come back yet" for longer than it should, and one that runs ahead can show a stale
    /// outcome as current. Both are visible rather than silent — the event's own timestamp is
    /// rendered beside it — and both are better than the alternative, which is a schema change to
    /// the deliberately generic settings store so that one key can record its write time.
    /// </para>
    /// </remarks>
    public static ArrayFlashReading From(IEnumerable<DeviceEvent> events, string? authorisation)
    {
        ArgumentNullException.ThrowIfNull(events);

        DeviceEvent? flash = null;
        DeviceEvent? firmware = null;

        foreach (var moment in events)
        {
            if (flash is null && string.Equals(moment.Kind, DeviceEventKinds.ArrayFlash, StringComparison.Ordinal))
            {
                flash = moment;
            }
            else if (firmware is null
                && string.Equals(moment.Kind, DeviceEventKinds.ArrayFirmware, StringComparison.Ordinal))
            {
                firmware = moment;
            }

            if (flash is not null && firmware is not null)
            {
                break;
            }
        }

        var armed = !string.IsNullOrWhiteSpace(authorisation);
        var issued = armed ? ArrayFlashTicket.IssuedAt(authorisation!) : null;
        var stale = flash is not null && issued is { } at && flash.OccurredUtc < at;

        var reading = new ArrayFlashReading
        {
            Phase = ArrayFlashPhases.NotAuthorised,
            Detail = "No firmware write is authorised on this frame.",
            RunningFirmware = firmware?.Summary,
            RunningFirmwareUtc = firmware?.OccurredUtc,
        };

        if (flash is null || stale)
        {
            return armed
                ? reading with
                {
                    Phase = ArrayFlashPhases.Authorised,
                    Detail = "A firmware write is authorised on this frame. It looks for one about once a minute, "
                        + "and has not reported back about this authorisation yet.",
                }
                : reading;
        }

        var refusal = RefusalIn(flash.Delta);

        if (refusal is null)
        {
            return reading with
            {
                Phase = flash.Summary.StartsWith(TriedToWriteSummaryPrefix, StringComparison.Ordinal)
                    ? ArrayFlashPhases.Failed
                    : flash.Summary.StartsWith(WroteSummaryPrefix, StringComparison.Ordinal)
                        ? ArrayFlashPhases.Flashed
                        : armed ? ArrayFlashPhases.Authorised : ArrayFlashPhases.NotAuthorised,
                Detail = flash.Summary,
                ReportedUtc = flash.OccurredUtc,
            };
        }

        return reading with
        {
            Phase = string.Equals(refusal, AwaitingLocalApproval, StringComparison.Ordinal)
                ? ArrayFlashPhases.AwaitingHousehold
                : ArrayFlashPhases.Refused,
            Detail = flash.Summary,
            Refusal = refusal,
            ReportedUtc = flash.OccurredUtc,
        };
    }

    /// <summary>The interlock named in a refusal delta, or null when the delta is not one.</summary>
    public static string? RefusalIn(string? delta)
    {
        if (delta is null || !delta.StartsWith(RefusalDeltaPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var tail = delta[RefusalDeltaPrefix.Length..];
        var end = tail.IndexOf('\'');
        var name = (end < 0 ? tail : tail[..end]).Trim();

        return name.Length == 0 ? null : name;
    }
}
