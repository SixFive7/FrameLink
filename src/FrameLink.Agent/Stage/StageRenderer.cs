using System.Globalization;
using System.Text;
using FrameLink.Agent.Firmware;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.State;

namespace FrameLink.Agent.Stage;

/// <summary>Colours for §2.7's two stages.</summary>
/// <remarks>
/// One accent per frame so that a glance from across a room already carries information, before a
/// word has been read — which is exactly why it has to be the accent of the frame the person is
/// looking at rather than of the last thing the Fleet Manager said about it (decision 83). Held to
/// the same bar as everything else (§7.4) — the console stage is not a lesser surface, it is the
/// same design language in a 16-colour medium, and the page beside it is sent the same value by
/// name rather than deriving its own.
/// </remarks>
public static class StagePalette
{
    /// <summary>Box drawing and rules.</summary>
    public const int Border = 240;

    /// <summary>Field labels.</summary>
    public const int Label = 245;

    /// <summary>Field values and body copy.</summary>
    public const int Body = 252;

    /// <summary>Headline text.</summary>
    public const int Headline = 231;

    /// <summary>Everything verified.</summary>
    public const int Green = 42;

    /// <summary>Work in progress.</summary>
    public const int Amber = 214;

    /// <summary>Waiting on a person.</summary>
    public const int Blue = 39;

    /// <summary>An authoritative refusal.</summary>
    public const int Red = 203;

    /// <summary>Silence.</summary>
    public const int Grey = 244;

    /// <summary>
    /// <b>The accent for a whole frame</b>, composed the way its headline is (decision 83).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes an <see cref="AgentStatus"/> and not a <see cref="DeviceCondition"/>, and that
    /// is the fix.</b> The rung is what the Fleet Manager said; whether the frame is repairing
    /// itself is <see cref="AgentStatus.Drifted"/>, deliberately not on the ladder (decision 82).
    /// Reading the rung alone painted an adopted frame that was putting a setting back in
    /// <see cref="Green"/> underneath <see cref="ReconcileVoice.RepairingHeadline"/>, and painted a
    /// frame that had given up in <see cref="Green"/> underneath
    /// <see cref="ReconcileVoice.StoppedHeadline"/> — the third rendering in this family to make
    /// the same claim, after a stopped frame's live progress animation and <c>c3116bc</c>'s
    /// headline. §2.6: a frame that is not running the product must not be rendered as one that is,
    /// in wording, in animation or in colour.
    /// </para>
    /// <para>
    /// <b>The conjunction is not repeated here.</b> <see cref="ReconcileVoice.Voice"/> makes it
    /// once and both the words and this colour are switches over its answer, so the two cannot come
    /// apart again — including in the two cases where the rung's own accent is still right: a frame
    /// with nothing wrong on it, and one the Fleet Manager has not cleared, whose blue or red says
    /// something the drift does not.
    /// </para>
    /// <para>
    /// <see cref="Amber"/> for a repairing frame is the accent the dead <c>DeviceState.Reconciling</c>
    /// arm carried and nothing could ever select; <see cref="Red"/> for a stopped one is already
    /// what <see cref="StageRenderer"/> paints its still glyph and its stopped bar with, so the
    /// screen now agrees with itself rather than with the handshake.
    /// </para>
    /// </remarks>
    public static int For(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        // Decision 91's screens sit above the ladder rather than on it, so the accent does too. A
        // frame asking somebody not to unplug it is a frame with nothing wrong on it — green would
        // be the ladder's honest answer and would be the wrong signal entirely, because the whole
        // point of the screen is that something needs a person. Blue is this palette's "waiting on a
        // person" and is exactly what this is; red is a write that did not come back or a unit that
        // is not answering; green is the one screen that genuinely says everything is well.
        if (status.ArrayFlash is { } prompt)
        {
            return prompt.Alarming ? Red
                : prompt.Phase is ArrayFlashPhase.Succeeded ? Green
                : Blue;
        }

        return ReconcileVoice.Voice(status) switch
        {
            StageVoice.Stopped => Red,
            StageVoice.Repairing => Amber,
            _ => ForRung(status.Condition),
        };
    }

