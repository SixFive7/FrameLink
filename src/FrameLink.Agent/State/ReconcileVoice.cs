using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Protocol;

namespace FrameLink.Agent.State;

/// <summary>
/// Which of three things a frame is, once the ladder and the frame's own observation are put
/// together — §2.7's top line, and everything else that has to agree with it.
/// </summary>
/// <remarks>
/// <b>One classification, so that no second one can be written.</b> The wording, the accent and
/// anything a later surface derives from "what is this frame" all come off
/// <see cref="ReconcileVoice.Voice"/>, which makes the conjunction §2.6 specifies exactly once. A
/// screen whose headline and whose colour were composed separately is how a frame ends up saying
/// it is fixing itself in the green of a frame that is working.
/// </remarks>
public enum StageVoice
{
    /// <summary>
    /// The ladder's own wording and the rung's own accent — what the Fleet Manager said.
    /// </summary>
    /// <remarks>
    /// Either the condition already stops the product, in which case <i>adopt this frame</i>,
    /// <i>it has been blocked</i> and <i>it is updating</i> are each more actionable than <i>it is
    /// fixing itself</i>; or nothing is wrong at all and the frame says what it always said.
    /// </remarks>
    Ladder,

    /// <summary>Cleared to run, and putting one of its own settings back.</summary>
    Repairing,

    /// <summary>Cleared to run, and has given up on something (§2.7 item 7).</summary>
    Stopped,
}

/// <summary>
/// <b>The sentences the frame says about reconciliation</b> — §2.7 items 5, 7 and 8.
/// </summary>
/// <remarks>
/// <para>
/// <b>One home for the wording, because there are two surfaces.</b> The console stage paints text
/// on <c>/dev/tty8</c> and the browser stage sends fields to a page; if each composed its own
/// sentences they would eventually disagree about whether a frame had given up, and the two
/// surfaces exist precisely so that a person can believe whichever one is in front of them.
/// <see cref="Stage.StageRenderer"/> and <see cref="Stage.BrowserStage.Compose"/> both call in
/// here, so a change of wording is one edit and a change of <i>meaning</i> is impossible to make
/// on one surface only.
/// </para>
/// <para>
/// <b>Pure functions of a status snapshot.</b> Nothing here reads a clock, a file or a link, which
/// is what lets every claim §2.7 makes be asserted directly rather than through a rendered frame —
/// and what makes §2.7 item 8's promise testable with no server anywhere in the test.
/// </para>
/// <para>
/// <b>The delta is rendered, never re-derived</b> (decision 70). §2.5 rung 2 already requires the
/// exact expected-versus-observed text and the attempt count to be recorded, and
/// <see cref="ResourceObservation.Delta"/> already produces it in the one form the log, the screen
/// and the Fleet Manager's device row all share. A second spelling composed here would be a second
/// truth about the same failure.
/// </para>
/// </remarks>
public static class ReconcileVoice
{
    /// <summary>What the frame says when it has given up and nobody has told it who to ask.</summary>
    public const string UnknownContact = "Nothing more will happen until someone helps. Ask whoever looks after your Fleet Manager.";

    /// <summary>
    /// Whether a resource has stopped being touched — §2.5 rung 2's "stop", however it is spelled.
    /// </summary>
    /// <remarks>
    /// One definition, shared with the loop: <see cref="ResourceStatuses.HasGivenUp"/> lives beside
    /// the enum it asks about, so the screen and the reconciler cannot come to disagree about which
    /// statuses mean a frame has stopped.
    /// </remarks>
    public static bool HasGivenUp(ResourceStatusKind kind) => kind.HasGivenUp();

    /// <summary>The resource that has given up, or null while the frame is still trying.</summary>
    /// <remarks>
    /// Prefers the one the loop is already narrating, so the screen names the same resource its
    /// own status line does; falls back to the first in the published list, which is ledger order.
    /// </remarks>
    public static ResourceStatus? Stopped(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        ResourceStatus? first = null;

        foreach (var resource in status.Resources)
        {
            if (!HasGivenUp(resource.Kind))
            {
                continue;
            }

            if (string.Equals(resource.Name, status.Reconcile.Resource, StringComparison.Ordinal))
            {
                return resource;
            }

            first ??= resource;
        }

        return first;
    }

