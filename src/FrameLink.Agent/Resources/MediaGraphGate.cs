using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <b>The shared media-graph gate</b> — one probe in front of every resource whose verdict lives
/// inside the graph WirePlumber publishes, for the seconds after a boot in which it has not
/// published one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists to close, measured on the frame.</b>
/// <see cref="UserSessionGate"/> opens as soon as <c>/run/user/&lt;uid&gt;/bus</c> exists, and on
/// this hardware that is <i>before</i> WirePlumber has done anything: the user manager starts
/// <c>wireplumber.service</c> at boot+0.1 s of the login and its device monitors are still loading
/// 3.3 s later (<c>wp-internal-comp-loader: Loading profile 'main'</c> at 03:03:27.728,
/// <c>s-monitors:</c> at 03:03:31.049, 2026-08-19). A reconcile pass that lands in that window asks
/// a running daemon about a graph it has not built, and gets answers that read exactly like a
/// broken frame:
/// </para>
/// <list type="bullet">
/// <item><c>audio.wireplumber.playback-volume</c> — <c>wpctl answered '', which carries no
/// volume</c>, because <c>@DEFAULT_AUDIO_SINK@</c> translates to <c>-1</c> while no default sink
/// has been chosen. Its Act is refused for the same reason: <i>"Translate ID error: '-1' is not a
/// valid ID (returned by default-nodes-api)"</i>.</item>
/// <item><c>camera.pipewire-node.framelink-cam</c> — <c>PipeWire is offering no camera at
/// all</c>, seconds before <c>gst-launch</c> registers the node.</item>
/// </list>
/// <para>
/// Each of those is drift, so each is acted on, and §2.4 makes each act reboot the frame — which
/// starts the next boot, which asks too early again. That is the cascade: six reboots on the worst
/// night, and <c>audio.wireplumber.playback-volume</c> reaching <c>attempts=3</c>, the last rung
/// before it gives up, on both cascade nights.
/// </para>
/// <para>
/// <b>Unevaluable, for <see cref="UserSessionGate"/>'s reasons and under its rule.</b> No attempt is
/// spent, nothing is acted on, nothing reboots, the resource reports as waiting and says what for,
/// and it is re-read <see cref="ReconcileOptions.UnevaluableRecheck"/> later. The reserved meaning
/// survives intact: this is not a failed local read, which
/// <see cref="CameraNodeResource"/> requires to stay drift. The authority is WirePlumber, which
/// owns the graph, builds it asynchronously and is no more the agent's to command than
/// <c>logind</c> or the Fleet Manager is; what is excluded is not a read that failed but a read
/// that <i>could not be made</i>. The moment the graph exists, a wrong volume and a missing camera
/// are drift again, exactly as before.
/// </para>
/// <para>
/// <b>The fact is the default audio sink, and the two clauses are one question asked twice.</b>
/// A sink WirePlumber has marked default is what <c>@DEFAULT_AUDIO_SINK@</c> resolves to, so its
/// absence is the readable form of the token that was failing. Requiring <c>Audio</c> /
/// <c>Devices</c> to be empty as well is what keeps this a statement about start-up rather than
/// about health: a frame whose card WirePlumber has enumerated and then refused to route is a real
/// fault, it has a device and no default, and it goes straight back to being drift — which is
/// where a fault that needs a person belongs. <b>This outcome must never become the place a real
/// failure goes to be quiet</b>, and those are the two ways it could have.
/// </para>
/// <para>
/// <b>Why the camera is behind an audio fact, and why it cannot be behind its own.</b>
/// <c>camera.pipewire-node.framelink-cam</c> exists to assert that the camera node is there, so
/// gating it on the camera node being there would leave it unable to report anything, ever — the
/// mistake this project has already made once. Its gate therefore has to be a fact about the graph
/// as a whole, and an empty <c>Audio</c> half is the one this frame publishes: WirePlumber loads
/// one profile and one set of monitors, so a daemon that has adopted no audio device has adopted
/// nothing. The coupling is stated rather than hidden, and it is bounded at both ends. A frame with
/// no sound hardware at all is <b>settled</b> by <see cref="HasSoundHardware"/> below — the same
/// escape, for the same reason, as <c>LoginUserSession.ReadinessAsync</c> reporting ready for a uid
/// that will not resolve. A frame whose array is present and unadopted keeps its own escalation
/// path in <c>audio.modprobe.snd-usb-audio-index</c>, which owns that question and answers it from
/// <c>/proc/asound</c> without asking WirePlumber anything.
/// </para>
/// <para>
/// <b>Which resources are behind it, and which deliberately are not.</b> The test is
/// <see cref="UserSessionGate"/>'s: whether the <i>in-sync predicate</i> reads the graph.
/// <c>wireplumber.conf.camera-monitors-disabled</c> runs <c>wpctl status</c> and stays out, because
/// its predicate is the fragment on disk and the probe only rides in the observed text — gating it
/// would hide a genuinely absent config line for the first seconds of every boot in exchange for
/// nothing. <c>session.bash-profile-exec-labwc</c> stays out too: it is in the same cascades, but
/// what it reads is <c>pgrep -x labwc</c>, which is not the graph and is already gated on the
/// session.
/// </para>
/// </remarks>
public static class MediaGraphGate
{
    /// <summary>How the graph is read.</summary>
    public const string Executable = "wpctl";

