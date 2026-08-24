using System.Globalization;
using FrameLink.Protocol;

namespace FrameLink.Control.Firmware;

/// <summary>What the console says a frame's firmware write is doing.</summary>
/// <remarks>
/// <para>
/// <b>Seven values, and the seventh is the one this used to say could never exist.</b> The note
/// here read <i>there is no <c>writing</c>: the agent emits nothing at all between the household
/// agreeing and <c>dfu-util</c> returning</i>, and the diagnosis was right while it was true — a
/// write in progress and a frame that died mid-write were the same picture from a desk. What
/// changed is the premise: the agent now parses <c>dfu-util</c>'s output as it arrives and folds
/// the stage, the percentage and the byte count into its self-report
/// (<see cref="ArrayFlashWire"/>), so <see cref="Writing"/> is read off something the frame
/// actually said rather than inferred from silence.
/// </para>
/// <para>
/// <b>The rule the old note was protecting is untouched, and it is what bounds the new value.</b>
/// <see cref="Writing"/> is set only from a live self-report on a frame this server currently holds
/// a socket to; a frame that went quiet mid-write reverts to what its last <i>event</i> said, which
/// is the honest answer, because the thing that stopped might be the frame. Nothing here is
/// inferred from a timeout and nothing is inferred from an authorisation being old.
/// </para>
/// <para>
/// Every other value is derived from an <c>array-flash</c> event the agent actually sent, or from
/// the presence of an authorisation this server actually holds.
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

    /// <summary>
    /// <c>dfu-util</c> is running on the frame right now, and the frame is saying how far it has got.
    /// </summary>
    /// <remarks>
    /// The one phase in this set that comes from a live self-report rather than from an event, and
    /// therefore the one that can only ever be shown for a frame that is online. It carries a stage
    /// and, while bytes are moving, a percentage and a byte count — see
    /// <see cref="ArrayFlashReading.Progress"/>.
    /// </remarks>
    public const string Writing = "writing";

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
    /// How far a write running <i>right now</i> has got, or null when none is.
    /// </summary>
    /// <remarks>
    /// Read out of the frame's live self-report rather than out of an event, which is why it is only
    /// ever set for a frame this server currently holds a socket to. A frame that went silent
    /// mid-write leaves this null and the reading falls back to its last event, because a bar frozen
    /// at 41% for an hour asserts that a write is still running, and the thing that stopped might be
    /// the frame.
    /// </remarks>
    public ArrayFlashWireStatus? Progress { get; init; }

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
    /// <param name="live">
    /// What the frame's own self-report says it is doing at this instant, or null when it is offline
    /// or has said nothing. Only a <c>writing</c> screen is acted on: everything else the frame can
    /// be doing already has an event behind it carrying the frame's own sentence, and a live screen
    /// name is a poorer version of the same fact.
    /// </param>
    public static ArrayFlashReading From(
        IEnumerable<DeviceEvent> events,
        string? authorisation,
        ArrayFlashWireStatus? live = null)
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

        // <b>A live write outranks every event, and only a live write does.</b> It is the one state
        // this screen used to be structurally blind to — for the thirty seconds to two minutes a
        // write takes, the newest event was whatever came before it — and it is the only one whose
        // newest fact is on a socket rather than in the trail. Everything else the frame can be
        // doing already has an event carrying the frame's own words, and those are better than a
        // screen name.
        if (live is { } running && string.Equals(running.Screen, ArrayFlashPhases.Writing, StringComparison.Ordinal))
        {
            return reading with
            {
                Phase = ArrayFlashPhases.Writing,
                Detail = Describe(running),
                Progress = running,
            };
        }

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

    /// <summary>
    /// One sentence for a write in flight, assembled from the numbers the frame sent.
    /// </summary>
    /// <remarks>
    /// <b>The one detail in this class the frame did not word itself, and only because it did not
    /// word this one.</b> Everywhere else the agent's own sentence is carried through untouched;
    /// a write in flight arrives as a stage name and two integers, so somebody has to turn those
    /// into a line, and this server is the only party that can. The frame's plain-language wording
    /// of the same state lives on its own panel, where it is read by a family member; this is
    /// written for an operator and says what the operator would otherwise have to work out from the
    /// raw token.
    /// </remarks>
    public static string Describe(ArrayFlashWireStatus live)
    {
        ArgumentNullException.ThrowIfNull(live);

        var stage = live.Stage switch
        {
            ArrayFlashStages.Preparing => "getting the microphone unit ready",
            ArrayFlashStages.Downloading => "sending the image to the unit",
            ArrayFlashStages.Manifesting => "the unit is committing the image to its own flash",
            ArrayFlashStages.Settling => "the unit has finished committing the image",
            ArrayFlashStages.Resetting => "resetting the unit",
            ArrayFlashStages.ReEnumerating => "waiting for the unit to come back on the USB bus",
            ArrayFlashStages.Verifying => "reading the version back from the unit",

            // A frame running a newer agent may name a stage this build has never heard of. Its own
            // word is shown, exactly as an unrecognised refusal token is.
            { Length: > 0 } named => named,
            _ => "writing firmware",
        };

        var detail = new System.Text.StringBuilder("This frame is writing firmware to its microphone unit now — ")
            .Append(stage);

        if (live.Percent is { } percent)
        {
            detail.Append(CultureInfo.InvariantCulture, $", {percent}%");
        }

        if (live.BytesWritten is { } written)
        {
            detail.Append(
                live.BytesTotal is { } total
                    ? string.Create(CultureInfo.InvariantCulture, $" ({written:N0} of {total:N0} bytes)")
                    : string.Create(CultureInfo.InvariantCulture, $" ({written:N0} bytes)"));
        }

        if (live.ElapsedSeconds is { } seconds)
        {
            detail.Append(CultureInfo.InvariantCulture, $", {seconds}s in");
        }

        return detail
            .Append(". Nothing may interrupt it: losing power during a write can leave the unit unusable.")
            .ToString();
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
