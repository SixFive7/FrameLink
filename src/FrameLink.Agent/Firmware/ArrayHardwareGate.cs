using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Resources;

namespace FrameLink.Agent.Firmware;

/// <summary>What this build concluded about the unit it is looking at.</summary>
/// <remarks>
/// <para>
/// <b>One value per rung of <see cref="ArrayHardwareGate"/>'s ladder, and the order of the values
/// is the order of the ladder.</b> That is not decoration: the first rung that fails is the one a
/// person reads, so a rung that ran too early produces a true sentence about the wrong thing —
/// "this unit reports a firmware version nobody here has seen" is a useless answer when the real
/// problem is that two units are plugged in and the tool answered whichever one enumerated first.
/// </para>
/// <para>
/// Every value except <see cref="Recognised"/> is a refusal, and every refusal is a complete
/// message rather than a code: see <see cref="ArrayGateRuling"/>.
/// </para>
/// </remarks>
public enum ArrayGateVerdict
{
    /// <summary>Everything readable about this unit is something this build has been told about.</summary>
    Recognised,

    /// <summary>Rung 1 — nothing with the array's vendor and product ids is on the bus.</summary>
    NoArrayOnTheBus,

    /// <summary>Rung 2 — more than one is, and the control tool has no device selector.</summary>
    MoreThanOneArray,

    /// <summary>Rung 3 — the unit reports no USB serial, or one this build cannot take apart.</summary>
    SerialUnreadable,

    /// <summary>Rung 3 — the serial decodes, and its product code is not one this build knows.</summary>
    UnknownProductSku,

    /// <summary>Rung 4 — the board revision somebody recorded for this frame vetoes the write.</summary>
    BoardRevisionRefused,

    /// <summary>Rung 5 — the control tool is not installed, so nothing below can be read.</summary>
    ControlToolMissing,

    /// <summary>Rung 5 — the tool is installed and the unit did not answer it.</summary>
    ControlSilent,

    /// <summary>Rung 6 — the unit's build configuration is not the one the pinned image is built for.</summary>
    UnknownBuildConfiguration,

    /// <summary>Rung 7 — the USB descriptor and the control interface report different firmware.</summary>
    ReadingsDisagree,

    /// <summary>Rung 8 — the firmware this unit runs is not one this build has ever been told about.</summary>
    UnknownFirmware,

    /// <summary>Rung 8 — the unit already runs something newer than the pinned target.</summary>
    FirmwareNewerThanTarget,

    /// <summary>Rung 9 — the running firmware says it is driving a linear array, not a square one.</summary>
    MicArrayTypeUnexpected,

    /// <summary>Rung 9 — the microphone coordinates it reports are not this board's 66 mm square.</summary>
    MicArrayGeometryUnexpected,

    /// <summary>Rung 10 — every field passed on its own and the whole tuple is on no allowlist entry.</summary>
    NotOnTheAllowlist,
}

/// <summary>
/// Everything a frame can actually read about the microphone unit plugged into it.
/// </summary>
/// <param name="VendorId">USB <c>idVendor</c>, as sysfs spells it.</param>
/// <param name="ProductId">USB <c>idProduct</c>, as sysfs spells it.</param>
/// <param name="BcdDevice">The raw <c>bcdDevice</c> field, which encodes the firmware version.</param>
/// <param name="Serial">The unit's USB serial, which identifies the unit and not the design.</param>
/// <param name="DescriptorVersion">The firmware version decoded from <paramref name="BcdDevice"/>.</param>
/// <param name="ControlVersion">The firmware version the control interface reports, or null.</param>
/// <param name="BuildConfiguration">The <c>BLD_MSG</c> build profile, or null.</param>
/// <param name="BuildRepositoryHash">The <c>BLD_REPO_HASH</c> fingerprint, or null.</param>
/// <param name="MicArrayType">
/// <c>AEC_MIC_ARRAY_TYPE</c> — 1 linear, 2 squarecular — or null when it could not be read.
/// </param>
/// <param name="MicArrayGeometry">
/// <c>AEC_MIC_ARRAY_GEO</c>'s twelve metres-XYZ floats, or null when they could not be read.
/// </param>
/// <remarks>
/// <para>
/// <b>Board revision is not a field here, and that is a finding rather than an omission.</b> It is
/// not in the USB descriptors, and it is not in the control tool's command set either: the pinned
/// <c>libcommand_map.so</c> was enumerated and filtered for <c>BOARD</c>, <c>REVIS</c>, <c>_REV</c>,
/// <c>HW_</c>, <c>PCB</c>, <c>VARIANT</c> and <c>MODEL</c>, and every identity command among them —
/// <c>VERSION</c>, <c>BLD_MSG</c>, <c>BLD_HOST</c>, <c>BLD_REPO_HASH</c>, <c>BLD_MODIFIED</c>,
/// <c>BOOT_STATUS</c>, <c>SERIAL_NUMBER</c>, <c>DFU_GETVERSION</c> — describes the <i>firmware</i>
/// or the <i>unit</i>, never the board. The revision is silkscreen. So the one gate a reader of
/// upstream issue #32 would reach for first, <i>refuse to write to a V1.1 board</i>, cannot be
/// written from anything this record holds; what <see cref="ArrayBoardRevision"/> gates on instead
/// is a value a human typed, which is a different kind of fact and is kept in a different place for
/// exactly that reason.
/// </para>
/// <para>
/// <b><c>BLD_REPO_HASH</c> is carried and never gated on</b>, for a measured reason: it is a stable,
/// reproducible per-build fingerprint that resolves to nothing anybody outside XMOS can look up —
/// <c>sw_xvf3800</c> does not exist as a public repository — and the unit on this project's own
/// frame answers <c>BLD_MODIFIED TRUE</c>, so the hash names a base commit rather than the bytes on
/// the board. It is worth recording in the event trail, because it tells two boards apart with
/// certainty; it is worth nothing as a gate, because there is no set of known-good values to
/// compare it against.
/// </para>
/// <para>
/// <b>The two <c>AEC_MIC_ARRAY_*</c> fields are new, and what they are for is narrower than it
/// looks.</b> They report the <i>running firmware's</i> belief about the microphone array, not the
/// board's wiring, so they cannot tell a V1.0 from a V1.1 and this file never claims they can. What
/// they do is turn "which profile is on this unit" from an inference off a filename into a
/// measurement taken from the device, which is the one thing <c>BLD_MSG</c> alone cannot
/// corroborate. <b>Neither has ever been read on this project's hardware</b> — see
/// <see cref="ArrayExpectation"/> for what this build does about an expectation it has never seen
/// confirmed.
/// </para>
/// </remarks>
public readonly record struct ArrayIdentity(
    string VendorId,
    string ProductId,
    string BcdDevice,
    string Serial,
    string? DescriptorVersion,
    string? ControlVersion,
    string? BuildConfiguration,
    string? BuildRepositoryHash,
    int? MicArrayType,
    IReadOnlyList<double>? MicArrayGeometry)
{
    /// <summary>How many characters of a serial are the product code.</summary>
    public const int SkuLength = 9;

    /// <summary>How many characters a whole serial has, on every unit anybody has read one from.</summary>
    /// <remarks>
    /// <b>SKU(9) + batch(4) + unit(5), and the decode is inference.</b> Seeed documents no serial
    /// format anywhere. What is measured is that the Bazaar lists the bare board's SKU as
    /// <c>101991441</c>, that both of this project's arrays read <c>101991441</c> + <c>2605</c> +
    /// a five-digit unit, and that upstream's own DFU guide shows <c>101991441000000001</c> — same
    /// nine-digit head, zeroed middle, unit 1. Three units, two unrelated sources, one shape.
    /// </remarks>
    public const int SerialLength = 18;

    /// <summary>The nine-digit product code at the head of the serial, or null when it will not decode.</summary>
    public string? ProductSku
    {
        get
        {
            var serial = Serial.Trim();

            if (serial.Length != SerialLength)
            {
                return null;
            }

            foreach (var character in serial)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return null;
                }
            }

            return serial[..SkuLength];
        }
    }

    /// <summary>The four-digit batch field, or null when the serial will not decode.</summary>
    public string? Batch => ProductSku is null ? null : Serial.Trim()[SkuLength..(SkuLength + 4)];

    /// <summary>The whole reading in one sentence, for the event trail and for a refusal.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"USB {VendorId}:{ProductId}, bcdDevice {BcdDevice} = firmware {DescriptorVersion ?? "undecodable"}, "
        + $"the control interface answers {ControlVersion ?? "nothing"}, build configuration "
        + $"{BuildConfiguration ?? "unreadable"}, build hash {BuildRepositoryHash ?? "unreadable"}, "
        + $"serial {(Serial.Length == 0 ? "(none)" : Serial)}, microphone array type "
        + $"{MicArrayType?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}, geometry "
        + $"{ArrayHardwareGate.DescribeGeometry(MicArrayGeometry)}");
}