    /// <summary>
    /// Whether this frame has given up on something and is waiting for a person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two sources, because the loop publishes through two paths and only one of them carries a
    /// resource list.</b> <c>PublishStatusesAsync</c> publishes the whole per-resource picture at
    /// the end of a pass; <c>PublishNarration</c> publishes ledger-derived narration <i>during</i>
    /// one and leaves <see cref="AgentStatus.Resources"/> exactly as it was. A screen that read
    /// only the first would go quiet about a frame that had just given up, for as long as the pass
    /// took to finish.
    /// </para>
    /// <para>
    /// The narration test mirrors <c>ReconcileLoop.HasGivenUp</c> deliberately — an escalation on
    /// the record <i>and</i> a spent budget — so the two cannot drift into disagreeing about which
    /// frames have stopped. The escalation count alone would be wrong in the one direction that
    /// matters: it survives a retry, which resets the attempts, so a frame given a fresh budget
    /// would still be painted as stopped.
    /// </para>
    /// <para>
    /// An <i>unknown</i> budget is treated as a spent one. Nothing in the loop publishes narration
    /// without it, so this arm is about being wrong in the safe direction if something ever does:
    /// an escalation with no budget beside it is still an escalation, and the failure to prefer is
    /// a static screen naming a person over an animated one naming nobody.
    /// </para>
    /// </remarks>
    public static bool HasStopped(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return Stopped(status) is not null
            || (status.Reconcile.Escalations > 0
                && (status.Reconcile.AttemptBudget <= 0
                    || status.Reconcile.Attempt >= status.Reconcile.AttemptBudget));
    }

    /// <summary>The top line of a frame that has given up on something.</summary>
    public const string StoppedHeadline = "This frame has stopped and needs help";

    /// <summary>The second line under <see cref="StoppedHeadline"/>.</summary>
    public const string StoppedDetail =
        "It could not finish setting itself up, so it is not showing your photos.";

    /// <summary>The top line of a frame that is still putting its own settings back.</summary>
    public const string RepairingHeadline = "Putting this frame right";

    /// <summary>The second line under <see cref="RepairingHeadline"/>.</summary>
    public const string RepairingDetail =
        "Something on it is not as it should be. It is fixing that first, and then your photos come back.";

    /// <summary>
    /// <b>The conjunction §2.6 specifies, made once</b> — what this frame is, from both halves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything the screen says about the frame as a whole is a function of this, and that is
    /// the point of it existing.</b> <see cref="Headline"/> and <see cref="Detail"/> are the words;
    /// <see cref="Stage.StagePalette.For(AgentStatus)"/> is the colour. Composing the second one
    /// separately is what left a repairing frame green under a headline saying it was fixing
    /// something (decision 83) — the same defect as <c>c3116bc</c>'s headline, one property along.
    /// </para>
    /// <para>
    /// <b>Which half wins when both stop the product.</b> A condition that already stops it keeps
    /// its own voice: <i>adopt this frame</i>, <i>it has been blocked</i>, <i>it is updating</i> are
    /// each more actionable than <i>it is fixing itself</i>, and the body under them renders an
    /// adoption fingerprint rather than a delta. The composition is reached only where the Fleet
    /// Manager has cleared the frame to run and the frame's own observation has not — which is
    /// exactly the state that produced the contradiction, and is the same conjunction
    /// <see cref="AgentStatus.ProductRuns"/> already makes.
    /// </para>
    /// </remarks>
    public static StageVoice Voice(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!status.Condition.ProductRuns)
        {
            return StageVoice.Ladder;
        }

