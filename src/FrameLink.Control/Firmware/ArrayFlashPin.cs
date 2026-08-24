using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FrameLink.Control.Firmware;

/// <summary>
/// The Fleet Manager's half of what an operator needs in order to authorise a microphone-array
/// firmware write (decision 91): the authorisation's shape, and the warnings behind its bypass.
/// </summary>
/// <remarks>
/// <para>
/// <b>The image itself is no longer restated here.</b> Its name, version, digest and length used to
/// sit below as four <c>const</c>s held equal to the agent's by <c>ControlArrayFlashTests</c>,
/// string by string. On the operator's decision there is now one definition:
/// <see cref="XvfFirmwarePin"/> is the <i>agent's</i> file,
/// <c>src/FrameLink.Agent/Firmware/XvfFirmwarePin.cs</c>, compiled into this assembly by a
/// <c>&lt;Compile Include&gt;</c> that reaches across the project boundary — the same move the
/// csproj already makes for <c>fl-agent.service</c>. Two records that must agree is a pair that will
/// one day not; one file compiled twice cannot disagree with itself, and what the test now checks is
/// that both assemblies were built from it.
/// </para>
/// <para>
/// <b>What is still a second record, and why it stays one.</b> The authorisation key, the unattended
/// prefix and the four warning sentences below are <c>ArrayFirmwareFlash</c>'s, and that class
/// cannot be linked here — it is the flash itself, and it reaches into the agent's filesystem,
/// process runner, telemetry and panel. So those four values remain copies held equal by
/// <c>ControlArrayFlashTests</c>. The warning in particular is copied and <b>never reworded</b>: it
/// is the text an operator accepts by taking the unattended bypass, and the agent emits it verbatim
/// into the <c>array-flash</c> event of every unattended write, so the sentence an operator read and
/// the sentence in the audit trail have to be one sentence.
/// </para>
/// </remarks>
public static class ArrayFlashPin
{
    /// <summary>
    /// The fleet setting an authorisation arrives in — <c>ArrayFirmwareFlash.AuthorisationKey</c>.
    /// </summary>
    /// <remarks>
    /// Written as a <i>per-device override</i> and never as a fleet default. A fleet default would
    /// authorise one write on every frame that has not already spent that exact string, which is a
    /// thing an operator can still do by hand on the settings screen and should have to mean.
    /// </remarks>
    public const string AuthorisationKey = "audio.arrayFirmwareFlash";

    /// <summary>The one image this server may ever compose an authorisation for.</summary>
    /// <remarks>
    /// A version string is not an identity here: upstream published
    /// <c>respeaker_xvf3800_usb_dfu_firmware_v2.0.10.bin</c> twice with different bytes, both
    /// answering <c>VERSION 2 0 10</c>. Only a digest names an image, which is why the operator
    /// never types one and this server composes it from the pin.
    /// </remarks>
    public static XvfFirmwareImage Target => XvfFirmwarePin.Current.Target;

    /// <summary>
    /// The operator's scoped bypass, written inside a ticket as <c>&lt;prefix&gt;&lt;deviceId&gt;</c>
    /// — <c>ArrayFirmwareFlash.UnattendedPrefix</c>.
    /// </summary>
    public const string UnattendedPrefix = "unattended-nobody-at-this-frame-i-accept-mains-loss-destroys-it=";

    /// <summary>
    /// The warnings an operator accepts by choosing the bypass —
    /// <c>ArrayFirmwareFlash.UnattendedWarning</c>, verbatim.
    /// </summary>
    public static IReadOnlyList<string> UnattendedWarning { get; } =
    [
        "Nobody will be standing at this frame while its microphone is written.",
        "Mains loss during the write is unguardable at the device: no interlock in this product can reach it, and a "
            + "write interrupted by loss of power can leave the microphone unusable until somebody recovers it by hand.",
        "Recovery needs physical access — power the unit off, hold Mute, power it back on — so a frame nobody can "
            + "reach is a frame nobody can recover.",
        "This applies to one write on one named frame. It is spent the instant the write starts and authorises "
            + "nothing afterwards.",
    ];

    /// <summary>The word every composed ticket opens with, so the trail says where it came from.</summary>
    public const string Issuer = "fleet-manager";