/// <summary>
/// One look at this frame's USB bus: every array on it, and the full identity when there is one.
/// </summary>
/// <param name="Devices">Every <c>2886:001a</c> device the kernel is publishing, in bus order.</param>
/// <param name="Identity">
/// Everything readable about the single attached unit, or null when there is not exactly one.
/// </param>
/// <param name="BusEnumerable">Whether this machine publishes USB devices at all.</param>
/// <remarks>
/// <b>The device list is carried rather than collapsed to a count, because rung 2's message needs
/// it.</b> "More than one microphone unit is attached" tells an operator nothing they can act on;
/// "two are attached, on <c>1-1</c> serial …069 and <c>1-3</c> serial …030" tells them which cable
/// to pull.
/// </remarks>
public readonly record struct ArrayScan(
    IReadOnlyList<XvfArrayDevice> Devices,
    ArrayIdentity? Identity,
    bool BusEnumerable);

/// <summary>
/// How well established one expected value is — which decides what an <i>unreadable</i> one means.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because two of the ten rungs check values nobody in this project has ever
/// seen.</b> <c>AEC_MIC_ARRAY_TYPE</c> and <c>AEC_MIC_ARRAY_GEO</c> are both in the pinned command
/// map and both have expected values published by Seeed and by XMOS — but no reading of either has
/// ever been taken from this project's arrays, so the exact text the compiled <c>xvf_host</c> prints
/// them in is unknown. A rung that refused when it could not parse an answer would therefore refuse
/// on every genuine board the first time it ran, which is the worst possible failure for a gate
/// whose entire purpose is to be trusted.
/// </para>
/// <para>
/// <b>So the rule is asymmetric, and the asymmetry is the honesty.</b> A reading that <i>positively
/// contradicts</i> an expectation refuses whatever the provenance — a unit answering "linear" is a
/// unit with a different microphone arrangement, and that is true whether or not anybody here has
/// ever seen "squarecular" come back. A reading that is <i>absent or unparseable</i> refuses only
/// when the expectation is <see cref="Measured"/>, because only then does this build know from its
/// own hardware that the value is readable at all. Everything else is recorded in the technical
/// block as unverified and named as such.
/// </para>
/// </remarks>
public enum ArrayExpectation
{
    /// <summary>Read from this project's own hardware. An unreadable value is itself a fault.</summary>
    Measured,

    /// <summary>Stated by Seeed or XMOS, never read here. An unreadable value is not a refusal.</summary>
    Published,
}

/// <summary>
/// One complete unit this build has been told about — the allowlist's entry, not a set of rules.
/// </summary>
/// <remarks>
/// <para>
/// <b>The allowlist is the rung that makes "we only flash what we recognise" literally true.</b>
/// The nine rungs above it each check one field against the union of everything any entry allows,
/// which is what makes their messages specific; passing all nine still only means every field is
/// <i>individually</i> familiar. A unit whose serial says one product, whose build configuration
/// says another and whose firmware belongs to a third would walk through all nine. Rung 10 asks the
/// different question: <i>is this whole tuple one entry?</i>
/// </para>
/// <para>
/// <b>Adding an entry is a source edit somebody has to mean</b>, in the same shape as bumping the
/// firmware pin, and <see cref="Evidence"/> is not optional decoration — it is the record of what
/// was actually established before a unit was allowed to be written to. An entry whose evidence
/// reads "assumed" is an entry that should not be here.
/// </para>
/// </remarks>
public sealed record KnownArrayProfile
{
    /// <summary>What a person would call this unit.</summary>
    public required string Name { get; init; }

    /// <summary>USB <c>idVendor</c>, as sysfs spells it.</summary>
    public required string VendorId { get; init; }

    /// <summary>USB <c>idProduct</c>, as sysfs spells it.</summary>
    public required string ProductId { get; init; }

    /// <summary>Every nine-digit serial head this entry covers.</summary>
    public required IReadOnlyList<string> ProductSkus { get; init; }

    /// <summary>The <c>BLD_MSG</c> build configuration this entry is, exactly.</summary>
    public required string BuildConfiguration { get; init; }

    /// <summary>Every firmware version this entry has been established on.</summary>
    public required IReadOnlyList<string> Firmware { get; init; }

    /// <summary>
    /// Every silkscreen board revision this entry has actually been established on.
    /// </summary>
    /// <remarks>
    /// Not "every revision that exists" — <b>every revision somebody here has held in their hand and
    /// established this entry against</b>. V1.0 is attested only by Seeed's own product photographs
    /// and no customer photograph, review, teardown or issue report of one has ever been found, so
    /// it is deliberately absent: whether it ever shipped is unknown, and an allowlist that covered
    /// a board nobody has ever seen would not be an allowlist.
    /// </remarks>
    public required IReadOnlyList<string> BoardRevisions { get; init; }

    /// <summary><c>AEC_MIC_ARRAY_TYPE</c>'s expected value, and how well established it is.</summary>
    public required int MicArrayType { get; init; }

    /// <summary>Where <see cref="MicArrayType"/> and the geometry came from.</summary>
    public required ArrayExpectation MicArrayProvenance { get; init; }

    /// <summary>What was established, by whom, and when.</summary>
    public required string Evidence { get; init; }

    /// <summary>Whether every field of <paramref name="unit"/> belongs to this one entry.</summary>
    public bool Covers(ArrayIdentity unit) =>
        string.Equals(unit.VendorId, VendorId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(unit.ProductId, ProductId, StringComparison.OrdinalIgnoreCase)
        && unit.ProductSku is { } sku
        && ProductSkus.Contains(sku, StringComparer.Ordinal)
        && string.Equals(unit.BuildConfiguration, BuildConfiguration, StringComparison.Ordinal)
        && unit.ControlVersion is { } control
        && Firmware.Contains(control, StringComparer.Ordinal)
        && unit.DescriptorVersion is { } descriptor
        && Firmware.Contains(descriptor, StringComparer.Ordinal)
        && (unit.MicArrayType is not { } type || type == MicArrayType)
        && (unit.MicArrayGeometry is not { } geometry || ArrayHardwareGate.IsSquareGeometry(geometry));
}

/// <summary>What the board-revision gate decided, and why in words a person can read.</summary>
/// <param name="Refuses">Whether this ruling stops the write.</param>
/// <param name="Plain">The plain-language reason, or empty when it does not refuse.</param>
/// <param name="Technical">The compact technical reason, or empty.</param>
public readonly record struct BoardRevisionRuling(bool Refuses, string Plain, string Technical)
{
    /// <summary>The ruling that stops nothing.</summary>
    public static BoardRevisionRuling Allows { get; } =
        new(Refuses: false, string.Empty, string.Empty);
}

/// <summary>
/// Rung 4 — how the board revision somebody wrote down for this frame is allowed to matter.
/// </summary>
/// <remarks>
/// <b>This is the seam the pending decision moves on.</b> See <see cref="ArrayBoardRevision"/>.
/// </remarks>
public interface IBoardRevisionGate
{
    /// <summary>One line naming this gate's semantics, for the technical block.</summary>
    string Semantics { get; }

    /// <summary>
    /// Rung 4 — asked before the unit is spoken to at all, so it can only see what was typed.
    /// </summary>
    /// <param name="recorded">Whatever an operator wrote in the setting, trimmed, or null.</param>
    /// <param name="known">Every revision any allowlist entry has been established on.</param>
    BoardRevisionRuling BeforeReading(string? recorded, IReadOnlyList<string> known);