    /// <summary>
    /// The name the browser stage sends for an accent this class chose (§2.7's second surface).
    /// </summary>
    /// <remarks>
    /// The page cannot use a 256-colour terminal index, and it must not re-derive the accent from
    /// anything of its own — one surface guessing is exactly how the console and the page come to
    /// disagree. So the composed value travels as a name and each medium renders it: this class in
    /// ANSI, <c>frame-stage.js</c> in CSS.
    /// </remarks>
    public static string NameOf(int accent) => accent switch
    {
        Green => "green",
        Amber => "amber",
        Blue => "blue",
        Red => "red",
        _ => "grey",
    };

    /// <summary>The rung's own accent — one colour per rung of §2.6's ladder.</summary>
    /// <remarks>
    /// Private, and it stays private. Every accent on the screen is the whole frame's
    /// (<see cref="For(AgentStatus)"/>); a caller able to ask for the rung's colour alone is a
    /// caller able to reintroduce decision 83's defect without noticing.
    /// </remarks>
    private static int ForRung(DeviceCondition condition) => condition.State switch
    {
        DeviceState.InSync => Green,
        DeviceState.VersionMismatch => Amber,
        DeviceState.ControlNotConfigured => Blue,
        DeviceState.NotAdopted => condition.Cause is "blocked" or "bad-signature" ? Red : Blue,
        _ => Grey,
    };
}

/// <summary>
/// Paints one whole frame of §2.7's console stage.
/// </summary>
/// <remarks>
/// <para>
/// A pure function: status in, one string out. That is deliberate and it is what makes the screen
/// testable at all — every claim §2.7 makes about what the repair screen shows becomes an
/// assertion over the returned text, with no terminal, no timing and no device involved.
/// </para>
/// <para>
/// It repaints the whole screen every tick, from cursor-home, rather than scrolling. §2.7 is
/// explicit that this is "a designed terminal interface with colour, box drawing and animated
/// progress, <b>not log spew</b>", and a scrolling surface cannot be that: it has no layout, only
/// a history.
/// </para>
/// </remarks>
public static class StageRenderer
{
    /// <summary>Frames of the activity spinner.</summary>
    private static readonly string[] SpinnerFrames =
        ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private const string TopLeft = "╭";
    private const string TopRight = "╮";
    private const string BottomLeft = "╰";
    private const string BottomRight = "╯";
    private const string Horizontal = "─";
    private const string Vertical = "│";
    private const string RuleLeft = "├";
    private const string RuleRight = "┤";
    private const int LabelWidth = 11;
    private const int MinimumColumns = 40;
    private const int MinimumRows = 12;

    /// <summary>Renders the frame, including the cursor and repaint control sequences.</summary>
    /// <param name="status">What to say.</param>
    /// <param name="now">
    /// The instant being rendered. Passed in rather than read, so the countdown in §2.7 item 6
    /// genuinely counts down and the whole renderer stays a pure function of its arguments.
    /// </param>
    /// <param name="tick">Animation frame counter.</param>
    /// <param name="columns">Terminal width.</param>
    /// <param name="rows">Terminal height.</param>
    /// <param name="colour">Whether to emit escape sequences.</param>
    public static string Render(
        AgentStatus status,
        DateTimeOffset now,
        int tick,
        int columns,
        int rows,
        bool colour)
    {
        ArgumentNullException.ThrowIfNull(status);

        columns = Math.Max(MinimumColumns, columns);
        rows = Math.Max(MinimumRows, rows);

        var inner = columns - 4;
        var accent = StagePalette.For(status);
        var blank = Compose([], inner, colour);

        var head = BuildHead(status, tick, inner, accent, colour);
        var foot = BuildFoot(status, now, tick, inner, accent, colour);

        // The identity block is pinned to the bottom and the narration sits above it, with the
        // slack split roughly one-third above and two-thirds below. Splitting it rather than
        // dumping it all in the middle is what stops a tall panel — the DSI console is fifty rows
        // — from rendering as a caption stranded at the top of a void.
        var available = rows - 2;
        var footRoom = Math.Min(foot.Count, available);
        var headRoom = available - footRoom;
        var slack = Math.Max(0, headRoom - head.Count);
        var above = slack / 3;

        var body = new List<string>(available);
        for (var index = 0; index < above; index++)
        {
            body.Add(blank);
        }

        for (var index = 0; index < head.Count && body.Count < headRoom; index++)
        {
            body.Add(head[index]);
        }

        while (body.Count < headRoom)
        {
            body.Add(blank);
        }

        for (var index = foot.Count - footRoom; index < foot.Count; index++)
        {
            body.Add(foot[index]);
        }

        var frame = new StringBuilder(columns * rows * 2);
        frame.Append(Ansi.HideCursor).Append(Ansi.Home);
        frame.Append(TopBorder(status, columns, accent, colour)).Append('\n');

        var border = Style(Vertical, StagePalette.Border, colour);
        foreach (var line in body)
        {
            frame.Append(border).Append(' ').Append(line).Append(' ').Append(border).Append('\n');
        }

        frame.Append(BottomBorder(columns, colour));
        frame.Append(Ansi.ClearToEnd);

        return frame.ToString();
    }

