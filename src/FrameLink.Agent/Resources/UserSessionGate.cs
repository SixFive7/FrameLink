using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <b>The shared session-readiness gate</b> — one probe in front of every resource whose verdict
/// lives inside the login user's session.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists to close, measured across ten boots.</b> <c>fl-agent</c> runs its
/// first reconcile pass at boot+10.0–10.6 s. The console login opens its PAM session at 10.3–10.8 s
/// and the user manager comes up <b>0.03–0.7 s after the agent's verdict</b>, on every boot —
/// including the one boot where the verdict landed after the login. So every resource that asks the
/// session a question is asking before there is anything to ask, and gets an answer that reads
/// exactly like a broken frame:
/// </para>
/// <list type="bullet">
/// <item><c>session.bash-profile-exec-labwc</c> — "labwc is not running", five attempts of five.</item>
/// <item><c>unit.xdg-desktop-portal.dropin-desktop</c>, <c>unit.chromium-kiosk.enabled</c>,
/// <c>unit.framelink-camera.enabled</c> — "Failed to connect to user scope bus … No such file or
/// directory", which is the same fact in D-Bus form: no session, no <c>/run/user/1000</c>, no bus.</item>
/// </list>
/// <para>
/// Nothing was wrong on the frame. The <c>.bash_profile</c> was the expected 118 bytes with the
/// expected hash, the portal drop-in was on disk, and labwc was running — the agent even
/// contradicted itself in its own log, with <c>ScreenHandover</c> detecting a live compositor 4.1 s
/// and 4.3 s <i>after</i> it had declared labwc absent.
/// </para>
/// <para>
/// <b>This is <c>d275689</c>'s defect again, and that is the argument for one gate rather than five
/// windows.</b> That commit fixed the identical race for <c>boot.autologin.getty-tty1</c> with a
/// settle window of its own, and <c>9b83e81</c>'s audit named this family in advance. Replicating
/// the fix per resource would put the same reasoning in five places that can drift apart, each with
/// its own threshold to re-derive; the cause is one shared fact, so it is read once.
/// </para>
/// <para>
/// <b>Unevaluable rather than a settle window, and the difference matters.</b> A window is a guess
/// about how long something takes. This is not a guess — it is the thing itself: the session either
/// exists or it does not, and the frame can be asked. <see cref="ResourceObservation.Unevaluable"/>
/// already carries exactly that verdict, and the loop already knows what to do with it: no attempt
/// is spent, nothing is acted on, nothing reboots, the resource reports as waiting and says what
/// for, and it is re-read <see cref="ReconcileOptions.UnevaluableRecheck"/> later. That is the whole
/// fix, and it needed no change to the reboot decision — an unevaluable observation never reaches
/// <c>ActAsync</c>, so a resource that is merely unsettled cannot request a reboot in the first
/// place.
/// </para>
/// <para>
/// <b>Why the reserved meaning of Unevaluable is not being widened.</b> <c>CameraNodeResource</c>
/// records the rule this has to answer to: a local read that failed "has learned something real
/// about this machine, so it is drift and not Unevaluable — that outcome is reserved for an
/// authority off the device that did not answer, and must never become the place a real failure
/// goes to be quiet." The rule survives intact. The authority here is <c>logind</c>, which owns
/// session state, brings it up asynchronously and is no more the agent's to command than the Fleet
/// Manager is. What this gate excludes is not a failed read but a read that <i>could not be
/// made</i>; once the session is up, a failing <c>wpctl</c> or <c>busctl</c> is drift again, exactly
/// as before, and every one of these resources still reports its real verdict on every pass after
/// the first ten seconds of a boot.
/// </para>
/// <para>
/// <b>Which resources are behind it, and which deliberately are not.</b> The test is whether the
/// <i>in-sync predicate</i> depends on the session — not whether the Observe touches it at all.
/// <c>wireplumber.conf.camera-monitors-disabled</c> and <c>boot.config.camera-auto-detect</c> both
/// run a session command and are both left alone, because each already does the right thing:
/// the probe rides in the observed text and the predicate stays on the file the resource owns.
/// Gating those would take a genuinely absent config line and hide it for the first ten seconds of
/// every boot, in exchange for nothing.
/// </para>
/// </remarks>
public static class UserSessionGate
{
    /// <summary>
    /// The observation to return instead of looking, or null when the session is up and the
    /// resource should go ahead and look.
    /// </summary>
    /// <param name="session">The login user's session.</param>
    /// <param name="expected">
    /// The resource's own expected value, unchanged — an unevaluable observation still says what it
    /// was hoping for, because "could not be determined" on its own tells an operator nothing.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async ValueTask<ResourceObservation?> NotSettledAsync(
        IUserSession session,
        string expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);

        var readiness = await session.ReadinessAsync(cancellationToken).ConfigureAwait(false);

        return readiness.Ready ? null : ResourceObservation.Unevaluable(expected, readiness.Why);
    }
}