    /// <summary>
    /// Rung 10 — asked once a whole-profile match exists, so it can see a contradiction.
    /// </summary>
    /// <param name="recorded">Whatever an operator wrote in the setting, trimmed, or null.</param>
    /// <param name="matched">The allowlist entry the unit's readable fields matched.</param>
    BoardRevisionRuling AgainstProfile(string? recorded, KnownArrayProfile matched);
}

/// <summary>
/// <b>PENDING OPERATOR DECISION — open question C.</b> How the operator-recorded board revision
/// gates. This class is the whole of the seam; changing the semantics is changing
/// <see cref="Default"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question.</b> The board revision is silkscreen and no software anywhere can read it, so
/// the only way a fleet can know one is for a person to look at the board and type it in. That makes
/// it a fact of a different kind from every other rung: unverifiable, absent by default, and wrong
/// whenever somebody swaps a unit and forgets. The question the operator has not answered is what
/// such a value is allowed to do.
/// </para>
/// <para>
/// <b>What is built, and it is the recommendation already put to them: a veto, never a
/// permission.</b> <see cref="VetoOnlyBoardRevisionGate"/> can refuse a write and can never be the
/// only reason one proceeds. Concretely: a blank value permits nothing and refuses nothing, so
/// every other rung still has to pass on its own; a recorded value this build has not been
/// established against refuses at rung 4, before the unit is even spoken to; and a recorded value
/// that contradicts the allowlist entry the unit's own readings matched refuses at rung 10 <i>even
/// though the recorded value is a revision this build knows</i>. The last of those is the property
/// that makes it a veto rather than a checkbox — the typed value can disagree with the hardware,
/// and when it does, the hardware does not win either.
/// </para>
/// <para>
/// <b>The two alternatives, so the seam is legible.</b> (a) <i>Required</i> — no frame is flashed
/// until somebody has recorded a revision for it, which is the strongest gate and the one that
/// stops a fleet dead if a household's frame was never surveyed. (b) <i>Advisory</i> — the value is
/// recorded in the event trail and never refuses, which is the weakest and makes the field
/// decoration. Both are a one-line change to <see cref="Default"/> plus one new implementation of
/// <see cref="IBoardRevisionGate"/>; nothing else in the ladder moves, because no other rung reads
/// the recorded value.
/// </para>
/// </remarks>
public static class ArrayBoardRevision
{
    /// <summary>The fleet setting an operator records a frame's board revision in.</summary>
    /// <remarks>
    /// A per-device override in practice — a revision is a property of one physical board — but it
    /// is an ordinary setting and nothing here stops somebody setting it fleet-wide. A fleet-wide
    /// value that is right for one frame and wrong for another refuses the wrong ones, which is the
    /// safe direction for a veto to fail in.
    /// </remarks>
    public const string SettingKey = "audio.arrayBoardRevision";

    /// <summary>
    /// Every revision that demonstrably exists anywhere, which is not the same as the allowlist.
    /// </summary>
    /// <remarks>
    /// Two, and the evidence for each is thin: <c>V1.0</c> from silkscreen legible in two of Seeed's
    /// own published product photographs, and <c>V1.1</c> from this project's two boards and from
    /// upstream issue #32. A GitHub-wide search for <c>XVF3800 V1.1</c> returns exactly one result
    /// in all of GitHub. There is no V1.2, no V2, no letter suffix and no dated revision in any
    /// evidence. This list exists so a refusal can say <i>this is not a revision anybody has ever
    /// recorded</i> separately from <i>this is not a revision we have been established against</i>,
    /// which are different problems with different answers.
    /// </remarks>
    public static IReadOnlyList<string> Attested { get; } = ["V1.0", "V1.1"];

    /// <summary>
    /// The semantics in force. <b>Changing this one line changes the pending decision.</b>
    /// </summary>
    public static IBoardRevisionGate Default { get; } = new VetoOnlyBoardRevisionGate();

    /// <summary>A recorded revision as this build compares it: trimmed, upper-cased, or null.</summary>
    /// <remarks>
    /// <c>v1.1</c>, <c>V1.1</c> and <c> V1.1 </c> are one person writing one thing down. A value
    /// that normalises to nothing is treated as not recorded at all, because an operator who
    /// cleared the field has not made a claim about the hardware.
    /// </remarks>
    public static string? Normalise(string? recorded)
    {
        var value = recorded?.Trim();
        return value is { Length: > 0 } ? value.ToUpperInvariant() : null;
    }
}

/// <summary>
/// The default semantics: the recorded revision may refuse a write and may never permit one.
/// </summary>
/// <remarks>
/// See <see cref="ArrayBoardRevision"/> for the decision this implements and for the alternatives.
/// </remarks>
public sealed class VetoOnlyBoardRevisionGate : IBoardRevisionGate
{
    /// <inheritdoc/>
    public string Semantics =>
        "veto-only — a recorded revision can refuse a write and can never be the only reason one proceeds";

    /// <inheritdoc/>
    public BoardRevisionRuling BeforeReading(string? recorded, IReadOnlyList<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);

        if (ArrayBoardRevision.Normalise(recorded) is not { } value)
        {
            // Absence permits nothing. Every rung below still has to pass on its own, which is what
            // keeps this a veto: a frame nobody surveyed is flashed on the strength of what its
            // hardware says, exactly as it would be if this gate did not exist.
            return BoardRevisionRuling.Allows;
        }

        if (known.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return BoardRevisionRuling.Allows;
        }

        var attested = ArrayBoardRevision.Attested.Contains(value, StringComparer.OrdinalIgnoreCase);

        return new BoardRevisionRuling(
            Refuses: true,
            attested
                ? "Somebody has written down which version of the microphone bar this frame has, and this update has "
                    + "never been tried on that version."
                : "Somebody has written down which version of the microphone bar this frame has, and it is not a "
                    + "version anybody has ever recorded for this product.",
            "recorded board revision " + value + " is "
            + (attested
                ? "attested but not one any allowlist entry has been established on"
                : "not among the revisions attested anywhere (" + string.Join(", ", ArrayBoardRevision.Attested) + ")"));
    }

    /// <inheritdoc/>
    public BoardRevisionRuling AgainstProfile(string? recorded, KnownArrayProfile matched)
    {
        ArgumentNullException.ThrowIfNull(matched);

        if (ArrayBoardRevision.Normalise(recorded) is not { } value)
        {
            return BoardRevisionRuling.Allows;
        }

        if (matched.BoardRevisions.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            // Note what this does *not* do: it does not return "allowed because the revision
            // matches". It returns "this ruling refuses nothing", and the write proceeds on the
            // strength of the nine readable rungs. That distinction is the whole of the decision.
            return BoardRevisionRuling.Allows;
        }

        return new BoardRevisionRuling(
            Refuses: true,
            "The version of the microphone bar somebody wrote down for this frame does not match the microphone bar "
                + "the frame can actually see. One of the two is wrong, and nothing will be written until somebody "
                + "has checked which.",
            "recorded board revision " + value + " contradicts allowlist entry '" + matched.Name
                + "', which has only been established on " + string.Join(", ", matched.BoardRevisions));
    }
}

/// <summary>
/// One complete answer to <i>may this build write firmware to this unit?</i> — for two readers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every refusal is written for two people at once, and the halves are kept apart on purpose.</b>
/// The person standing in front of the frame is a family member who has never opened a terminal;
/// the person they will forward this to is somebody technical who needs the values. A single
/// message pitched between the two is useless to both — so <see cref="Plain"/> carries no version
/// numbers, no hex and no product ids at all, and <see cref="Technical"/> carries nothing else.
/// </para>
/// <para>
/// <b><see cref="Next"/> is not optional and is not advice.</b> A refusal that does not say what to
/// do next is a dead end with a reason attached, and the household reading it has no route from
/// there to a working frame. Every verdict names one concrete next action, and where that action is
/// "nothing, tell somebody", it says so rather than leaving the reader to work out that there is
/// nothing they can do.
/// </para>
/// </remarks>
public sealed record ArrayGateRuling
{
    /// <summary>Which rung concluded this, or <see cref="ArrayGateVerdict.Recognised"/>.</summary>
    public required ArrayGateVerdict Verdict { get; init; }

    /// <summary>Which rung of ten this is, or 0 when nothing refused.</summary>
    public required int Rung { get; init; }

    /// <summary>What that rung checks, in one short phrase, for the technical block.</summary>
    public required string Checked { get; init; }

    /// <summary>What was found, for the technical block.</summary>
    public required string Found { get; init; }

    /// <summary>What was expected, for the technical block.</summary>
    public required string Expected { get; init; }

    /// <summary>One line a person reads from across a room. No numbers, no jargon.</summary>
    public required string Headline { get; init; }

    /// <summary>The plain-language body. Written for somebody with no computer experience.</summary>
    public required IReadOnlyList<string> Plain { get; init; }

    /// <summary>What the person in front of the frame should actually do. Always present.</summary>
    public required string Next { get; init; }

    /// <summary>The compact block to hand to somebody technical.</summary>
    public required IReadOnlyList<string> Technical { get; init; }

    /// <summary>Whether this build may write the pinned image to the unit it just read.</summary>
    public bool MayWrite => Verdict is ArrayGateVerdict.Recognised;

    /// <summary>The plain half as one flowing paragraph, for a screen a household reads.</summary>
    public IReadOnlyList<string> Screen => [.. Plain, Next];

