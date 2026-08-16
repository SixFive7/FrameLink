using System.Globalization;
using System.Text;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.State;

namespace FrameLink.Agent.Stage;

/// <summary>Colours for each rung of the device state ladder.</summary>
/// <remarks>
/// One accent per rung so that a glance from across a room already carries information, before a
/// word has been read. Held to the same bar as everything else (§7.4) — the console stage is not
/// a lesser surface, it is the same design language in a 16-colour medium.
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

    /// <summary>The accent for a condition.</summary>
    public static int For(DeviceCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        return condition.State switch
        {
            DeviceState.InSync => Green,
            DeviceState.Reconciling => Amber,
            DeviceState.VersionMismatch => Amber,
            DeviceState.ControlNotConfigured => Blue,
            DeviceState.NotAdopted => condition.Cause is "blocked" or "bad-signature" ? Red : Blue,
            _ => Grey,
        };
    }
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
        var accent = StagePalette.For(status.Condition);
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

    private static List<string> BuildHead(AgentStatus status, int tick, int inner, int accent, bool colour)
    {
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
                    new Run(status.Condition.Headline, StagePalette.Headline, Bold: true),
                ],
                inner,
                colour),
            Compose([], inner, colour),
        };

        // The headline above already says what was detected (§2.7 item 1) in the common case, so
        // the field is only spelled out when it adds something the headline did not.
        var detected = status.Narration.Detected ?? status.Condition.Headline;
        if (!string.Equals(detected, status.Condition.Headline, StringComparison.Ordinal))
        {
            AddField(lines, "Detected", detected, inner, colour);
        }

        AddField(lines, "Why", status.Narration.WhyItMatters ?? status.Condition.Detail, inner, colour);

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

            return Compose(
                [
                    new Run(Pad("Try again", LabelWidth), StagePalette.Label),
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