    /// <summary>Longest operator note a composed ticket will carry.</summary>
    public const int NoteLimit = 200;
}

/// <summary>
/// Composes and reads back the <c>&lt;sha256&gt;:&lt;ticket&gt;</c> string the agent's
/// <c>audio.arrayFirmwareFlash</c> setting carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing an operator types reaches the parts that decide which frame is written.</b> The
/// digest comes from <see cref="ArrayFlashPin.Target"/> and the device id comes from the
/// route the request arrived on — the same id the settings row is written against — so a
/// composed authorisation cannot name a frame other than the one it is stored for. That is the
/// whole reason this lives on the server rather than in the browser: a device id assembled in
/// JavaScript and posted back would be a value the server takes on trust, and the bypass is
/// scoped by exactly that value.
/// </para>
/// <para>
/// <b>Every composition is unique, and it has to be.</b> The agent's authorisation is single-use
/// by <i>exact string</i>: the whole value is written to <c>array-flash.consumed</c> before
/// <c>dfu-util</c> starts, and a value equal to the recorded one is refused for ever after. A
/// ticket built only from stable parts would therefore authorise one write per frame per lifetime
/// — the second press would compose the string already on the card and be refused as
/// <c>AlreadyConsumed</c> with nothing to say why. <see cref="Compose"/> carries a random
/// <c>id=</c> so re-authorising is always a genuinely different string, which is what the agent
/// documents as the deliberate act.
/// </para>
/// <para>
/// <b>The <c>at=</c> field is read back rather than stored beside the setting.</b>
/// <c>ISettingsStore</c> holds an opaque key and an opaque string and records no write time, and
/// giving it one for this single key would be a schema change in the generic mechanism §3.4 exists
/// to keep generic. The instant therefore travels inside the value this server composed, and
/// <see cref="IssuedAt"/> reads it back. A hand-written authorisation has no <c>at=</c> and simply
/// answers null, which every caller treats as "unknown" rather than as an error.
/// </para>
/// </remarks>
public static class ArrayFlashTicket
{
    private const string IdField = "id=";
    private const string AtField = "at=";
    private const string DeviceField = "device=";
    private const string NoteField = "note=";