    /// <summary>Both halves, as the one message that goes into the event trail.</summary>
    /// <remarks>
    /// Blank-line separated rather than run together, and the Fleet Manager renders it with
    /// <c>white-space: pre-line</c> so the technical block stays a block. A console that collapses
    /// it still reads correctly, because every line is a complete sentence or a
    /// <c>key: value</c> pair.
    /// </remarks>
    public string Message => string.Join(
        "\n",
        [
            Headline,
            string.Empty,
            .. Plain,
            string.Empty,
            "What to do next: " + Next,
            string.Empty,
            "Technical detail, for whoever helps you:",
            .. Technical,
        ]);
}

/// <summary>
/// <b>Refuse to write firmware to a unit this build cannot recognise, and say so loudly.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>The worry this answers is future hardware, and the operator is right to have it.</b> The
/// pinned image is a single build for a single audio topology on a single product; a unit bought in
/// two years' time may be a different revision, a different profile or a different product wearing
/// the same vendor and product id. Writing 933 KB of firmware into a device on the hope that it is
/// the one this build was tested against is exactly the operation with no undo, so this class asks
/// the opposite question: <i>is everything I can read about this unit something I have been told
/// about?</i> — and refuses when the answer is no, rather than proceeding hopefully.
/// </para>
/// <para>
/// <b>Ten rungs, and the order is the design.</b> Presence before identity, identity before talking
/// to it, talking before interpreting what it says. The order is what makes the <i>first</i> failure
/// the useful one: a frame with two arrays plugged in would otherwise be told its firmware is
/// unrecognised, which is true, unhelpful, and about whichever unit happened to enumerate first.
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Is any <c>2886:001a</c> device on the bus at all?</b> Nothing below means anything without one.
/// </description></item>
/// <item><description>
/// <b>Exactly one?</b> The control tool has no device selector — its USB backend opens whichever
/// array enumerates first — so with two attached every reading below describes an unknown one of
/// them, and the refusal names all of them so somebody knows which cable to pull.
/// </description></item>
/// <item><description>
/// <b>Does the serial decode, and is its product code one this build knows?</b> The serial is
/// <c>SKU(9) + batch(4) + unit(5)</c>, and the nine-digit head is Seeed's own SKU for this bare
/// board. <b>It is the only machine-readable product discriminator that exists across Seeed's eight
/// XVF3800 SKUs</b>, and until this rung nothing in this project read it. It is the rung that
/// catches a different Seeed product wearing the same USB ids — which the reSpeaker Flex, with its
/// interchangeable circular and linear arrays, is exactly the shape of.
/// </description></item>
/// <item><description>
/// <b>Does the board revision somebody recorded for this frame veto the write?</b> Fourth, before
/// the unit is spoken to, because a value a human typed needs no device to check and a refusal that
/// needs no device is the cheapest and clearest one available. See <see cref="ArrayBoardRevision"/>
/// — <b>the semantics of this rung are the one thing here the operator has not settled.</b>
/// </description></item>
/// <item><description>
/// <b>Does the unit answer the control tool at all?</b> Split in two, because "the tool is missing"
/// names something the reconciler will fix by itself and "the unit did not answer" names a cable.
/// </description></item>
/// <item><description>
/// <b>Is the build configuration exactly <see cref="XvfFirmwarePin.Profile"/>?</b> Upstream
/// publishes six-channel and 48 kHz builds under names one character apart, and the measured cost of
/// getting it wrong is a frame that enumerates, records, plays back and <i>silently cannot cancel
/// echo during a call</i> — upstream issue #31, where <c>AEC_AECCONVERGED</c> reads 0 at every
/// system delay on the 48 kHz builds. Nothing in ALSA, PipeWire or the mixer would report it.
/// </description></item>
/// <item><description>
/// <b>Do the two independent firmware readings agree?</b> <c>bcdDevice</c> from sysfs and
/// <c>VERSION</c> from the control interface are two routes to one fact and this build reads both
/// anyway. A unit on which they disagree is a unit this build cannot describe.
/// </description></item>
/// <item><description>
/// <b>Is the version known, and is it older than the target?</b> Newer is its own refusal and its
/// own message, because it is the case where every other check passing is most misleading: nothing
/// is wrong with the frame, and writing the pin would be a downgrade nobody asked for.
/// </description></item>
/// <item><description>
/// <b>Does the running firmware say it is driving this board's microphone array?</b>
/// <c>AEC_MIC_ARRAY_TYPE</c> and <c>AEC_MIC_ARRAY_GEO</c>, both in the pinned command map, neither
/// ever read by this project. They report the firmware's configuration rather than the board's, so
/// they cannot identify a revision — what they turn is "which profile is on this unit" from a
/// filename inference into a measurement. <b>Their expected values are published rather than
/// measured</b>, so an unreadable answer is recorded and not refused: see
/// <see cref="ArrayExpectation"/>.
/// </description></item>
/// <item><description>
/// <b>Is the complete observed tuple one entry on <see cref="Allowlist"/>?</b> Nine independent
/// checks passing is not the same claim as "this is a unit we recognise", and this rung is the
/// difference. It is also where a recorded board revision that <i>contradicts</i> what the hardware
/// says refuses, even when the recorded value is one this build knows.
/// </description></item>
/// </list>
/// <para>
/// <b>An unreadable identity is a refusal, not a shrug.</b> If the control tool is missing, or the
/// unit does not answer it, the build configuration cannot be read — and writing without it is
/// precisely the hopeful proceeding this class exists to stop. The tool is an ordinary resource
/// (<c>tool.xvf-host.installed</c>) that a converged frame has, so this refusal names something the
/// reconciler can fix.
/// </para>
/// <para>
/// <b>What the gate cannot see, stated rather than implied.</b> The board revision, from the unit —
/// see <see cref="ArrayIdentity"/>. Whether the unit in front of it is healthy: a board can report
/// the right ids, the right version and the right profile and still be a unit somebody has
/// half-bricked. And whether a <i>future</i> firmware version is safe: <see cref="Allowlist"/> is a
/// list a human edits, so it can only ever say what has already been established, which is the
/// property that makes it a gate rather than a guess.
/// </para>
/// </remarks>
public static class ArrayHardwareGate
{
    /// <summary>The <c>BLD_MSG</c> command — the build configuration the firmware was built for.</summary>
    public const string BuildConfigurationCommand = "BLD_MSG";

    /// <summary>The <c>BLD_REPO_HASH</c> command — a per-build fingerprint that names no build.</summary>
    public const string BuildHashCommand = "BLD_REPO_HASH";

    /// <summary>The <c>AEC_MIC_ARRAY_TYPE</c> command — resid 33, cmd 73, one int32, read-only.</summary>
    public const string MicArrayTypeCommand = "AEC_MIC_ARRAY_TYPE";

    /// <summary>The <c>AEC_MIC_ARRAY_GEO</c> command — resid 33, cmd 74, twelve floats, read-only.</summary>
    public const string MicArrayGeometryCommand = "AEC_MIC_ARRAY_GEO";

    /// <summary>What <c>AEC_MIC_ARRAY_TYPE</c> answers on a squarecular array. 1 is linear.</summary>
    public const int SquarecularArrayType = 2;

    /// <summary>How many rungs the ladder has, so a refusal can say which one it is.</summary>
    public const int Rungs = 10;

    /// <summary>The sentence that stops anybody reading a refusal as "the board is wrong".</summary>
    /// <remarks>
    /// It appears on every refusal, and it is load-bearing rather than boilerplate. Upstream issue
    /// #32 reports the pinned firmware not booting on a V1.1 board, so <i>refuse to write to the
    /// wrong revision</i> is the first gate anybody would reach for and the first thing anybody
    /// reading a refusal will assume happened. It cannot be written from the device, and saying so
    /// in every refusal is cheaper than one person, once, concluding that it silently was.
    /// </remarks>
    public const string RevisionNote =
        "board revision is not readable from the unit at all — not in the USB descriptors and not in the control "
        + "tool's command set — so the only revision this frame can gate on is one a person typed into "
        + ArrayBoardRevision.SettingKey + ", and that value is a veto rather than a permission";

    /// <summary>Half the width of the window a 66 mm square's coordinates must fall in, in metres.</summary>
    /// <remarks>
    /// <b>The window is wide because two published values disagree in the third decimal and both are
    /// right.</b> XMOS's shipped <c>mic_geometries.yaml</c> gives the squarecular set as ±0.0333;
    /// Seeed's own published <c>AEC_MIC_ARRAY_GEO</c> output on this product prints 0.033. A window
    /// of 0.030–0.036 accepts both and still rejects the only alternative XMOS ships: the linear
    /// geometry's ±0.04995 and ±0.01665 both fall outside it, which is the discrimination this
    /// check exists for.
    /// </remarks>
    public const double SquareMin = 0.030;

