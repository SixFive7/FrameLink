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
        var counted = tries == 1
            ? string.Create(CultureInfo.InvariantCulture, $"{name} failed after 1 try")
            : string.Create(CultureInfo.InvariantCulture, $"{name} failed after {tries} tries");

        return resource?.Delta is { Length: > 0 } delta ? $"{counted}, {delta}" : counted;
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
    /// §2.7 item 9 — how to retry at the frame, or where the button is instead.
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
    /// genuinely has no touchscreen, and on that frame the Fleet Manager really is where the button
    /// is. The sentence is chosen from what the agent found rather than from what was assumed.
    /// </para>
    /// </remarks>
    public static string RetryLine(TouchRetryState touch) => touch.Available
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"Touch the screen and hold for {(int)touch.Hold.TotalSeconds} seconds to try again.")
        : "This frame has no touchscreen — the Try again button is in the Fleet Manager.";

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