    /// <summary>
    /// The observation to return instead of looking, or null when the graph is up and the resource
    /// should go ahead and look.
    /// </summary>
    /// <param name="session">The login user's session, where <c>wpctl</c> runs.</param>
    /// <param name="files">The filesystem, for "is there any sound hardware here at all".</param>
    /// <param name="expected">
    /// The resource's own expected value, unchanged — an unevaluable observation still says what it
    /// was hoping for, because "could not be determined" on its own tells an operator nothing.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async ValueTask<ResourceObservation?> NotSettledAsync(
        IUserSession session,
        ISystemFiles files,
        string expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);

        if (!HasSoundHardware(files))
        {
            return null;
        }

        var status = await session
            .RunAsync(Executable, ["status"], cancellationToken)
            .ConfigureAwait(false);

        // A wpctl that would not run has learned something real about this machine, so it is drift,
        // and the resource behind this gate is the one that reports it — in its own words, with its
        // own delta. Answering "not settled" here would be exactly the quiet place the reserved
        // meaning forbids.
        return status.Succeeded ? NotSettled(status.StandardOutput, files, expected) : null;
    }

    /// <summary>
    /// The same verdict from a <c>wpctl status</c> the caller already has.
    /// </summary>
    /// <remarks>
    /// <see cref="CameraNodeResource"/> runs that command anyway, so it asks this overload rather
    /// than spawning a second one. The wording is shared for the reason the parsing is: two
    /// resources waiting on one fact must say the same sentence about it.
    /// </remarks>
    /// <param name="status">Whole <c>wpctl status</c> output.</param>
    /// <param name="files">The filesystem, for "is there any sound hardware here at all".</param>
    /// <param name="expected">The resource's own expected value, unchanged.</param>
    public static ResourceObservation? NotSettled(string status, ISystemFiles files, string expected)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);

        if (!HasSoundHardware(files))
        {
            return null;
        }

        if (WpctlStatus.DefaultOf(status, WpctlStatus.Audio, WpctlStatus.Sinks) is not null)
        {
            return null;
        }

        if (WpctlStatus.Entries(status, WpctlStatus.Audio, WpctlStatus.Devices).Count > 0)
        {
            return null;
        }

        return ResourceObservation.Unevaluable(
            expected,
            "wireplumber is running but has not published a media graph yet (no audio device, and no "
            + "default sink for @DEFAULT_AUDIO_SINK@ to name)");
    }

    /// <summary>Whether this machine has ALSA at all (§5.3's virtual agents do not).</summary>
    /// <remarks>
    /// The same question <c>AlsaMixer.HasSoundHardware</c> asks, from the same file. It is asked
    /// here as well because this gate has callers that own no mixer, and because a machine with no
    /// sound hardware would otherwise wait behind an audio fact that can never become true.
    /// </remarks>
    private static bool HasSoundHardware(ISystemFiles files) => files.FileExists(AlsaCards.CardsPath);
}