    /// <summary>The far edge of that window, in metres.</summary>
    public const double SquareMax = 0.036;

    /// <summary>How far from the plane a microphone may sit and still be in it, in metres.</summary>
    public const double PlaneTolerance = 0.001;

    /// <summary>
    /// Every complete unit this build has been told about. Rung 10 compares the whole tuple to this.
    /// </summary>
    /// <remarks>
    /// <b>One entry, and its single-ness is the point.</b> This project has held two arrays, both
    /// V1.1, both from batch 2605, both reporting <c>ua-io16-sqr</c>. Everything else upstream
    /// publishes — the six-channel builds, the 48 kHz builds, the whole I2S line, the reSpeaker Flex
    /// — is deliberately absent, and support arrives when hardware does.
    /// </remarks>
    public static IReadOnlyList<KnownArrayProfile> Allowlist { get; } =
    [
        new KnownArrayProfile
        {
            Name = "ReSpeaker XVF3800 USB 4-Mic Array, 16 kHz stereo, square array",
            VendorId = XvfArrayUsb.VendorId,
            ProductId = XvfArrayUsb.ProductId,
            ProductSkus = ["101991441"],
            BuildConfiguration = XvfFirmwarePin.Profile,
            Firmware = ["2 0 6", "2 0 10", "2 1 0"],
            BoardRevisions = ["V1.1"],
            MicArrayType = SquarecularArrayType,
            MicArrayProvenance = ArrayExpectation.Published,
            Evidence =
                "USB ids read from two arrays on the bench 2026-08-20 and reported identically in every upstream "
                + "issue. Serial head 101991441 is Seeed's Bazaar SKU for the bare board and is the head of both of "
                + "this project's serials and of the unit in upstream's own DFU guide. BLD_MSG ua-io16-sqr read from "
                + "Frame #1 2026-08-23 and from an unrelated 2.0.6 unit in upstream issue #19. Firmware 2 0 6 is what "
                + "both of this project's arrays shipped with, 2 0 10 is what Frame #1 reports and has been read "
                + "twice, 2 1 0 is the pinned target and has never been seen on hardware. Board revision V1.1 read "
                + "off the silkscreen of both boards by the operator's eye. AEC_MIC_ARRAY_TYPE 2 and the 66 mm "
                + "square geometry are Seeed's published output and XMOS's shipped default and have NEVER been read "
                + "on this project's hardware.",
        },
    ];

    /// <summary>
    /// Every firmware version any allowlist entry has been established on, in <c>xvf_host</c>'s spelling.
    /// </summary>
    /// <remarks>
    /// <b>Observed or pinned, and nothing else.</b> Derived from <see cref="Allowlist"/> rather than
    /// held beside it, because two lists that must agree are a list that will one day not. Upstream
    /// publishes other versions and they are deliberately absent: a version nobody here has seen is
    /// exactly the case this gate exists to refuse, and adding one is a source edit somebody has to
    /// mean, in the same shape as bumping the pin itself.
    /// </remarks>
    public static IReadOnlyList<string> KnownFirmware { get; } =
        [.. Allowlist.SelectMany(profile => profile.Firmware).Distinct(StringComparer.Ordinal)];

    /// <summary>Every nine-digit product code any allowlist entry covers.</summary>
    public static IReadOnlyList<string> KnownProductSkus { get; } =
        [.. Allowlist.SelectMany(profile => profile.ProductSkus).Distinct(StringComparer.Ordinal)];

    /// <summary>Every build configuration any allowlist entry is.</summary>
    public static IReadOnlyList<string> KnownBuildConfigurations { get; } =
        [.. Allowlist.Select(profile => profile.BuildConfiguration).Distinct(StringComparer.Ordinal)];

    /// <summary>Every board revision any allowlist entry has actually been established on.</summary>
    public static IReadOnlyList<string> KnownBoardRevisions { get; } =
        [.. Allowlist.SelectMany(profile => profile.BoardRevisions).Distinct(StringComparer.Ordinal)];

    /// <summary>Reads everything this frame can read about the attached unit.</summary>
    /// <remarks>
    /// Four process starts on a path that runs at most once in a frame's life, against a device that
    /// is about to be written to. The descriptor half needs no tool, no root and no process at all,
    /// so a frame with no control tool still produces a partial identity — which
    /// <see cref="Judge"/> then refuses on, rather than this method inventing the missing fields.
    /// </remarks>
    public static async Task<ArrayScan> ReadAsync(
        ISystemFiles files,
        XvfHost tool,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(tool);

        var attached = XvfArrayUsb.Attached(files);
        var enumerable = XvfArrayUsb.Enumerable(files);

        if (attached.Count != 1)
        {
            return new ArrayScan(attached, null, enumerable);
        }

        var device = attached[0];
        string? version = null;
        string? configuration = null;
        string? hash = null;
        int? arrayType = null;
        IReadOnlyList<double>? geometry = null;

        if (tool.Root() is { } root)
        {
            var reported = await tool
                .RunAsync(root, [XvfHost.VersionCommand], cancellationToken)
                .ConfigureAwait(false);

            version = XvfHost.Version(reported.StandardOutput) ?? XvfHost.Version(reported.Combined);
            configuration = await FieldAsync(tool, root, BuildConfigurationCommand, cancellationToken)
                .ConfigureAwait(false);
            hash = await FieldAsync(tool, root, BuildHashCommand, cancellationToken).ConfigureAwait(false);

            var typeReply = await tool
                .RunAsync(root, [MicArrayTypeCommand], cancellationToken)
                .ConfigureAwait(false);

            arrayType = MicArrayType(typeReply.StandardOutput) ?? MicArrayType(typeReply.Combined);

            var geometryReply = await tool
                .RunAsync(root, [MicArrayGeometryCommand], cancellationToken)
                .ConfigureAwait(false);

            geometry = MicArrayGeometry(geometryReply.StandardOutput)
                ?? MicArrayGeometry(geometryReply.Combined);
        }

        return new ArrayScan(
            attached,
            new ArrayIdentity(
                XvfArrayUsb.VendorId,
                XvfArrayUsb.ProductId,
                device.BcdDevice.Trim(),
                device.Serial.Trim(),
                XvfArrayUsb.Version(device.BcdDevice),
                version,
                configuration,
                hash,
                arrayType,
                geometry),
            enumerable);
    }

