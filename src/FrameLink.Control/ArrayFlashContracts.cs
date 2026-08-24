using FrameLink.Protocol;

namespace FrameLink.Control;

/// <summary>
/// What the console is told about, and asks for, when a microphone array is to be flashed
/// (decision 91).
/// </summary>
/// <remarks>
/// Its own file beside <c>RetryContracts.cs</c>, for the reason given there: a feature this small
/// is legible when its handler, its contract and its state reader sit together, and unfindable
/// when it is four one-line additions to four shared files. The one line that could not live here
/// is the <c>[JsonSerializable]</c> registration — see <c>RetryContracts.cs</c> for why it has to
/// be in <c>ControlJson.cs</c> with the others.
/// </remarks>
public sealed record ArrayFlashTargetView
{
    /// <summary>The file name upstream publishes.</summary>
    public required string Name { get; init; }

    /// <summary>The firmware version it carries, in the array's own spelling.</summary>
    public required string Version { get; init; }

    /// <summary>Its SHA-256 — the whole of what an authorisation names.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Its exact length in bytes.</summary>
    public required long SizeBytes { get; init; }
}

/// <summary>The authorisation currently held for one frame.</summary>
/// <remarks>
/// <b>Shown in full, deliberately.</b> It is not a credential — it names a public digest, a device
/// id the operator is already looking at and a ticket this server composed, and it authorises
/// exactly one write on exactly one frame. What it <i>is</i> is the audit record, so an operator
/// diagnosing a refusal has to be able to read the same string the frame read.
/// </remarks>
public sealed record ArrayFlashAuthorisationView
{
    /// <summary>The value in <c>audio.arrayFirmwareFlash</c>, verbatim.</summary>
    public required string Value { get; init; }

    /// <summary>Everything after the first colon.</summary>
    public required string Ticket { get; init; }

    /// <summary>Whether it bypasses the local approval on this frame.</summary>
    public required bool Unattended { get; init; }

    /// <summary>Whether it names the image this build knows about.</summary>
    /// <remarks>
    /// False for a hand-written value naming some other digest, which the frame will refuse with
    /// <c>NotThePinnedImage</c>. Saying so here is what stops that refusal being a surprise.
    /// </remarks>
    public required bool NamesTheTarget { get; init; }

    /// <summary>When this server composed it, or null when something else wrote the value.</summary>
    public DateTimeOffset? IssuedUtc { get; init; }

    /// <summary>The operator's own words from the ticket, when it carries any.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The device id a bypass inside it names, when it carries one.
    /// </summary>
    /// <remarks>
    /// Present even when it is <i>not</i> this frame. A bypass naming another device is ignored by
    /// the frame that reads it — it asks its household exactly as it would have — and an operator
    /// who pushed one fleet-wide by hand needs to see which frame it actually applies to.
    /// </remarks>
    public string? UnattendedDeviceId { get; init; }
}

/// <summary>
/// How far a write that is running <i>right now</i> has got, as the frame itself reports it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The console draws the bar; it does not decide what the bar means.</b> Every field here is a
/// number the frame sent — the stage it is at, <c>dfu-util</c>'s own printed percentage, the bytes
/// it says it has sent against the pinned image's length, and how long the write has been running.
/// <see cref="Fraction"/> is the one derived value and it is derived on the server so that the two
/// surfaces that draw this bar, the frame's own screen and this console, fill it to the same place.
/// </para>
/// <para>
/// <b>Null <see cref="Fraction"/> is a value with a meaning.</b> Only the download stage has a
/// quantity behind it; the unit committing the image to its flash, resetting and coming back on the
/// USB bus is tens of seconds with nothing to measure. A console that drew an empty bar through
/// those would say the write had gone backwards and one that drew a full bar would say it had
/// finished, so the contract is that a null fraction is drawn as an indeterminate bar with the
/// stage named beside it.
/// </para>
/// </remarks>
public sealed record ArrayFlashProgressView
{
    /// <summary>The stage, in the agent's own spelling. Shown as sent when unrecognised.</summary>
    public required string Stage { get; init; }

    /// <summary><c>dfu-util</c>'s own printed percentage, or null.</summary>
    public int? Percent { get; init; }

    /// <summary>Bytes the tool says it has sent, or null.</summary>
    public long? BytesWritten { get; init; }

    /// <summary>The pinned image's length in bytes, or null.</summary>
    public long? BytesTotal { get; init; }