        return HasStopped(status) ? StageVoice.Stopped
            : status.Drifted ? StageVoice.Repairing
            : StageVoice.Ladder;
    }

    /// <summary>
    /// §2.7 item 1 — the one line at the top of both surfaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Composed from the same snapshot the body underneath it is composed from, and that is the
    /// whole of this.</b> On 2026-08-16 a frame printed <i>"Everything is working — This frame is
    /// adopted, up to date and showing your photos"</i> directly above
    /// <c>unit.chromium-kiosk.running-matches-content failed after 3 tries</c>, the sentence saying
    /// nothing further would happen until somebody helped, and a Try again button — on a panel
    /// showing no photographs at all.
    /// </para>
    /// <para>
    /// <b>Both surfaces were taking the top two lines straight off
    /// <see cref="DeviceCondition"/>.</b> <see cref="DeviceStateLadder.FromHandshake"/> derives that
    /// from the handshake and nothing else, so <see cref="Protocol.HandshakeStatus.Ok"/> means
    /// adopted, on the served version, and no more than that. What the frame has observed of
    /// <i>itself</i> lives in <see cref="AgentStatus.Drifted"/>, kept off the ladder deliberately —
    /// §2.6's ladder is about what the Fleet Manager has said — and the one place the two halves
    /// were ever put together was <see cref="AgentStatus.ProductRuns"/>. So the screen decided to
    /// <i>appear</i> on the composed truth and then titled itself with the adoption half alone.
    /// </para>
    /// <para>
    /// <b>Decision 70's rule did not fail to bind here; it never reached this far.</b> <i>A stopped
    /// frame stops looking like a working one</i> was carried out against everything that moved —
    /// the spinner, the marquee, the attempt counter, the countdown — because the failure it was
    /// written for was a frame that looked busy. A still headline making a false claim is the same
    /// defect with the animation taken out.
    /// </para>
    /// <para>
    /// <b>Which half wins when both stop the product</b> is <see cref="Voice"/>'s answer, and this
    /// is only the wording of it. Reading the classification from there rather than repeating the
    /// conjunction is what keeps the accent beside these sentences from being composed differently.
    /// </para>
    /// </remarks>
    public static string Headline(AgentStatus status) => Voice(status) switch
    {
        StageVoice.Stopped => StoppedHeadline,
        StageVoice.Repairing => RepairingHeadline,
        _ => status.Condition.Headline,
    };

    /// <summary>§2.7 item 1's second line, composed the same way as <see cref="Headline"/>.</summary>
    public static string Detail(AgentStatus status) => Voice(status) switch
    {
        StageVoice.Stopped => StoppedDetail,
        StageVoice.Repairing => RepairingDetail,
        _ => status.Condition.Detail,
    };

    /// <summary>
    /// §2.7 item 1's <c>Detected</c> field, or null when the headline has already said it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same two sentences, a second time.</b> <c>ControlLink</c> and <c>ConnectionAttempt</c>
    /// seed <see cref="Narration.Detected"/> and <see cref="Narration.WhyItMatters"/> from
    /// <see cref="DeviceCondition.Headline"/> and <see cref="DeviceCondition.Detail"/>, so a surface
    /// that renders all four printed the claim twice — measured on the same screenshot, at 30 px and
    /// again at 20 px underneath it.
    /// </para>
    /// <para>
    /// <see cref="Stage.StageRenderer"/> already suppressed the repeat for <c>Detected</c> and the
    /// page did not, which is precisely the divergence this class exists to make impossible.
    /// Composing the suppression here gives it to both surfaces, and it is written against the
    /// composed headline as well as the condition's own, so a narration echoing either is dropped.
    /// </para>
    /// </remarks>
    public static string? Detected(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return status.Narration.Detected is { Length: > 0 } detected
            && !string.Equals(detected, status.Condition.Headline, StringComparison.Ordinal)
            && !string.Equals(detected, Headline(status), StringComparison.Ordinal)
                ? detected
                : null;
    }

    /// <summary>
    /// §2.7 item 2, or null when <see cref="Detail"/> has already said it.
    /// </summary>
    public static string? WhyItMatters(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return status.Narration.WhyItMatters is { Length: > 0 } why
            && !string.Equals(why, status.Condition.Detail, StringComparison.Ordinal)
            && !string.Equals(why, Detail(status), StringComparison.Ordinal)
                ? why
                : null;
    }

    /// <summary>
    /// §2.7 item 7 — <c>item z failed after 3 tries, expected a but got b</c>, or null.
    /// </summary>
    /// <remarks>
    /// The delta is present whenever the per-resource list is, and absent when only the narration
    /// says a frame has stopped. The sentence is composed without it in that case rather than
    /// suppressed: <i>what</i> failed and <i>how many times</i> are already more than a blank
    /// screen, and the delta arrives with the pass's own publish a moment later.
    /// </remarks>
    public static string? StoppedLine(AgentStatus status)
    {
        if (!HasStopped(status))
        {
            return null;
        }

        var resource = Stopped(status);
        var name = resource?.Name ?? status.Reconcile.Resource;
        if (name is not { Length: > 0 })
        {
            return null;
        }

        var tries = resource?.Attempts is > 0 ? resource.Attempts : status.Reconcile.Attempt;
        var counted = resource is { Attempted: false }
            ? string.Create(CultureInfo.InvariantCulture, $"{name} cannot be put right by this frame")
            : tries == 1
                ? string.Create(CultureInfo.InvariantCulture, $"{name} failed after 1 try")
                : string.Create(CultureInfo.InvariantCulture, $"{name} failed after {tries} tries");

        return resource?.Delta is { Length: > 0 } delta ? $"{counted}, {delta}" : counted;
    }

    /// <summary>
    /// The heading above the technical block, on both surfaces and in the event trail.
    /// </summary>
    public const string TechnicalHeading = "Technical detail, for whoever helps you:";

    /// <summary>The label on the button that restarts the frame and tries again.</summary>
    /// <remarks>
    /// <b>It says what it does in the order it does it.</b> The operator's two buttons are
    /// <i>shutdown</i> and <i>reboot, which forces a new retry</i>, and a button labelled only
    /// "Try again" would hide the restart from the person pressing it — on a screen in a living
    /// room, where the frame going dark for a minute is the visible half of what happens next.
    /// </remarks>
    public const string RestartButton = "Restart and try again";

    /// <summary>The label on the button that switches the frame off.</summary>
    public const string ShutdownButton = "Shut down";

    /// <summary>
    /// How many times this was tried, in plain words — or that it was not tried at all.
    /// </summary>
    /// <remarks>
    /// <b>A gate is the case this exists for.</b> It reaches rung 2 with the budget declared spent
    /// and nothing attempted, so the ordinary sentence would tell a household the frame had tried
    /// three times and restarted three times when it had read one value once. The alternative
    /// wording is not a softer version of the same claim: it says the frame cannot fix this, which
    /// is the fact that decides whether waiting is any use.
    /// </remarks>
    public static string TriesLine(ResourceStatus resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (!resource.Attempted)
        {
            return "The frame did not try to put this right, because there is nothing it could do about it. "
                + "Somebody has to look at it.";
        }

        var tries = resource.Attempts > 0 ? resource.Attempts : 1;

        return tries == 1
            ? "It tried once, restarting the frame to check, and it will not try again on its own."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"It tried {tries} times, restarting the frame each time to check, and it will not try again on its own.");
    }

    /// <summary>
    /// <b>The plain half of §2.7's stopped screen</b> — everything a person with no computer
    /// experience can act on, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written to <see cref="Firmware.ArrayGateRuling"/>'s pattern, because that pattern was
    /// built for exactly this reader and works.</b> Two halves, kept apart on purpose: the person
    /// standing in front of the frame is a family member who has never opened a terminal, and the
    /// person they will forward this to needs the values. A single message pitched between the two
    /// is useless to both.
    /// </para>
    /// <para>
    /// Every line comes off the row that gave up, so a frame with two stopped resources can never
    /// pair one resource's sentence with another's numbers. Empty on a frame that has not stopped.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> SupportPlain(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!HasStopped(status))
        {
            return [];
        }

        var lines = new List<string>(5);
        var resource = Stopped(status);

        if (resource is null)
        {
            // The narration knows the frame has stopped and the pass has not published the row
            // yet. Saying the little that is certain beats saying nothing for a pass.
            lines.Add("Something it needs is not right, and it has stopped trying to fix it.");
            return lines;
        }

        // Suppressed against everything the surfaces already print, which is the headline, the
        // detail, and whatever the live narration is still carrying. A frame that gives up without
        // a reboot in between — a refused reboot is the case — has the narration for this very
        // resource still published, and the same sentence at two sizes one under the other is the
        // defect measured on 2026-08-16 with the roles reversed.
        if (resource.Detected is { Length: > 0 } detected
            && !string.Equals(detected, Headline(status), StringComparison.Ordinal)
            && !string.Equals(detected, Detected(status), StringComparison.Ordinal))
        {
            lines.Add(detected);
        }

        if (resource.WhyItMatters is { Length: > 0 } why
            && !string.Equals(why, Detail(status), StringComparison.Ordinal)
            && !string.Equals(why, WhyItMatters(status), StringComparison.Ordinal))
        {
            lines.Add(why);
        }

        lines.Add(TriesLine(resource));

        if (resource.Attempted && resource.Gloss is { Length: > 0 } gloss)
        {
            lines.Add($"What it tried: {gloss}");
        }

        return lines;
    }

    /// <summary>
    /// <b>The technical half</b> — the block somebody photographs and sends on (§2.7 item 7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>key: value</c> pairs, one per line, no prose. This is the one screen in the product where
    /// density beats brevity: the person reading it is going to be asked to relay it, and a message
    /// trimmed to fit is a message they have to be asked follow-up questions about.
    /// </para>
    /// <para>
    /// <b>Every value is rendered, never re-derived</b> (decision 70). The delta is the string §2.5
    /// rung 2 recorded, the change is the resource's own verbatim <see cref="ResourceAction"/>, and
    /// the counts are the ledger's. A value that arrives with newlines in it — the hardware gate's
    /// whole refusal is one — is split into its own lines rather than flattened into a paragraph,
    /// because the structure of that message is half of what makes it readable.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> SupportTechnical(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!HasStopped(status))
        {
            return [];
        }

        var resource = Stopped(status);
        var lines = new List<string>(10);

        Add(lines, "resource", resource?.Name ?? status.Reconcile.Resource);

        if (resource is not null)
        {
            Add(
                lines,
                "tried",
                resource.Attempted
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"{resource.Attempts} of {resource.AttemptBudget}")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"nothing was attempted (this is a precondition, not a repair); budget {resource.AttemptBudget} declared spent"));

            Add(lines, "delta", resource.Delta);
            Add(lines, "last change", resource.Attempted ? resource.Action : null);
            Add(
                lines,
                "escalations",
                resource.Escalations > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{resource.Escalations}")
                    : null);
        }

        Add(
            lines,
            "reported",
            status.Reconcile.Escalations > 0 || resource?.Escalations > 0
                ? resource?.Kind is ResourceStatusKind.Escalated || status.Reconcile.AdminNotified
                    ? "yes, the Fleet Manager has it"
                    : "no, the Fleet Manager could not be reached"
                : null);

        Add(lines, "device", status.DeviceId);
        Add(lines, "serial", status.HardwareSerial);
        Add(lines, "agent", status.AgentVersion);

        return lines;

        static void Add(List<string> lines, string label, string? value)
        {
            if (value is not { Length: > 0 })
            {
                return;
            }

            var parts = value.Split('\n');

            lines.Add($"{label}: {parts[0].TrimEnd('\r')}");

            for (var index = 1; index < parts.Length; index++)
            {
                lines.Add(parts[index].TrimEnd('\r'));
            }
        }
    }

    /// <summary>
    /// §2.7 item 5 — <c>item x attempt 1 of 3</c>, or null when nothing is in progress.
    /// </summary>
    /// <remarks>
    /// Null once anything has given up, because under decision 68 nothing else is being attempted
    /// and a live attempt line beside a stopped frame is the exact misreading decision 70 exists to
    /// prevent.
    /// </remarks>
    public static string? ProgressLine(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (HasStopped(status))
        {
            return null;
        }

        if (status.Reconcile.Resource is not { Length: > 0 } name || status.Reconcile.Attempt <= 0)
        {
            return null;
        }

        return status.Reconcile.AttemptBudget > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{name} attempt {status.Reconcile.Attempt} of {status.Reconcile.AttemptBudget}")
            : string.Create(CultureInfo.InvariantCulture, $"{name} attempt {status.Reconcile.Attempt}");
    }

    /// <summary>
    /// §2.7 item 9 — <b>how to restart and how to switch off at the frame</b>, or where the buttons
    /// are instead (decision 92).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this replaces was false</b> (decision 77). The console stage printed <i>"This screen
    /// has no buttons — the Try again button is in the Fleet Manager"</i>, which decision 72 shipped
    /// honestly: nothing in this repository had ever captured an input device from a frame, so
    /// whether the panel exposed one was genuinely unknown and the sentence was written for the
    /// answer that needed no evdev reader. It has since been measured, and the panel does: a Goodix
    /// digitiser on its own evdev node, tagged <c>ID_INPUT_TOUCHSCREEN</c>, emitting
    /// <c>BTN_TOUCH</c>. A frame that says it cannot be touched, in front of a person touching it,
    /// is the specific harm §2.7 exists to prevent, pointing the other way.
    /// </para>
    /// <para>
    /// <b>Both sentences are still needed and both are now true of the frame that prints them.</b>
    /// A frame whose panel is not up yet — the overlay is 2nd in the catalog and takes a reboot —
    /// genuinely has no touchscreen, and on that frame the Fleet Manager really is where the buttons
    /// are. The sentence is chosen from what the agent found rather than from what was assumed.
    /// </para>
    /// <para>
    /// <b>Several lines rather than one, because a gesture nobody understands is worse than a single
    /// button.</b> This screen is the first thing a new frame shows and the reader may never have
    /// used a touchscreen: what the words have to establish, in order, is that the glass responds to
    /// a finger at all, that resting a finger there commits to nothing, where to look to see that
    /// the frame noticed, which length does which thing, and — first, before either verb — that
    /// taking the finger off early does nothing at all. The last of those is the whole of the way
    /// out, so it is said before the two things it is a way out of.
    /// </para>
    /// <para>
    /// <b>The two verbs are stated as their costs, not as their names.</b> "Restarts and tries
    /// everything again" carries the dark minute the person will otherwise watch and worry about;
    /// the shutdown line says what nothing else in the product says, which is that no button
    /// anywhere brings the frame back and somebody has to walk over to it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> TouchLines(TouchRetryState touch)
    {
        if (!touch.Available)
        {
            // The honest half of decision 72 survives and is now true of the frame that prints it:
            // a frame whose panel overlay has not been applied yet really has no touchscreen, and
            // on that frame the Fleet Manager really is where both buttons are. Both, now — a
            // sentence naming only the restart would leave the reader of a frame they wanted off
            // believing there was nowhere to do it.
            return
            [
                "This frame has no touchscreen, so nothing can be pressed on this screen. The buttons that "
                + "restart it and switch it off are in the Fleet Manager.",
            ];
        }

        if (!touch.TwoWay)
        {
            // A single-meaning hold is a firmware question, and that screen writes its own
            // sentences (ArrayFlashVoice). Saying anything about restarting or switching off
            // underneath it would offer two things the hold in front of the person does not do.
            return [];
        }

        var restart = ((int)Math.Round(touch.RestartAt!.Value.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        var shutdown = ((int)Math.Round(touch.Hold.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        return
        [
            "This screen feels your finger. Put one finger anywhere on it and keep it still. Do not tap "
            + "the screen, and do not take your finger off straight away.",

            "While your finger rests there a bar fills up near the bottom of this box, and the line under "
            + "the bar always says what would happen if you took your finger off at that moment. Nothing "
            + "happens while your finger is still on the screen.",

            "Take your finger off in the first " + restart + " seconds and nothing happens at all. That is "
            + "how you change your mind.",

            "Keep your finger there for " + restart + " seconds, then take it off: this frame restarts and "
            + "tries everything again. The screen goes dark for about a minute and then comes back on its own.",

            "Keep your finger there for " + shutdown + " seconds instead, then take it off: this frame "
            + "switches off and stays off. Nothing can switch it on again from anywhere else — somebody has "
            + "to come to this frame, unplug it and plug it in again.",
        ];
    }

    /// <summary>
    /// The word beside the bar — what letting go right now would do, in as few characters as the
    /// console has room for.
    /// </summary>
    /// <remarks>
    /// <b>Two lengths of words for one fact, and both are needed.</b> This is the one a person
    /// glances at while their finger is on the glass and their eye is on the bar; the sentence in
    /// <see cref="HoldPromise"/> under it is the one they read the first time. Neither is a summary
    /// of the other — they are the same decision at two reading distances, and both come from
    /// <see cref="TouchRetryState.Commit"/> so neither can promise something the release will not
    /// do.
    /// </remarks>
    public static string HoldBand(TouchCommit commit) => commit switch
    {
        TouchCommit.Restart => "restart",
        TouchCommit.Shutdown => "switch off",
        _ => "nothing yet",
    };

    /// <summary>
    /// The sentence under the bar — what letting go now would do, and what holding on would do
    /// instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It always names the next band as well as this one.</b> A line saying only "nothing
    /// happens" would read, to somebody two seconds into their first attempt, as a screen that had
    /// not noticed them — which is the failure this whole surface exists to prevent, and it is why
    /// the seconds still to go are said out loud.
    /// </para>
    /// <para>
    /// <b>Nothing here counts down towards an action.</b> The number is how much longer the person
    /// would have to keep doing what they are already doing, not how long until something happens
    /// to them: at zero the frame does nothing and waits, exactly as it did at three seconds.
    /// </para>
    /// </remarks>
    public static string HoldPromise(TouchRetryState touch, DateTimeOffset now)
    {
        if (!touch.TwoWay || touch.HoldingSince is null)
        {
            return string.Empty;
        }

        var elapsed = touch.Elapsed(now);

        return touch.Commit(now) switch
        {
            TouchCommit.Shutdown =>
                "Take your finger off now and this frame switches off. It stays off until somebody comes to "
                + "it, unplugs it and plugs it in again.",

            TouchCommit.Restart =>
                "Take your finger off now and this frame restarts and tries everything again. Keep it there "
                + "for " + Seconds(touch.Hold - elapsed) + " instead and it switches off.",

            _ =>
                "Take your finger off now and nothing happens. Keep it there for "
                + Seconds(touch.RestartAt!.Value - elapsed) + " to restart this frame.",
        };
    }

    /// <summary>Whole seconds, rounded up, never below one, with the noun agreeing.</summary>
    /// <remarks>
    /// Rounded up rather than down because the number is an instruction: a person told "1 second"
    /// at 1.4 s to go lets go at 1 s and gets the band they were trying to leave. Never zero, for
    /// the same reason — "keep it there for 0 more seconds" is not something to ask of anybody.
    /// </remarks>
    private static string Seconds(TimeSpan left)
    {
        var whole = Math.Max(1, (int)Math.Ceiling(left.TotalSeconds));

        return whole == 1
            ? "1 more second"
            : string.Create(CultureInfo.InvariantCulture, $"{whole} more seconds");
    }

    /// <summary>
    /// §2.7 item 8 — who to contact, in one sentence a non-technical reader can act on.
    /// </summary>
    /// <remarks>
    /// Never null. A frame that has stopped and cannot name anybody still has to say that somebody
    /// is needed, because the alternative is a screen that reports a failure and no next step —
    /// which is how a household ends up waiting for a frame to fix itself.
    /// </remarks>
    public static string ContactLine(OperatorContact? contact)
    {
        var name = contact?.Name?.Trim();
        var detail = contact?.Contact?.Trim();

        return (name, detail) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"Nothing more will happen until someone helps. Ask {name} — {detail}.",
            ({ Length: > 0 }, _) => $"Nothing more will happen until someone helps. Ask {name}.",
            (_, { Length: > 0 }) => $"Nothing more will happen until someone helps. Contact {detail}.",
            _ => UnknownContact,
        };
    }
}