    /// <summary>
    /// Walks the ladder and returns the first rung that refuses, or the recognised ruling.
    /// </summary>
    /// <param name="scan">What <see cref="ReadAsync"/> saw.</param>
    /// <param name="pin">The images this build would write.</param>
    /// <param name="recordedRevision">
    /// Whatever an operator typed into <see cref="ArrayBoardRevision.SettingKey"/>, or null.
    /// </param>
    /// <param name="revision">
    /// The semantics rung 4 and rung 10 apply to <paramref name="recordedRevision"/>. Defaults to
    /// <see cref="ArrayBoardRevision.Default"/> — <b>the pending decision</b>.
    /// </param>
    /// <param name="allowlist">
    /// The entries every rung measures against. Defaults to <see cref="Allowlist"/>; nothing in the
    /// product passes anything else, and the suite does, so rung 10 can be watched refusing.
    /// </param>
    public static ArrayGateRuling Judge(
        ArrayScan scan,
        XvfFirmwarePin pin,
        string? recordedRevision = null,
        IBoardRevisionGate? revision = null,
        IReadOnlyList<KnownArrayProfile>? allowlist = null)
    {
        ArgumentNullException.ThrowIfNull(pin);

        var gate = revision ?? ArrayBoardRevision.Default;
        var target = pin.Target;

        // <b>The unions are derived here rather than read off the static properties, and the last
        // parameter exists so a test can prove rung 10 does something.</b> With one entry the union
        // of every entry's values *is* that entry, so a tuple that passes the nine per-field rungs
        // cannot fail the whole-profile one: rung 10 is unreachable today by construction, and
        // becomes load-bearing the instant a second entry lands. A gate whose last rung has never
        // been observed to fire is a gate nobody should trust, so the suite supplies two entries
        // that split the union and watches it refuse a tuple assembled from both.
        var entries = allowlist ?? Allowlist;
        var skus = entries.SelectMany(profile => profile.ProductSkus).Distinct(StringComparer.Ordinal).ToList();
        var revisions = entries.SelectMany(profile => profile.BoardRevisions).Distinct(StringComparer.Ordinal).ToList();
        var configurations = entries.Select(profile => profile.BuildConfiguration).Distinct(StringComparer.Ordinal).ToList();
        var firmware = entries.SelectMany(profile => profile.Firmware).Distinct(StringComparer.Ordinal).ToList();

        // ---- Rung 1: is anything there at all? -------------------------------------------------
        if (scan.Devices.Count == 0)
        {
            return Refuse(
                ArrayGateVerdict.NoArrayOnTheBus,
                1,
                "whether any microphone unit is plugged into this frame",
                scan.BusEnumerable
                    ? "no " + XvfArrayUsb.VendorId + ":" + XvfArrayUsb.ProductId + " device under "
                        + XvfArrayUsb.DevicesPath
                    : XvfArrayUsb.DevicesPath + " does not exist, so this machine publishes no USB devices at all",
                "exactly one " + XvfArrayUsb.VendorId + ":" + XvfArrayUsb.ProductId + " device",
                "This frame cannot find its microphone bar",
                [
                    "The frame looked for the microphone bar it listens through and found nothing plugged in.",
                    "Nothing has been changed and nothing is broken. The frame will not try to update a microphone "
                        + "it cannot find.",
                ],
                "Check that the microphone bar's cable is pushed all the way into both the bar and the frame, then "
                    + "tell whoever looks after your frames.",
                scan,
                pin,
                gate);
        }

        // ---- Rung 2: is there exactly one? -----------------------------------------------------
        if (scan.Devices.Count > 1)
        {
            return Refuse(
                ArrayGateVerdict.MoreThanOneArray,
                2,
                "whether exactly one microphone unit is plugged into this frame",
                scan.Devices.Count.ToString(CultureInfo.InvariantCulture) + " attached: " + DescribeAttached(scan.Devices),
                "exactly one, because the control tool has no device selector and opens whichever enumerates first",
                "This frame can see more than one microphone bar",
                [
                    "More than one microphone bar is plugged into this frame, and the frame cannot tell which one it "
                        + "would be updating.",
                    "Nothing has been changed. The frame will not guess.",
                ],
                "Unplug every microphone bar except the one this frame is meant to use, then tell whoever looks after "
                    + "your frames.",
                scan,
                pin,
                gate);
        }

        var unit = scan.Identity!.Value;

        // ---- Rung 3: does the serial decode, and is its product code one we know? ---------------
        if (unit.ProductSku is not { } sku)
        {
            return Refuse(
                ArrayGateVerdict.SerialUnreadable,
                3,
                "the serial number the microphone unit reports over USB",
                unit.Serial.Length == 0
                    ? "the unit reports no serial at all"
                    : "serial '" + unit.Serial + "', which is not "
                        + ArrayIdentity.SerialLength.ToString(CultureInfo.InvariantCulture) + " digits",
                ArrayIdentity.SerialLength.ToString(CultureInfo.InvariantCulture)
                    + " digits: product code(9) + batch(4) + unit(5)",
                "This frame cannot read the microphone bar's serial number",
                [
                    "Every microphone bar has a number printed into it electronically that says which product it is. "
                        + "This frame asked for that number and did not get one it could read.",
                    "That may mean the bar is a different product from the one this frame expects, or it may mean the "
                        + "connection is not reliable. Either way the frame will not update something it cannot "
                        + "identify.",
                ],
                "Unplug the microphone bar, wait a few seconds, plug it back in, and tell whoever looks after your "
                    + "frames if this message comes back.",
                scan,
                pin,
                gate);
        }

        if (!skus.Contains(sku, StringComparer.Ordinal))
        {
            return Refuse(
                ArrayGateVerdict.UnknownProductSku,
                3,
                "the product code at the head of the microphone unit's serial number",
                "product code " + sku + " (serial " + unit.Serial + ", batch " + (unit.Batch ?? "?") + ")",
                "one of " + string.Join(", ", skus),
                "The microphone bar in this frame is a different product",
                [
                    "The microphone bar says it is a different product from the one this frame was built and tested "
                        + "for. Several bars from the same maker look alike and plug in the same way, and this is not "
                        + "the one.",
                    "Nothing has been changed. Updating the wrong kind of bar can stop it working, so the frame has "
                        + "stopped instead.",
                ],
                "Tell whoever looks after your frames, and send them the technical detail below — it says exactly "
                    + "which bar this is.",
                scan,
                pin,
                gate);
        }

        // ---- Rung 4: does the recorded board revision veto? -------------------------------------
        // Fourth on purpose: it needs no device, so it is the cheapest refusal available and the
        // clearest. It is also the one rung whose semantics are still open — see ArrayBoardRevision.
        if (gate.BeforeReading(recordedRevision, revisions) is { Refuses: true } vetoed)
        {
            return Refuse(
                ArrayGateVerdict.BoardRevisionRefused,
                4,
                "the board revision somebody recorded for this frame in " + ArrayBoardRevision.SettingKey,
                vetoed.Technical,
                "one of " + string.Join(", ", revisions) + ", or nothing recorded at all",
                "This frame's microphone bar is a version this update has not been tried on",
                [
                    vetoed.Plain,
                    "The frame cannot check this for itself — the version is only printed on the bar, and no software "
                        + "can read printing. Somebody wrote it down, and this update has not been proven on it.",
                ],
                "Tell whoever looks after your frames. If the version written down is wrong, they can correct it and "
                    + "the frame will try again.",
                scan,
                pin,
                gate);
        }

        // ---- Rung 5: does it answer the control tool at all? -------------------------------------
        if (unit.ControlVersion is null
            && unit.BuildConfiguration is null
            && unit.BuildRepositoryHash is null
            && unit.MicArrayType is null)
        {
            return Refuse(
                ArrayGateVerdict.ControlToolMissing,
                5,
                "whether this frame has the program that talks to the microphone unit",
                "the control tool is not installed under " + XvfHost.AgentDirectory,
                "xvf_host installed and answering, which the resource tool.xvf-host.installed keeps true",
                "This frame is missing a piece of its own software",
                [
                    "To update a microphone bar the frame first has to ask the bar what it is. The small program that "
                        + "does the asking is not on this frame.",
                    "Nothing has been changed. The frame fetches that program by itself and will try again once it "
                        + "has it.",
                ],
                "Nothing, for now. If this message is still here tomorrow, tell whoever looks after your frames.",
                scan,
                pin,
                gate);
        }

        if (unit.ControlVersion is null || unit.BuildConfiguration is null)
        {
            return Refuse(
                ArrayGateVerdict.ControlSilent,
                5,
                "whether the microphone unit answers the control tool",
                "VERSION answered " + Quote(unit.ControlVersion) + " and " + BuildConfigurationCommand
                    + " answered " + Quote(unit.BuildConfiguration),
                "both answered",
                "The microphone bar is not answering this frame",
                [
                    "The microphone bar is plugged in, but it is not replying when the frame asks it questions.",
                    "Nothing has been changed. The frame will not update a bar that is not talking to it.",
                ],
                "Unplug the microphone bar's cable, wait ten seconds, and plug it back in. If this message comes "
                    + "back, tell whoever looks after your frames.",
                scan,
                pin,
                gate);
        }

        // ---- Rung 6: is it the audio topology the pinned image is built for? ---------------------
        if (!configurations.Contains(unit.BuildConfiguration, StringComparer.Ordinal))
        {
            return Refuse(
                ArrayGateVerdict.UnknownBuildConfiguration,
                6,
                "the build configuration the microphone unit's firmware reports (" + BuildConfigurationCommand + ")",
                unit.BuildConfiguration
                    + (string.Equals(unit.DescriptorVersion, target.Version, StringComparison.Ordinal)
                        ? " — note that this unit also reports the pinned version " + target.Version
                            + ", which is a measured collision rather than a coincidence: upstream publishes "
                            + "v2.1.0, v2.1.0_16k6ch and v2.1.0_48k2ch and all three answer VERSION 2 1 0, and "
                            + "issues #22 and #24 read VERSION 2 0 8 off the six-channel build. The version alone "
                            + "can never establish that a unit is on the target image"
                        : string.Empty),
                string.Join(", ", configurations),
                "The microphone bar is set up differently from the one this update was made for",
                [
                    "A microphone bar can be set up to hear in different ways — how many microphones it sends on, and "
                        + "how finely. This bar is set up in a way this update was not made for.",
                    "Nothing has been changed. Installing this update on it would change how the whole frame hears, "
                        + "and everything else on the frame is tuned for the way it hears now.",
                    "The version number on the bar cannot tell anybody this, which is why the frame checks it "
                        + "separately.",
                ],
                "Do not try again. Tell whoever looks after your frames and send them the technical detail below.",
                scan,
                pin,
                gate);
        }

        // ---- Rung 7: do the two independent firmware readings agree? -----------------------------
        // <b>VERSION is the authority and bcdDevice is the cheap corroborator, never the reverse.</b>
        // bcdDevice is 0xJJMP — major in the first byte, minor and patch in the two nibbles of the
        // second — so a minor or patch of 16 or more cannot be represented in it at all. 2.0.10 is
        // already 0x020A, which puts that ceiling one release away, and the day upstream ships a
        // 2.0.16 this rung will refuse a perfectly good unit because the descriptor cannot say what
        // the control interface can. That is the safe direction to fail in and it is not free: when
        // it happens, the fix is to stop decoding bcdDevice as a version rather than to widen this
        // comparison.
        if (unit.DescriptorVersion is not { } descriptor
            || !string.Equals(descriptor, unit.ControlVersion, StringComparison.Ordinal))
        {
            return Refuse(
                ArrayGateVerdict.ReadingsDisagree,
                7,
                "the microphone unit's firmware version, read two independent ways",
                "bcdDevice " + unit.BcdDevice + " = " + Quote(unit.DescriptorVersion) + ", VERSION = "
                    + Quote(unit.ControlVersion),
                "the two readings agree",
                "This frame is getting two different answers from the microphone bar",
                [
                    "The frame asked the microphone bar which version of its software it is running, in two different "
                        + "ways, and the two readings disagree.",
                    "Nothing has been changed. The frame does not know which answer is true, and it will not update "
                        + "something it cannot describe.",
                ],
                "Switch the frame off at the wall, wait ten seconds, and switch it back on. If this message comes "
                    + "back, tell whoever looks after your frames.",
                scan,
                pin,
                gate);
        }

        // ---- Rung 8: is the target a step forward, and is the version known? ---------------------
        // <b>Newer is asked first, and that ordering is the whole value of this rung.</b> Every
        // version this build knows is at or below the pin, so a unit running something newer is
        // always also a unit running something unknown — and answering "never been told about"
        // there is true, useless, and hides the one fact the operator actually needs: this frame is
        // ahead of the software, and somebody should hear about it. Asking "known?" first would
        // make FirmwareNewerThanTarget unreachable for ever.
        if (Compare(descriptor, target.Version) > 0)
        {
            return Refuse(
                ArrayGateVerdict.FirmwareNewerThanTarget,
                8,
                "whether the pinned firmware would move this unit forwards",
                "the unit runs " + descriptor + " and the pin is " + target.Version,
                "the unit running the same version as the pin, or an older one",
                "This frame's microphone is already newer than this update",
                [
                    "There is nothing wrong with your frame. Its microphone bar is already running a newer version of "
                        + "its own software than the one this update would install.",
                    "Nothing has been changed, and nothing needs to be. Installing this update would put an older "
                        + "version back, which the frame will not do on its own.",
                ],
                "Tell whoever looks after your frames that this frame is ahead of the update, and ask them to pass "
                    + "the technical detail below to whoever maintains FrameLink — this frame is newer than the "
                    + "software knows about, and that is something they need to hear.",
                scan,
                pin,
                gate);
        }

        if (!firmware.Contains(descriptor, StringComparer.Ordinal))
        {
            return Refuse(
                ArrayGateVerdict.UnknownFirmware,
                8,
                "the firmware version the microphone unit is running",
                descriptor,
                "one of " + string.Join(", ", firmware),
                "The microphone bar is running something this update has never been told about",
                [
                    "The microphone bar is running a version of its own software that this update has never been told "
                        + "about.",
                    "Nothing has been changed. The frame only updates microphone bars it recognises completely.",
                ],
                "Tell whoever looks after your frames and send them the technical detail below. They will know "
                    + "whether this bar can be supported.",
                scan,
                pin,
                gate);
        }

        // ---- Rung 9: does the firmware say it is driving this board's array? ---------------------
        // Published expectations, never measured here: a contradicting reading refuses, an
        // unreadable one does not. ArrayExpectation carries the whole of that reasoning.
        if (unit.MicArrayType is { } arrayType && arrayType != SquarecularArrayType)
        {
            return Refuse(
                ArrayGateVerdict.MicArrayTypeUnexpected,
                9,
                "the microphone arrangement the running firmware believes it is driving ("
                    + MicArrayTypeCommand + ")",
                arrayType.ToString(CultureInfo.InvariantCulture)
                    + (arrayType == 1 ? " (linear)" : " (undocumented)"),
                SquarecularArrayType.ToString(CultureInfo.InvariantCulture) + " (squarecular)",
                "The microphone bar has its microphones arranged differently",
                [
                    "The microphone bar says its microphones are laid out in a line, not in a square. The bars this "
                        + "frame is built for have four microphones at the corners of a square.",
                    "Nothing has been changed. A bar laid out differently hears direction differently, and this "
                        + "update was not made for it.",
                ],
                "Tell whoever looks after your frames and send them the technical detail below.",
                scan,
                pin,
                gate);
        }

        if (unit.MicArrayGeometry is { } geometry && !IsSquareGeometry(geometry))
        {
            return Refuse(
                ArrayGateVerdict.MicArrayGeometryUnexpected,
                9,
                "the microphone coordinates the running firmware reports (" + MicArrayGeometryCommand + ")",
                DescribeGeometry(geometry),
                "four microphones at the corners of a 66 mm square, z = 0 — each coordinate "
                    + SquareMin.ToString("0.000", CultureInfo.InvariantCulture) + " to "
                    + SquareMax.ToString("0.000", CultureInfo.InvariantCulture) + " m from centre",
                "The microphone bar's microphones are not where this update expects",
                [
                    "The microphone bar reports where each of its microphones physically sits, and they are not where "
                        + "the bars this frame is built for have them.",
                    "Nothing has been changed. Where the microphones sit is how the bar works out which direction a "
                        + "voice came from, and this update was not made for this arrangement.",
                ],
                "Tell whoever looks after your frames and send them the technical detail below.",
                scan,
                pin,
                gate);
        }

        // ---- Rung 10: is the whole tuple one entry on the allowlist? -----------------------------
        var matched = entries.FirstOrDefault(profile => profile.Covers(unit));

        if (matched is null)
        {
            return Refuse(
                ArrayGateVerdict.NotOnTheAllowlist,
                Rungs,
                "whether everything read from this unit describes one unit this build has been told about",
                unit.Describe(),
                string.Join("; ", entries.Select(profile => profile.Name)),
                "This frame does not recognise the microphone bar as a whole",
                [
                    "Each thing the frame asked the microphone bar about was familiar on its own, but the answers do "
                        + "not add up to any bar this frame has been told about.",
                    "Nothing has been changed. The frame only updates a microphone bar it recognises completely, and "
                        + "this one it does not.",
                ],
                "Tell whoever looks after your frames and send them the technical detail below.",
                scan,
                pin,
                gate);
        }

        if (gate.AgainstProfile(recordedRevision, matched) is { Refuses: true } contradicted)
        {
            return Refuse(
                ArrayGateVerdict.BoardRevisionRefused,
                Rungs,
                "the recorded board revision against the unit this frame can actually read",
                contradicted.Technical,
                "the recorded revision to be one '" + matched.Name + "' has been established on ("
                    + string.Join(", ", matched.BoardRevisions) + ")",
                "What is written down about this frame's microphone bar does not match the bar",
                [
                    contradicted.Plain,
                    "The frame cannot settle this by itself: the version is printed on the bar and no software can "
                        + "read printing, so somebody has to look.",
                ],
                "Tell whoever looks after your frames. Somebody needs to look at the writing on the microphone bar "
                    + "and check it against what was recorded.",
                scan,
                pin,
                gate);
        }

        return new ArrayGateRuling
        {
            Verdict = ArrayGateVerdict.Recognised,
            Rung = 0,
            Checked = "all " + Rungs.ToString(CultureInfo.InvariantCulture) + " checks",
            Found = unit.Describe(),
            Expected = matched.Name,
            Headline = "The microphone unit is one this build recognises.",
            Plain = ["Everything this frame can read about the microphone bar is something it has been told about."],
            Next = "Nothing.",
            Technical = TechnicalBlock(
                ArrayGateVerdict.Recognised,
                0,
                "all " + Rungs.ToString(CultureInfo.InvariantCulture) + " checks",
                unit.Describe(),
                matched.Name,
                new ArrayScan(scan.Devices, unit, scan.BusEnumerable),
                pin,
                gate,
                matched),
        };
    }