    /// <summary>How long the write has been running, in whole seconds.</summary>
    public int? ElapsedSeconds { get; init; }

    /// <summary>How full to draw the bar, from 0 to 1, or null when nothing is measurable.</summary>
    public double? Fraction { get; init; }
}

/// <summary>Everything the console renders about one frame's firmware write.</summary>
public sealed record ArrayFlashStatusResponse
{
    /// <summary>The frame this is about.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Whether it is adopted. Only an adopted frame can hold an authorisation.</summary>
    public required bool Adopted { get; init; }

    /// <summary>Whether a socket is open to it right now.</summary>
    public required bool Online { get; init; }

    /// <summary>The image the fleet converges on.</summary>
    public required ArrayFlashTargetView Target { get; init; }

    /// <summary>The bypass token's prefix, so the console can show the exact word it will write.</summary>
    public required string UnattendedPrefix { get; init; }

    /// <summary>
    /// The warnings an operator accepts by taking the bypass, in the frame's own words.
    /// </summary>
    /// <remarks>
    /// Served rather than written into the GUI bundle so that the sentences an operator reads, the
    /// sentences the frame puts on its own screen and the sentences the audit event carries are one
    /// set of sentences with one owner.
    /// </remarks>
    public required IReadOnlyList<string> UnattendedWarning { get; init; }

    /// <summary>The authorisation in force, or null when there is none.</summary>
    public ArrayFlashAuthorisationView? Authorisation { get; init; }

    /// <summary>One of <c>ArrayFlashPhases</c>.</summary>
    public required string Phase { get; init; }

    /// <summary>The sentence to show — the frame's own whenever the frame has said one.</summary>
    public required string Detail { get; init; }

    /// <summary>Which interlock refused, verbatim from the frame, or null.</summary>
    public string? Refusal { get; init; }

    /// <summary>
    /// How far a write in flight has got, or null when none is running on this frame right now.
    /// </summary>
    /// <remarks>
    /// Non-null only while <see cref="Phase"/> is <c>writing</c>, which is only ever set from a live
    /// self-report on a frame this server currently holds a socket to. A frame that went quiet
    /// mid-write leaves this null rather than freezing a bar at whatever it last said, because a
    /// stationary bar asserts that a write is still running and the thing that stopped might be the
    /// frame.
    /// </remarks>
    public ArrayFlashProgressView? Progress { get; init; }

    /// <summary>When the frame said it.</summary>
    public DateTimeOffset? ReportedUtc { get; init; }

    /// <summary>The newest reading of which firmware this frame's array is running.</summary>
    public string? RunningFirmware { get; init; }

    /// <summary>When that reading was taken.</summary>
    public DateTimeOffset? RunningFirmwareUtc { get; init; }

    /// <summary>
    /// This frame's <c>array-flash</c> and <c>array-firmware</c> events, newest first.
    /// </summary>
    /// <remarks>
    /// The trail, filtered to the two kinds this screen is about. A refusal is in it for the same
    /// reason a write is: <i>which interlock stopped this frame</i> is as much a part of the record
    /// as a write that happened.
    /// </remarks>
    public required IReadOnlyList<DeviceEvent> Events { get; init; }
}

/// <summary>The operator asking for one write on one frame.</summary>
/// <remarks>
/// <para>
/// <b>It carries no device id and no digest, and that is the point.</b> The frame is the one in the
/// route, and the image is the one this build pins. Nothing an operator can type reaches either,
/// so no typo can produce an authorisation that names a frame they are not looking at.
/// </para>
/// <para>
/// <b>The bypass is two fields rather than one.</b> <see cref="Unattended"/> is the choice and
/// <see cref="Acknowledged"/> is the acceptance, and the server refuses the first without the
/// second. One boolean would have made the most dangerous operation in the product the same
/// keystroke as the safe one.
/// </para>
/// </remarks>
public sealed record ArrayFlashRequest
{
    /// <summary>Whether to skip the approval on the frame's own screen.</summary>
    public bool Unattended { get; init; }

    /// <summary>
    /// Whether the operator has been shown <c>ArrayFlashStatusResponse.UnattendedWarning</c> and
    /// accepted it. Required by <see cref="Unattended"/>, meaningless without it.
    /// </summary>
    public bool Acknowledged { get; init; }

    /// <summary>The operator's own words for the trail. Optional.</summary>
    public string? Note { get; init; }
}