    /// <summary>The activity spinner glyph for a tick.</summary>
    public static string Spinner(int tick) =>
        SpinnerFrames[((tick % SpinnerFrames.Length) + SpinnerFrames.Length) % SpinnerFrames.Length];

    /// <summary>Renders a determinate progress bar.</summary>
    public static string Bar(double fraction, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width, MidpointRounding.AwayFromZero);
        return new string('█', filled) + new string('░', width - filled);
    }

    /// <summary>
    /// Renders an indeterminate bar whose highlight travels with the tick.
    /// </summary>
    /// <remarks>
    /// §2.7 item 6: a pause must never look like a hang. Something has to be moving on screen even
    /// when there is no measurable progress to report, or a frame waiting patiently and a frame
    /// that has died are the same picture.
    /// </remarks>
    public static string Marquee(int tick, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        const int HighlightWidth = 4;
        var span = width + HighlightWidth;
        var offset = ((tick % span) + span) % span - HighlightWidth;

        var cells = new char[width];
        for (var index = 0; index < width; index++)
        {
            cells[index] = index >= offset && index < offset + HighlightWidth ? '█' : '░';
        }

        return new string(cells);
    }

    /// <summary>
    /// The firmware screen, which replaces the narration rather than sitting beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is exclusive, and that is the design.</b> Everything else this renderer draws is
    /// addressed to somebody who came to find out what is wrong with a frame. This is addressed to
    /// whoever happens to be in the room, about one decision, with a consequence measured in broken
    /// hardware — and a screen that put "please do not unplug this" underneath four fields about
    /// resource attempts would be asking them to read past the part that matters.
    /// </para>
    /// <para>
    /// <b>Nothing here is composed.</b> The headline, every line and the affordance arrive already
    /// worded from <see cref="ArrayFlashVoice"/>, which is the same record the browser stage sends
    /// to the page — so the two surfaces cannot come to say different things about the same write,
    /// which is decision 83's rule applied before there is a second implementation to disagree with.
    /// </para>
    /// </remarks>
    private static List<string> BuildFlash(ArrayFlashPrompt prompt, int tick, int inner, int accent, bool colour)
    {
        var lines = new List<string>(prompt.Lines.Count + 6)
        {
            Compose([], inner, colour),
            Compose(
                [
                    // Still, never a spinner, on every one of these screens. Decision 70's rule is
                    // that nothing may animate work that is not happening, and the one screen here
                    // where work *is* happening is the one nobody may act on — so a moving glyph
                    // there would be inviting attention to a screen whose whole message is "wait".
                    new Run("■  ", accent),
                    new Run(prompt.Headline, StagePalette.Headline, Bold: true),
                ],
                inner,
                colour),
            Compose([], inner, colour),
        };

        // The bar, above the sentences rather than below them, because it is the part somebody
        // glances at from across the room and the sentences are what they read when they come
        // closer. It is the one moving thing on this screen and it is not an exception to decision
        // 70: what it animates is a write that is genuinely happening, measured against the pinned
        // image's own byte count while bytes are moving, and against nothing at all when they are
        // not — which is why the indeterminate form is used for the stages that have no quantity,
        // rather than a determinate bar frozen at whatever the download left it at.
        if (prompt.Progress is { } progress)
        {
            // Narrower than the activity line's bar, for the same reason the countdown's is: this
            // one carries the longest caption on the screen — a percentage *and* a byte count — and
            // the caption is the part the operator asked for. A bar sized like the others would push
            // the numbers off the right edge on an 80-column console.
            var barWidth = Math.Max(8, inner - 56);

            lines.Add(Compose(
                [
                    new Run(Pad("Updating", LabelWidth), StagePalette.Label),
                    new Run(
                        (progress.Fraction is { } fraction ? Bar(fraction, barWidth) : Marquee(tick, barWidth)) + "  ",
                        accent),
                    new Run(FlashProgressText(progress), StagePalette.Body),
                ],
                inner,
                colour));

            lines.Add(Compose([], inner, colour));
        }

        foreach (var line in prompt.Lines)
        {
            AddField(lines, string.Empty, line, inner, colour);
        }

        if (ArrayFlashVoice.HoldLine(prompt) is { Length: > 0 } hold)
        {
            lines.Add(Compose([], inner, colour));
            AddField(lines, string.Empty, hold, inner, colour);
        }

        return lines;
    }

    /// <summary>
    /// The short caption beside a firmware write's bar — the numbers, not the sentence.
    /// </summary>
    /// <remarks>
    /// <b>The words are already above it and are not repeated here.</b> The stage is spelled out in
    /// plain language in the screen's first line, worded by <c>ArrayFlashVoice</c> for a family
    /// member; this is the caption a bar carries, so it is the percentage while there is one, the
    /// byte count when it fits, and the elapsed seconds when there is nothing else — which is what
    /// says a still bar is a wait rather than a hang.
    /// </remarks>
    private static string FlashProgressText(ArrayFlashProgress progress)
    {
        var seconds = (int)progress.Elapsed.TotalSeconds;

        if (progress.Percent is { } percent && progress.Fraction is not null)
        {
            return progress.BytesWritten is { } written && progress.BytesTotal > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{percent}% — {written:N0} of {progress.BytesTotal:N0} bytes")
                : string.Create(CultureInfo.InvariantCulture, $"{percent}%");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds}s so far");
    }

    private static List<string> BuildHead(AgentStatus status, int tick, int inner, int accent, bool colour)
    {
        if (status.ArrayFlash is { } prompt)
        {
            return BuildFlash(prompt, tick, inner, accent, colour);
        }

        // A stopped frame gets a still glyph in the accent of a refusal, not a turning spinner.
        // The spinner is the headline's half of the same promise the marquee makes below: that
        // something is happening. Nothing is (decision 70).
        var givenUp = ReconcileVoice.HasStopped(status);

        var lines = new List<string>(12)
        {
            Compose([], inner, colour),
            Compose(
                [
                    givenUp
                        ? new Run("■  ", StagePalette.Red)
                        : new Run(Spinner(tick) + "  ", accent),
                    new Run(ReconcileVoice.Headline(status), StagePalette.Headline, Bold: true),
                ],
                inner,
                colour),
            Compose([], inner, colour),
        };

        // The headline above already says what was detected (§2.7 item 1) in the common case, so
        // the field is only spelled out when it adds something the headline did not — and both
        // that judgement and the headline it is made against are composed in ReconcileVoice, so
        // the page beside this console cannot come to a different one.
        if (ReconcileVoice.Detected(status) is { Length: > 0 } detected)
        {
            AddField(lines, "Detected", detected, inner, colour);
        }

        AddField(lines, "Why", ReconcileVoice.WhyItMatters(status) ?? ReconcileVoice.Detail(status), inner, colour);

        if (status.Narration.Action is { Length: > 0 } action)
        {
            AddField(lines, "Doing", action, inner, colour);
        }

        if (status.Narration.ActionGloss is { Length: > 0 } gloss)
        {
            AddField(lines, string.Empty, gloss, inner, colour);
        }

        if (status.Condition.ServerMessage is { Length: > 0 } message
            && !string.Equals(message, status.Narration.Action, StringComparison.Ordinal))
        {
            AddField(lines, "Server", message, inner, colour);
        }

        // §2.7 item 5, in the operator's own words: one item at a time, with its attempt count.
        // It lives here rather than on the activity line because this is where a value gets
        // wrapped instead of truncated, and a resource name is routinely longer than the space
        // left beside a progress bar — a truncated name is exactly the field somebody reads when
        // they are trying to work out which setting is failing.
        if (ReconcileVoice.ProgressLine(status) is { Length: > 0 } progress)
        {
            AddField(lines, "Progress", progress, inner, colour);
        }

        // §2.7 items 7 and 8. Placed with the narration rather than beside the activity bar
        // because it is the answer to "what happens next", and what happens next is nothing until
        // a person acts.
        //
        // Three sentences and they are deliberately three: what stopped and what it wanted
        // (rendered from the delta §2.5 rung 2 recorded), whether anybody has been told, and who
        // to ask. Only the last of those survives the Fleet Manager being unreachable, which is
        // why it comes off the frame's own state rather than off the link (decision 71).
        if (ReconcileVoice.HasStopped(status))
        {
            // Gated on the fact rather than on the sentence: a frame can be known to have stopped
            // before the pass that stopped it has published which resource it was, and the two
            // sentences that follow — has anybody been told, who to ask — are exactly as true then.
            if (ReconcileVoice.StoppedLine(status) is { Length: > 0 } stopped)
            {
                AddField(lines, "Stopped", stopped, inner, colour);
            }

            // The plain half (§2.7 item 7, and the operator's "as much relevant information as
            // possible"). It is written for the person in the room and carries no numbers at all;
            // the technical block below carries nothing else. ArrayGateRuling's refusals are the
            // pattern, and the two halves are kept apart there for the same reason.
            foreach (var line in ReconcileVoice.SupportPlain(status))
            {
                AddField(lines, string.Empty, line, inner, colour);
            }

            if (status.Reconcile.EscalationLine is { Length: > 0 } escalation)
            {
                AddField(lines, string.Empty, escalation, inner, colour);
            }

            AddField(lines, string.Empty, ReconcileVoice.ContactLine(status.Contact), inner, colour);

            // §2.7 item 9, and the sentence is chosen from what the agent found rather than from
            // what was assumed (decision 77). A frame with a touchscreen says how to use it; a
            // frame without one — every frame whose panel overlay has not been applied yet — names
            // the Fleet Manager, which is then true rather than a hedge.
            AddField(lines, string.Empty, ReconcileVoice.RetryLine(status.Touch), inner, colour);

            // The technical half, last, because it is the part nobody in the room reads: it is
            // there to be photographed. Every line is already a complete `key: value` pair — the
            // splitting happens in ReconcileVoice so both surfaces show the same block — so this
            // never hands a multi-line value to a wrapper that would flatten it.
            var technical = ReconcileVoice.SupportTechnical(status);
            if (technical.Count > 0)
            {
                lines.Add(Compose([], inner, colour));
                AddField(lines, string.Empty, ReconcileVoice.TechnicalHeading, inner, colour);

                foreach (var line in technical)
                {
                    AddField(lines, string.Empty, line, inner, colour);
                }
            }
        }

        return lines;
    }

    private static List<string> BuildFoot(
        AgentStatus status,
        DateTimeOffset now,
        int tick,
        int inner,
        int accent,
        bool colour)
    {
        var lines = new List<string>(12) { Rule(inner, colour) };

        AddField(lines, "Device", status.DeviceId, inner, colour);

        if (status.HardwareSerial is { Length: > 0 } serial)
        {
            AddField(lines, "Serial", serial, inner, colour);
        }

        AddField(
            lines,
            "Server",
            status.CurrentEndpoint?.ToString()
                ?? (status.Endpoints.Count > 0 ? status.Endpoints[0].ToString() : "not configured"),
            inner,
            colour);

        foreach (var resource in status.Resources)
        {
            AddField(lines, resource.Name, DescribeResource(resource), inner, colour);
        }

        lines.Add(Compose([], inner, colour));
        lines.Add(BuildActivity(status, now, tick, inner, accent, colour));

        return lines;
    }

    private static string BuildActivity(
        AgentStatus status,
        DateTimeOffset now,
        int tick,
        int inner,
        int accent,
        bool colour)
    {
        var barWidth = Math.Max(8, inner - 34);

        // §2.7 item 9. A finger is on the screen right now, so this outranks even the stopped line
        // below it: the person doing it has to be able to see that it is being counted, or they let
        // go at two seconds and conclude the screen is dead.
        //
        // It is not an exception to decision 70. That rule forbids animating work that is not
        // happening, and what moves here is the person's own hold — determinate, measured against
        // the instant being rendered rather than against a tick counter, and gone the moment they
        // lift. Nothing about it claims the reconciler is doing anything.
        if (status.Touch.HoldingSince is not null)
        {
            var left = Math.Max(0, (int)Math.Ceiling(status.Touch.Remaining(now).TotalSeconds));

            // What the hold means is decided in one place and rendered here, so the label above
            // the bar is always the thing the sentence three lines up asked for. A bar labelled
            // "Try again" counting out a firmware approval would be the exact defect decision 77
            // set out to avoid, in words instead of in coordinates.
            var label = status.ArrayFlash is { Affordance: { Length: > 0 } affordance } ? affordance : "Try again";

            return Compose(
                [
                    new Run(Pad(label, LabelWidth), StagePalette.Label),
                    new Run(Bar(status.Touch.Progress(now), barWidth) + "  ", StagePalette.Blue),
                    new Run(
                        left <= 0
                            ? "keep holding"
                            : string.Create(CultureInfo.InvariantCulture, $"keep holding — {left}s"),
                        StagePalette.Body),
                ],
                inner,
                colour);
        }

        // §2.7 item 7, and the single most consequential branch in this method: it comes first,
        // and it does not animate.
        //
        // What it replaces was a frame that painted "Attempt 5 of 5" beside a *travelling
        // marquee* — an animation whose entire purpose is to prove that a pause is not a hang —
        // for a resource the loop had permanently stopped touching. ReconcileLoop narrates the
        // worst status in a pass and a resource that has given up sorts worst, so that picture was
        // redrawn on every boot, for ever, showing work that was not happening. That is what made
        // a frame look like it was rebooting endlessly (decision 70).
        //
        // Placed above the countdown deliberately as well: a stopped frame has no reboot coming,
        // so there is nothing for a countdown to count.
        if (ReconcileVoice.HasStopped(status))
        {
            var tries = ReconcileVoice.Stopped(status) is { Attempts: > 0 } resource
                ? resource.Attempts
                : status.Reconcile.Attempt;

            return Compose(
                [
                    new Run(Pad("Stopped", LabelWidth), StagePalette.Label),
                    new Run(Bar(1, barWidth) + "  ", StagePalette.Red),
                    new Run(
                        tries == 1
                            ? "gave up after 1 try — waiting for a person"
                            : string.Create(
                                CultureInfo.InvariantCulture,
                                $"gave up after {tries} tries — waiting for a person"),
                        StagePalette.Body),
                ],
                inner,
                colour);
        }

        if (status.UpdateProgress is { } progress)
        {
            return Compose(
                [
                    new Run(Pad("Updating", LabelWidth), StagePalette.Label),
                    new Run(Bar(progress, barWidth) + "  ", accent),
                    new Run(
                        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(progress * 100)}%"),
                        StagePalette.Body),
                ],
                inner,
                colour);
        }

        // §2.7 item 4: the countdown before the verifying reboot, with the skip named on screen.
        // It outranks everything below because it is the one moment the screen is asking the
        // person in front of it for something rather than telling them.
        if (status.Reconcile.Countdown is { } countdown)
        {
            var left = Math.Max(0, (int)Math.Ceiling(countdown.Remaining(now).TotalSeconds));

            // A narrower bar than the other activity lines, because this one carries the longest
            // caption on the screen and the caption is the part that matters: a bar that pushed
            // "Restart now" off the right edge would take the affordance away on exactly the
            // screen §2.7 says must offer it.
            var countdownBar = Math.Max(8, inner - 60);

            return Compose(
                [
                    new Run(Pad("Restarting", LabelWidth), StagePalette.Label),
                    new Run(Bar(countdown.Elapsed(now), countdownBar) + "  ", accent),
                    new Run(
                        countdown.Skippable
                            ? string.Create(CultureInfo.InvariantCulture, $"in {left}s — touch \"Restart now\" to skip")
                            : string.Create(CultureInfo.InvariantCulture, $"in {left}s"),
                        StagePalette.Body),
                ],
                inner,
                colour);
        }

        // §2.7 items 5 and 6, for the reconciler's own retry schedule. Separate from the
        // connection's, below, because a frame can be waiting on both at once and they mean
        // different things: one is "the server is not answering", the other is "this setting
        // would not stick".
        // The attempt count itself has moved up into the narration block, where it is wrapped
        // rather than truncated and where §2.7 item 5's "one item at a time" reads as a sentence.
        // What is left here is item 6's job alone — proof that a wait is a wait and not a hang.
        if (status.Reconcile.Attempt > 0)
        {
            if (status.Reconcile.BackoffEndsAt is not { } endsAt || status.Reconcile.BackoffTotal <= TimeSpan.Zero)
            {
                return Compose(
                    [
                        new Run(Pad("Working", LabelWidth), StagePalette.Label),
                        new Run(Marquee(tick, barWidth) + "  ", accent),
                        new Run(status.Reconcile.Resource ?? "working", StagePalette.Body),
                    ],
                    inner,
                    colour);
            }

            var left = endsAt - now;
            left = left < TimeSpan.Zero ? TimeSpan.Zero
                : left > status.Reconcile.BackoffTotal ? status.Reconcile.BackoffTotal
                : left;

            var done = (status.Reconcile.BackoffTotal - left).Ticks / (double)status.Reconcile.BackoffTotal.Ticks;

            return Compose(
                [
                    new Run(Pad("Waiting", LabelWidth), StagePalette.Label),
                    new Run(Bar(done, barWidth) + "  ", accent),
                    new Run(
                        string.Create(CultureInfo.InvariantCulture, $"trying again in {Math.Max(0, (int)Math.Ceiling(left.TotalSeconds))}s"),
                        StagePalette.Body),
                ],
                inner,
                colour);
        }

        // §2.7 lists the attempt number (item 5) and the backoff state (item 6) as two separate
        // things the screen must show. The attempt number therefore appears whenever the agent is
        // retrying at all — a frame that is mid-attempt, with no wait to count down, still says
        // which attempt it is on.
        if (status.Attempt > 0)
        {
            var attempt = Pad(string.Create(CultureInfo.InvariantCulture, $"Attempt {status.Attempt}"), LabelWidth);

            if (status.BackoffTotal <= TimeSpan.Zero)
            {
                return Compose(
                    [
                        new Run(attempt, StagePalette.Label),
                        new Run(Marquee(tick, barWidth) + "  ", accent),
                        new Run("trying now", StagePalette.Body),
                    ],
                    inner,
                    colour);
            }

            var remaining = Remaining(status, now);
            var elapsed = status.BackoffTotal - remaining;
            var fraction = elapsed.Ticks / (double)status.BackoffTotal.Ticks;

            return Compose(
                [
                    new Run(attempt, StagePalette.Label),
                    new Run(Bar(fraction, barWidth) + "  ", accent),
                    new Run(
                        string.Create(CultureInfo.InvariantCulture, $"next try in {Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds))}s"),
                        StagePalette.Body),
                ],
                inner,
                colour);
        }

        return Compose(
            [
                new Run(Pad(status.Connected ? "Connected" : "Working", LabelWidth), StagePalette.Label),
                new Run(Marquee(tick, barWidth) + "  ", accent),
                new Run(status.ProductRuns ? "photos are showing" : "the screen belongs to the agent", StagePalette.Body),
            ],
            inner,
            colour);
    }

    private static TimeSpan Remaining(AgentStatus status, DateTimeOffset now)
    {
        if (status.BackoffEndsAt is not { } endsAt)
        {
            return status.BackoffTotal;
        }

        var remaining = endsAt - now;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero
            : remaining > status.BackoffTotal ? status.BackoffTotal
            : remaining;
    }

    /// <summary>
    /// One resource's line in the foot list, in words rather than in status names.
    /// </summary>
    /// <remarks>
    /// The stopped case and the in-progress case are separate arms and read differently on
    /// purpose — "gave up after 3 tries" and "attempt 2 of 3" describe different futures, and the
    /// old shared arm rendered both as the enum member's name followed by an attempt count, which
    /// made a resource nothing was touching look exactly like one being worked on (decision 70).
    /// </remarks>
    private static string DescribeResource(ResourceStatus resource) => resource.Kind switch
    {
        ResourceStatusKind.InSync => "in sync",

        ResourceStatusKind.Blocked => string.Create(
            CultureInfo.InvariantCulture,
            $"waiting for {resource.BlockedBy ?? "something else"}"),

        _ when ReconcileVoice.HasGivenUp(resource.Kind) => string.Create(
            CultureInfo.InvariantCulture,
            $"gave up after {resource.Attempts} {(resource.Attempts == 1 ? "try" : "tries")} — {resource.Delta ?? "no detail"}"),

        _ when resource.AttemptBudget > 0 => string.Create(
            CultureInfo.InvariantCulture,
            $"attempt {resource.Attempts} of {resource.AttemptBudget} — {resource.Delta ?? "no detail"}"),

        _ => string.Create(
            CultureInfo.InvariantCulture,
            $"attempt {resource.Attempts} — {resource.Delta ?? "no detail"}"),
    };

    private static void AddField(List<string> lines, string label, string value, int inner, bool colour)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var wrapWidth = Math.Max(1, inner - LabelWidth);
        var first = true;

        foreach (var chunk in Wrap(value, wrapWidth))
        {
            lines.Add(Compose(
                [
                    new Run(Pad(first ? label : string.Empty, LabelWidth), StagePalette.Label),
                    new Run(chunk, StagePalette.Body),
                ],
                inner,
                colour));

            first = false;
        }
    }

    private static IEnumerable<string> Wrap(string value, int width)
    {
        var remaining = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (remaining.Length == 0)
        {
            yield break;
        }

        while (remaining.Length > width)
        {
            var cut = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));
            if (cut <= 0)
            {
                cut = width;
            }

            yield return remaining[..cut].TrimEnd();
            remaining = remaining[cut..].TrimStart();
        }

        yield return remaining;
    }

    private static string Pad(string text, int width) =>
        text.Length >= width ? text[..width] : text + new string(' ', width - text.Length);

    private static string Rule(int inner, bool colour) =>
        Style(RuleLeft + new string('─', Math.Max(0, inner - 2)) + RuleRight, StagePalette.Border, colour);

    private static string TopBorder(AgentStatus status, int columns, int accent, bool colour)
    {
        const string Title = " FrameLink ";

        var version = $" {ShortVersion(status.AgentVersion)} ";
        var fillWidth = Math.Max(0, columns - 2 - 1 - Title.Length - version.Length - 1);

        var builder = new StringBuilder();
        builder.Append(Style(TopLeft + Horizontal, StagePalette.Border, colour));
        builder.Append(Style(Title, accent, colour, bold: true));
        builder.Append(Style(new string('─', fillWidth), StagePalette.Border, colour));
        builder.Append(Style(version, StagePalette.Label, colour));
        builder.Append(Style(Horizontal + TopRight, StagePalette.Border, colour));
        return builder.ToString();
    }

    /// <summary>
    /// Renders a build version for the title bar, shortening the commit to its usual seven
    /// characters rather than truncating a forty-character hash at an arbitrary column.
    /// </summary>
    public static string ShortVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        const int CommitLength = 7;
        const int MaximumWidth = 24;

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0 && version.Length - plus - 1 > CommitLength)
        {
            version = version[..(plus + 1 + CommitLength)];
        }

        return version.Length > MaximumWidth ? version[..MaximumWidth] : version;
    }

    private static string BottomBorder(int columns, bool colour) =>
        Style(BottomLeft + new string('─', columns - 2) + BottomRight, StagePalette.Border, colour);

    private static string Compose(IReadOnlyList<Run> runs, int width, bool colour)
    {
        var builder = new StringBuilder(width * 2);
        var used = 0;

        foreach (var run in runs)
        {
            if (used >= width)
            {
                break;
            }

            var text = run.Text;
            if (used + text.Length > width)
            {
                var room = width - used;
                text = room <= 1 ? text[..room] : string.Concat(text.AsSpan(0, room - 1), "…");
            }

            builder.Append(Style(text, run.Colour, colour, run.Bold));
            used += text.Length;
        }

        if (used < width)
        {
            builder.Append(new string(' ', width - used));
        }

        return builder.ToString();
    }

    private static string Style(string text, int colour, bool enabled, bool bold = false) =>
        enabled ? (bold ? Ansi.Bold : string.Empty) + Ansi.Foreground(colour) + text + Ansi.Reset : text;

    private readonly record struct Run(string Text, int Colour, bool Bold = false);
}
