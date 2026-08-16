using System.Globalization;
using FrameLink.Agent.Reconcile;
using FrameLink.Protocol;

namespace FrameLink.Agent.State;

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