    /// <summary>
    /// Builds the authorisation for one frame.
    /// </summary>
    /// <param name="deviceId">The frame it is for. Taken from the route, never from a payload.</param>
    /// <param name="unattended">Whether the local approval on that frame's screen is bypassed.</param>
    /// <param name="note">An operator's own words for the trail, already checked by <see cref="NoteProblem"/>.</param>
    /// <param name="whenUtc">Now.</param>
    public static string Compose(string deviceId, bool unattended, string? note, DateTimeOffset whenUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var ticket = new StringBuilder(ArrayFlashPin.Issuer)
            .Append(' ').Append(IdField).Append(RandomNumberGenerator.GetHexString(8, lowercase: true))
            .Append(' ').Append(AtField).Append(whenUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
            .Append(' ').Append(DeviceField).Append(deviceId);

        if (Tidy(note) is { Length: > 0 } tidied)
        {
            ticket.Append(' ').Append(NoteField).Append(tidied);
        }

        // Last, because it is the one part of the string that changes what happens on the frame,
        // and because the agent's parser takes the last match — so nothing written after it could
        // displace it even if the note check above were ever weakened.
        if (unattended)
        {
            ticket.Append(' ').Append(ArrayFlashPin.UnattendedPrefix).Append(deviceId);
        }

        return ArrayFlashPin.Target.Sha256 + ":" + ticket;
    }

    /// <summary>
    /// Why <paramref name="note"/> cannot go in a ticket, or null when it can.
    /// </summary>
    /// <remarks>
    /// <b>The one thing a note may not contain is a bypass.</b> The agent reads the bypass out of
    /// the ticket by looking for a whitespace-delimited word starting with
    /// <see cref="ArrayFlashPin.UnattendedPrefix"/>, so free text that carried one would be an
    /// unattended write authorised through the attended path — the operator having accepted
    /// nothing, and the screen having asked nobody. Refusing the note outright is the only answer
    /// that cannot be got round: stripping it would silently change what the operator wrote, and
    /// escaping it would need the agent to un-escape.
    /// </remarks>
    public static string? NoteProblem(string? note)
    {
        if (Tidy(note) is not { Length: > 0 } tidied)
        {
            return null;
        }

        if (tidied.Contains(ArrayFlashPin.UnattendedPrefix, StringComparison.Ordinal))
        {
            return "A note may not contain the unattended-write token. Choosing the unattended option is how a "
                + "write is authorised with nobody at the frame, and it is offered on its own so that the warnings "
                + "are read before it is taken.";
        }

        return tidied.Length > ArrayFlashPin.NoteLimit
            ? $"A note is at most {ArrayFlashPin.NoteLimit} characters. This one is {tidied.Length}."
            : null;
    }

    /// <summary>The ticket half of an authorisation — everything after the first colon.</summary>
    public static string TicketOf(string authorisation)
    {
        ArgumentNullException.ThrowIfNull(authorisation);
        var colon = authorisation.IndexOf(':');
        return colon < 0 ? string.Empty : authorisation[(colon + 1)..].Trim();
    }

    /// <summary>The digest half — everything before the first colon.</summary>
    public static string DigestOf(string authorisation)
    {
        ArgumentNullException.ThrowIfNull(authorisation);
        var colon = authorisation.IndexOf(':');
        return (colon < 0 ? authorisation : authorisation[..colon]).Trim();
    }

    /// <summary>Whether this authorisation names the image this build knows how to authorise.</summary>
    public static bool NamesTheTarget(string authorisation) =>
        string.Equals(DigestOf(authorisation), ArrayFlashPin.Target.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The device id the bypass in <paramref name="authorisation"/> names, or null when it carries
    /// none.
    /// </summary>
    /// <remarks>
    /// A deliberate mirror of <c>ArrayFlashAuthorisation.Parse</c>, including its two subtleties:
    /// the bare prefix with nothing after it is <i>not</i> a bypass, and the last matching word
    /// wins. <c>ControlArrayFlashTests</c> runs both parsers over the same strings, so this cannot
    /// drift into disagreeing with the frame about what a string means.
    /// </remarks>
    public static string? UnattendedDeviceId(string authorisation)
    {
        string? named = null;

        foreach (var word in TicketOf(authorisation).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith(ArrayFlashPin.UnattendedPrefix, StringComparison.Ordinal)
                && word[ArrayFlashPin.UnattendedPrefix.Length..] is { Length: > 0 } tail)
            {
                named = tail;
            }
        }

        return named;
    }

    /// <summary>Whether this authorisation bypasses the local approval on <paramref name="deviceId"/>.</summary>
    public static bool IsUnattendedFor(string authorisation, string deviceId) =>
        UnattendedDeviceId(authorisation) is { Length: > 0 } named
        && string.Equals(named, deviceId, StringComparison.Ordinal);

    /// <summary>When this server composed it, or null when nothing did.</summary>
    public static DateTimeOffset? IssuedAt(string authorisation)
    {
        foreach (var word in TicketOf(authorisation).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!word.StartsWith(AtField, StringComparison.Ordinal))
            {
                continue;
            }

            if (DateTimeOffset.TryParse(
                    word[AtField.Length..],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>The operator's note, or null when the ticket carries none.</summary>
    public static string? NoteOf(string authorisation)
    {
        var ticket = TicketOf(authorisation);
        var start = ticket.IndexOf(NoteField, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        // To the end, or to the bypass — the note is the only free-text field, so it runs until
        // the one structured word that may follow it.
        var body = ticket[(start + NoteField.Length)..];
        var bypass = body.IndexOf(ArrayFlashPin.UnattendedPrefix, StringComparison.Ordinal);
        var text = (bypass < 0 ? body : body[..bypass]).Trim();

        return text.Length == 0 ? null : text;
    }

    /// <summary>Collapses whitespace and drops control characters. Never rewrites words.</summary>
    private static string Tidy(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return string.Empty;
        }

        var tidied = new StringBuilder(note.Length);
        var space = false;

        foreach (var character in note.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                space = true;
                continue;
            }

            if (char.IsControl(character))
            {
                continue;
            }

            if (space && tidied.Length > 0)
            {
                tidied.Append(' ');
            }

            space = false;
            tidied.Append(character);
        }

        return tidied.ToString();
    }
}