    /// <summary>Why a verdict refused, in a sentence an operator can act on.</summary>
    /// <remarks>
    /// The whole two-audience message, which is what goes into the <c>array-flash</c> event and what
    /// the Fleet Manager renders verbatim. <see cref="ArrayGateRuling.Screen"/> is the plain half on
    /// its own, for the panel a household reads.
    /// </remarks>
    public static string Explain(ArrayGateRuling ruling)
    {
        ArgumentNullException.ThrowIfNull(ruling);
        return ruling.Message;
    }

    /// <summary>Every attached unit named the way somebody standing at the frame could act on.</summary>
    public static string DescribeAttached(IReadOnlyList<XvfArrayDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        return devices.Count == 0
            ? "none"
            : string.Join(
                ", ",
                devices.Select(device =>
                    "bus " + device.Path + " serial " + (device.Serial.Length == 0 ? "(none)" : device.Serial)
                    + " bcdDevice " + device.BcdDevice));
    }

    /// <summary>Twelve floats as a person would read them, or a note that there were none.</summary>
    public static string DescribeGeometry(IReadOnlyList<double>? geometry) =>
        geometry is null || geometry.Count == 0
            ? "unreadable"
            : string.Join(" ", geometry.Select(value => value.ToString("0.000", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Whether twelve coordinates describe four microphones at the corners of this board's square.
    /// </summary>
    /// <remarks>
    /// Four triples, each z in the plane, each x and y between <see cref="SquareMin"/> and
    /// <see cref="SquareMax"/> from centre in absolute value, and all four sign combinations present
    /// exactly once. The sign check is what makes this a <i>square</i> rather than four microphones
    /// that happen to be the right distance out; without it, a build that put all four in one
    /// quadrant would pass.
    /// </remarks>
    public static bool IsSquareGeometry(IReadOnlyList<double>? geometry)
    {
        if (geometry is not { Count: 12 })
        {
            return false;
        }

        var corners = new HashSet<(bool X, bool Y)>();

        for (var index = 0; index < 12; index += 3)
        {
            var x = geometry[index];
            var y = geometry[index + 1];
            var z = geometry[index + 2];

            if (Math.Abs(z) > PlaneTolerance || !InSquare(x) || !InSquare(y))
            {
                return false;
            }

            corners.Add((x > 0, y > 0));
        }

        return corners.Count == 4;
    }

    /// <summary>
    /// One <c>NAME value</c> field out of a control-tool reply.
    /// </summary>
    /// <remarks>
    /// <b>The NUL padding is the whole reason this is not <c>XvfHost.Version</c>.</b> <c>BLD_MSG</c>,
    /// <c>BLD_HOST</c> and <c>BLD_MODIFIED</c> arrive padded to fixed widths — 39, 28 and 2 NULs
    /// respectively, measured on this project's own array — and the tool prints them raw, so they
    /// look like trailing spaces and are not. <c>string.Split</c> on whitespace does not remove
    /// them, and a value carrying its padding compares unequal to the same value read anywhere else,
    /// which would make this gate refuse every unit it was pointed at.
    /// </remarks>
    public static string? Field(string output, string command)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim('\0', '\r', ' ', '\t');

            if (!line.StartsWith(command, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[command.Length..].Trim('\0', '\r', ' ', '\t', ':');
            if (rest.Length > 0)
            {
                return rest;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>AEC_MIC_ARRAY_TYPE</c>'s single integer, or null when the reply does not carry one.
    /// </summary>
    /// <remarks>
    /// <b>Null is not a failure and must not be read as one.</b> No reading of this command has ever
    /// been taken on this project's hardware, so the exact text the compiled tool prints is unknown
    /// and a parser that could not cope would produce a refusal on every genuine board. What the
    /// caller does with null is <see cref="ArrayExpectation.Published"/>'s rule: record it, do not
    /// refuse on it.
    /// </remarks>
    public static int? MicArrayType(string output)
    {
        if (Field(output, MicArrayTypeCommand) is not { } value)
        {
            return null;
        }

        foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>AEC_MIC_ARRAY_GEO</c>'s twelve floats, or null when the reply does not carry twelve.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately tolerant of the layout, because the layout has never been seen.</b> Seeed's
    /// wiki prints this command's output as a bracketed, comma-separated block across four lines
    /// from the <i>Python</i> tool; this project calls the compiled binary, whose formatting nobody
    /// here has ever observed. So everything from the command name onwards is stripped of brackets
    /// and commas and read as whitespace-separated numbers, which parses both shapes and anything
    /// between them. Twelve is required exactly: eleven or thirteen is a reply this build does not
    /// understand, and guessing which one is missing would be worse than saying so.
    /// </remarks>
    public static IReadOnlyList<double>? MicArrayGeometry(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var start = output.IndexOf(MicArrayGeometryCommand, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var body = output[(start + MicArrayGeometryCommand.Length)..]
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace(',', ' ')
            .Replace(':', ' ')
            .Replace('\0', ' ');

        var values = new List<double>(12);

        foreach (var token in body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                break;
            }

            values.Add(parsed);

            if (values.Count == 12)
            {
                break;
            }
        }

        return values.Count == 12 ? values : null;
    }

    private static bool InSquare(double value) =>
        Math.Abs(value) >= SquareMin && Math.Abs(value) <= SquareMax;

    private static string Quote(string? value) => value is null ? "nothing" : "'" + value + "'";

    /// <summary>
    /// Two firmware versions in <c>xvf_host</c>'s spelling, compared field by field.
    /// </summary>
    /// <remarks>
    /// <b>Not a string comparison, and not <c>Version.Parse</c> either.</b> Ordinal ordering puts
    /// <c>2 0 10</c> before <c>2 0 6</c>, which would make the ladder's rung 8 report a downgrade as
    /// an upgrade on the exact pair of versions this project owns. Fields are compared numerically,
    /// a shorter version is padded with zeroes, and a field that will not parse compares equal —
    /// which is the safe direction, because rung 8's other arm has already established that the
    /// version is one this build knows.
    /// </remarks>
    private static int Compare(string left, string right)
    {
        var a = left.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var b = right.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var one = index < a.Length && int.TryParse(a[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;
            var two = index < b.Length && int.TryParse(b[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : 0;

            if (one != two)
            {
                return one < two ? -1 : 1;
            }
        }

        return 0;
    }

    private static ArrayGateRuling Refuse(
        ArrayGateVerdict verdict,
        int rung,
        string checked_,
        string found,
        string expected,
        string headline,
        IReadOnlyList<string> plain,
        string next,
        ArrayScan scan,
        XvfFirmwarePin pin,
        IBoardRevisionGate gate) => new()
        {
            Verdict = verdict,
            Rung = rung,
            Checked = checked_,
            Found = found,
            Expected = expected,
            Headline = headline,
            Plain = plain,
            Next = next,
            Technical = TechnicalBlock(verdict, rung, checked_, found, expected, scan, pin, gate, matched: null),
        };

    private static IReadOnlyList<string> TechnicalBlock(
        ArrayGateVerdict verdict,
        int rung,
        string checked_,
        string found,
        string expected,
        ArrayScan scan,
        XvfFirmwarePin pin,
        IBoardRevisionGate gate,
        KnownArrayProfile? matched)
    {
        var target = pin.Target;

        return
        [
            "  gate:       " + verdict,
            "  check:      "
                + (rung == 0
                    ? "all " + Rungs.ToString(CultureInfo.InvariantCulture) + " passed"
                    : rung.ToString(CultureInfo.InvariantCulture) + " of "
                        + Rungs.ToString(CultureInfo.InvariantCulture)) + " — " + checked_,
            "  found:      " + found,
            "  expected:   " + expected,
            "  unit:       " + (scan.Identity is { } unit ? unit.Describe() : "no single unit could be read"),
            "  bus:        " + DescribeAttached(scan.Devices),
            "  pin:        " + target.Name + ", firmware " + target.Version + ", sha256 " + target.Sha256,
            "  allowlist:  " + string.Join("; ", Allowlist.Select(profile => profile.Name))
                + (matched is null ? " (no entry matched)" : " (matched: " + matched.Name + ")"),
            "  revision:   " + gate.Semantics,
            "  note:       " + RevisionNote + ".",
            "  unverified: AEC_MIC_ARRAY_TYPE and AEC_MIC_ARRAY_GEO have never been read on this project's hardware; "
                + "their expected values are Seeed's published output and XMOS's shipped default, so a reading that "
                + "contradicts them refuses and a reading that cannot be parsed does not.",
            "  maintainer: this is a hardware set FrameLink has not been told about. Adding it means an entry on "
                + "ArrayHardwareGate.Allowlist with the evidence that established it, which is a source edit and a "
                + "release.",
        ];
    }

    private static async Task<string?> FieldAsync(
        XvfHost tool,
        string root,
        string command,
        CancellationToken cancellationToken)
    {
        var reply = await tool.RunAsync(root, [command], cancellationToken).ConfigureAwait(false);
        return Field(reply.StandardOutput, command) ?? Field(reply.Combined, command);
    }
}
