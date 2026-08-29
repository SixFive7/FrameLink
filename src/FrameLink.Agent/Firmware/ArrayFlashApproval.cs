using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Agent.Firmware;

/// <summary>What the frame's own screen is saying about a firmware write, if anything.</summary>
public enum ArrayFlashPhase
{
    /// <summary>Nothing to say. The ordinary state of every frame in the fleet.</summary>
    Idle,

    /// <summary>Somebody at the frame is being asked whether the write may start.</summary>
    Asking,

    /// <summary><c>dfu-util</c> is running right now.</summary>
    Writing,

    /// <summary>The write finished and the array came back on the pinned firmware.</summary>
    Succeeded,

    /// <summary>The write finished and the array did not come back on the pinned firmware.</summary>
    Failed,

    // <b>A Wedged phase stood here and is named rather than left as a gap.</b> It was the screen
    // a frame showed when a write had begun and no microphone unit was on the bus at all: the
    // vendor's Safe Mode gesture, five numbered steps, for somebody to perform with their hands. It
    // went on 2026-08-24 with the whole of this project's Safe Mode support — see
    // <see cref="ArrayFlashVoice"/> for the account and for what it costs. A write that did not
    // finish now shows <see cref="Unfinished"/> whether or not the unit came back, because the one
    // thing this frame can honestly say about either case is that it cannot tell.

    /// <summary>A write began and did not finish.</summary>
    Unfinished,
}

/// <summary>
/// What the frame's screen shows about a firmware write — the whole of it, composed once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sentences rather than facts, and that is deliberate.</b> §2.7's two stages have to say the
/// same thing about the same frame, and decision 83 records what happens when each surface composes
/// its own version of a state: they disagree, and the one nobody is looking at is the one that is
/// wrong. So the wording is decided in <see cref="ArrayFlashVoice"/>, carried here, and each medium
/// only renders it — the console in a box on <c>/dev/tty8</c>, the browser in a full-screen overlay.
/// </para>
/// <para>
/// <b>The lines are a list because the recovery gesture is a list.</b> Every other screen in this
/// product is a headline and two or three sentences; the one screen that has to be followed with
/// somebody's hands on the hardware is five numbered steps, and flattening those into a paragraph
/// is how a person loses their place half way through holding a button down.
/// </para>
/// </remarks>
public sealed record ArrayFlashPrompt
{
    /// <summary>Which screen this is.</summary>
    public required ArrayFlashPhase Phase { get; init; }

    /// <summary>The one sentence somebody reads from across the room.</summary>
    public required string Headline { get; init; }

    /// <summary>The body, one sentence or one numbered step per entry.</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>
    /// The label on the affordance this screen offers, or null when it offers none.
    /// </summary>
    /// <remarks>
    /// Null on <see cref="ArrayFlashPhase.Writing"/> and only there: a write in progress is the one
    /// screen in this product with nothing a person may usefully do, and drawing a button on it
    /// would invite exactly the interruption the whole feature exists to prevent.
    /// </remarks>
    public string? Affordance { get; init; }

    /// <summary>How long a finger has to stay on the panel to take that affordance.</summary>
    public TimeSpan Hold { get; init; } = ArrayFlashApproval.ApprovalHold;

    /// <summary>Whether this screen may be answered on this frame's own touchscreen.</summary>
    public bool Answerable { get; init; }

    /// <summary>Whether the accent should read as a refusal rather than as work in progress.</summary>
    public bool Alarming { get; init; }

    /// <summary>
    /// How far a write in flight has got, or null on every screen that is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Carried beside the sentences rather than folded into them</b>, because the two surfaces
    /// draw it differently and neither may compose it. The frame's own screen draws a bar with the
    /// stage named underneath, the Fleet Manager draws a bar with the byte count beside it, and both
    /// need the numbers rather than a sentence containing them — while the <i>words</i> around the
    /// bar stay where every other word on these screens is decided, in
    /// <see cref="ArrayFlashVoice"/>.
    /// </para>
    /// <para>
    /// Null on <see cref="ArrayFlashPhase.Writing"/> too, for the instant between the screen going
    /// up and the first reading arriving — which is why every surface treats it as optional rather
    /// than as something a writing screen is guaranteed to have.
    /// </para>
    /// </remarks>
    public ArrayFlashProgress? Progress { get; init; }

    /// <summary>Everything on this screen as one string, so two screens can be compared.</summary>
    /// <remarks>
    /// <b>A record's generated equality is the wrong instrument here, and quietly so.</b>
    /// <see cref="Lines"/> is a list, and the compiler compares it by reference — so two screens
    /// carrying identical sentences are unequal whenever they were built by two calls, which is
    /// every call. Publishing on that would repaint the console and re-send the page a frame on
    /// every tick for as long as a question is up, for a screen whose whole content is fixed. This
    /// is what <see cref="ArrayFlashApproval"/> compares instead.
    /// </remarks>
    public string Signature => string.Join(
        Environment.NewLine,
        [
            Phase.ToString(),
            Headline,
            Affordance ?? string.Empty,
            Alarming.ToString(),
            Progress?.Signature ?? string.Empty,
            .. Lines,
        ]);
}

/// <summary>
/// <b>Every word this product says to the person standing in front of a frame about a firmware
/// write</b>, and the register it says them in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reader is a family member, not an administrator.</b> The rule this repository applies to
/// the plain-language blocks of a build guide — write for somebody with practically no computer
/// experience — is the register here, and it is load-bearing rather than stylistic. The only
/// mitigation that exists for mains loss during a DFU write is a human being who knows not to unplug
/// the frame, and a person who does not understand the sentence cannot act on it. So there are no
/// version numbers in a headline, no <c>dfu-util</c>, no digests, and no "firmware" in the first
/// line of anything: there is a microphone, it is being updated, it must not lose power, and it
/// takes about two minutes.
/// </para>
/// <para>
/// <b>What each screen must contain is fixed by what it is for.</b> The asking screen names the
/// consequence of getting it wrong in the plainest words available — <i>the microphone can be
/// broken for good</i> — because a warning that hedges is a warning nobody weighs. The writing
/// screen repeats <i>do not unplug</i> for as long as the write lasts, because the person who agreed
/// to it two minutes ago may not be the person who walks past it now. Both completion screens lead
/// with <b>it is now safe to unplug</b>, because the frame asked somebody to stand guard and owes
/// them the moment they are released.
/// </para>
/// <para>
/// <b>There is no recovery screen any more, and its removal is the largest thing on this page.</b>
/// A screen used to stand here for an array that would not enumerate: the vendor's Safe Mode
/// gesture — power off, hold Mute, power on, watch for the blinking red LED — five numbered steps
/// written on the panel because the panel is the surface that still works when the microphone does
/// not. On 2026-08-24 the operator dropped this project's support for Safe Mode entirely: no
/// runbook, no on-screen instructions, no wedged-board detection. A board that has stopped
/// presenting itself over USB goes back to the maintainer.
/// <br/><br/>
/// <b>What that costs, said once and not argued.</b> Safe Mode is firmware in the board's Factory
/// partition that no DFU write can touch, and it is the <i>only</i> route back from a board that
/// has stopped enumerating — which is exactly the state in which the agent's own write cannot help,
/// because that write detaches a working device into DFU mode and there is no working device. After
/// this change the software has no recovery path for that state at all.
/// <br/><br/>
/// <b>The knowledge was not deleted.</b> <c>reference/xvf3800-recovery-model.md</c> is the measured
/// record — what Safe Mode is, what the Factory partition guarantees, what every recovery route
/// actually costs — and it stands unchanged. What went is the product's support for the procedure,
/// not the finding behind it.
/// </para>
/// </remarks>
public static class ArrayFlashVoice
{
    /// <summary>Roughly how long a write takes, in the only unit that helps here.</summary>
    /// <remarks>
    /// Upstream reports the write at about thirty seconds and this repository has never measured
    /// one, so the screen says <i>about two minutes</i> — deliberately generous. A person told two
    /// minutes who waits one is relieved; a person told thirty seconds who waits ninety starts
    /// wondering whether it has hung, and the thing they reach for when they wonder that is the plug.
    /// </remarks>
    public const string Duration = "about two minutes";

    /// <summary>The asking screen — the one decision only a human can make.</summary>
    public static ArrayFlashPrompt Asking(bool answerable, OperatorContact? contact) => new()
    {
        Phase = ArrayFlashPhase.Asking,
        Headline = "This frame needs to update its microphone",
        Lines = answerable
            ?
            [
                "Somebody who looks after your frames has asked this frame to update the microphone bar it listens through.",
                "It takes " + Duration + ".",
                "While it is happening the frame has to stay switched on. If the power goes off part-way — unplugged, "
                    + "switched off at the wall, or a power cut — the microphone can be broken for good, and somebody "
                    + "will have to come and repair it.",
                "Your photos and everything else on the frame are safe either way.",
                "If now is not a good time, leave this alone. Nothing will happen, and it will ask again later.",
            ]
            :
            [
                "Somebody who looks after your frames has asked this frame to update the microphone bar it listens through.",
                "This frame's screen cannot be touched, so the update cannot be agreed to here — and nothing will be "
                    + "written until somebody agrees to it.",
                ReconcileVoice.ContactLine(contact),
            ],
        Affordance = answerable ? "Yes — go ahead" : null,
        Hold = ArrayFlashApproval.ApprovalHold,
        Answerable = answerable,
    };

    /// <summary>The console's own sentence for taking the screen's affordance.</summary>
    /// <remarks>
    /// The browser draws a button and the console cannot: it paints a character grid on a
    /// framebuffer the kernel has turned 90° while the digitiser reports unrotated panel pixels, so
    /// a drawn button would answer somewhere other than where it appears (decision 77). A hold needs
    /// no coordinates. <b>It is five seconds rather than the retry's three</b>, because this is the
    /// one gesture on this frame that starts something nothing can undo, and a brush past a screen
    /// at eye level in a living room must not be able to reach it.
    /// </remarks>
    public static string HoldLine(ArrayFlashPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (prompt.Affordance is null || !prompt.Answerable)
        {
            return string.Empty;
        }

        var seconds = (int)prompt.Hold.TotalSeconds;

        return prompt.Phase is ArrayFlashPhase.Asking
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"When you are ready, and you are sure nobody will unplug the frame or switch it off, hold your finger anywhere on this screen for {seconds} seconds.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Hold your finger anywhere on this screen for {seconds} seconds to put this away.");
    }

    /// <summary>The writing screen. Shown however the write came to be agreed to.</summary>
    /// <param name="progress">How far the write has got, or null before anything is known.</param>
    /// <param name="attempt">
    /// Which write of the operation this is, counting from one. Anything past the first says so on
    /// the screen.
    /// </param>
    /// <remarks>
    /// <para>
    /// It is drawn on a frame nobody approved locally too — an operator bypass means nobody was
    /// standing there when it started, not that nobody will walk past while it runs.
    /// </para>
    /// <para>
    /// <b>The one line that changes is the first one, and it is what turns a static warning into
    /// evidence that something is happening.</b> This screen used to say the same three sentences
    /// for the whole write, so a write in progress and a frame that had died half way through it
    /// looked identical — and the thing a person reaches for when they decide a screen has hung is
    /// the plug, which is the exact outcome the screen exists to prevent. What goes in front of the
    /// warning is therefore a named stage and, while the bytes are moving, how far they have got.
    /// </para>
    /// <para>
    /// <b>A retry says it is a retry, and that sentence exists for the same reason as the stage
    /// line.</b> The operator's decision lets one authorisation write up to
    /// <c>ArrayFirmwareFlash.MaxAttempts</c> times, so somebody standing at a frame can watch the
    /// bar reach the end and then start again from nothing — which, unexplained, is exactly what a
    /// frame that has crashed and restarted its own screen looks like. It is deliberately not
    /// alarming: it says what is happening and repeats that the power must stay on.
    /// </para>
    /// </remarks>
    public static ArrayFlashPrompt Writing(ArrayFlashProgress? progress = null, int attempt = 1) => new()
    {
        Phase = ArrayFlashPhase.Writing,
        Headline = "Updating the microphone — please do not unplug this frame",
        Lines = attempt > 1
            ?
            [
                StageLine(progress),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The first try did not take, so the frame is sending it again — this is try {attempt} of "
                    + $"{ArrayFirmwareFlash.MaxAttempts}. Nothing is broken and there is nothing for you to do."),
                "This takes " + Duration + ".",
                "Please leave the frame switched on until this screen says it has finished.",
                "Taking the power away now can break the microphone for good.",
            ]
            :
            [
                StageLine(progress),
                "This takes " + Duration + ".",
                "Please leave the frame switched on until this screen says it has finished.",
                "Taking the power away now can break the microphone for good.",
            ],
        Affordance = null,
        Answerable = false,
        Progress = progress,
    };

    /// <summary>
    /// What a write is doing right now, in words a family member can read from across a room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every stage gets a sentence, including the ones with no number behind them.</b> The bar
    /// reaches 100% at the end of the download and then the unit spends time committing what it
    /// received to its own flash, resets, drops off the USB bus and comes back — twenty seconds and
    /// more with nothing measurable happening. A bar sitting at 100% with no words beside it is
    /// worse than no bar at all, because the person watching it concludes the frame has hung.
    /// </para>
    /// <para>
    /// <b>No jargon reaches this screen.</b> <c>dfuMANIFEST</c> is <i>saving the update</i>,
    /// re-enumeration is <i>waiting for the microphone to come back</i>, and the percentage is the
    /// only number on it. The tool's own words are kept — <see cref="ArrayFlashProgress.Line"/>
    /// carries them verbatim — and they go to the Fleet Manager, where somebody who wants them is
    /// already looking.
    /// </para>
    /// </remarks>
    public static string StageLine(ArrayFlashProgress? progress)
    {
        if (progress is null)
        {
            return "Getting the microphone ready.";
        }

        var elapsed = progress.Elapsed >= TimeSpan.FromSeconds(5)
            ? string.Create(CultureInfo.InvariantCulture, $" ({(int)progress.Elapsed.TotalSeconds} seconds so far.)")
            : string.Empty;

        return progress.Stage switch
        {
            ArrayFlashStages.Downloading when progress.Percent is { } percent => string.Create(
                CultureInfo.InvariantCulture,
                $"Sending the update to the microphone — {percent}% done.{elapsed}"),
            ArrayFlashStages.Downloading => "Sending the update to the microphone." + elapsed,
            ArrayFlashStages.Manifesting =>
                "The microphone is saving the update. This is the slowest part, and nothing will seem to happen for a "
                    + "little while." + elapsed,
            ArrayFlashStages.Settling => "The microphone has finished saving the update." + elapsed,
            ArrayFlashStages.Resetting => "Restarting the microphone." + elapsed,
            ArrayFlashStages.ReEnumerating => "Waiting for the microphone to come back." + elapsed,
            ArrayFlashStages.Verifying => "Checking that the microphone has the update." + elapsed,
            _ => "Getting the microphone ready." + elapsed,
        };
    }

    /// <summary>The firmware screen's phase, in the one spelling every surface uses.</summary>
    /// <remarks>
    /// <b>Named rather than numbered, and named once.</b> The browser stage sends it to the page,
    /// the self-report carries it to the Fleet Manager, and a consumer that does not recognise a
    /// name renders what it was sent instead of guessing at a state from an integer whose meaning
    /// moved. Two spellings of the same set would be decision 83's failure with no second
    /// implementation needed to produce it.
    /// </remarks>
    public static string NameOf(ArrayFlashPhase phase) => phase switch
    {
        ArrayFlashPhase.Asking => "asking",
        ArrayFlashPhase.Writing => "writing",
        ArrayFlashPhase.Succeeded => "succeeded",
        ArrayFlashPhase.Failed => "failed",
        ArrayFlashPhase.Unfinished => "unfinished",
        _ => "idle",
    };

    /// <summary>The screen a finished write leaves, whichever way it went.</summary>
    /// <remarks>
    /// <para>
    /// <b>It names the restart, because the write is a resource's Act now and every Act crosses a
    /// reboot</b> (§2.4). Before the flash moved into the graph nothing followed the write, and this
    /// screen stayed until somebody pressed it or the agent restarted — decision 93's choice, and
    /// its own account already named an agent restart as the other thing that ends it. What changed
    /// is that the restart now comes soon rather than eventually, and a screen saying <i>it is
    /// finished</i> on a frame that is about to reboot without saying so is a surprise this product
    /// does not otherwise hand people.
    /// </para>
    /// <para>
    /// <b>The line is a prediction and one thing can falsify it</b>: decision 79's reboot floor, or
    /// another hold, can refuse that reboot. A frame in that state says so in its own narration, so
    /// the correction reaches the same panel — and the alternative wording, which would have
    /// described the policy rather than what is about to happen, is not a sentence anybody standing
    /// in a living room can act on.
    /// </para>
    /// </remarks>
    public static ArrayFlashPrompt Finished(bool succeeded, bool answerable, OperatorContact? contact) => new()
    {
        Phase = succeeded ? ArrayFlashPhase.Succeeded : ArrayFlashPhase.Failed,
        Headline = succeeded ? "The microphone is up to date" : "The update did not finish",
        Lines = succeeded
            ?
            [
                "It worked, and it is finished.",
                "This frame will restart itself in a moment, to watch the microphone come back on its own.",
                "It is now safe to unplug this frame or switch it off, if you need to.",
                "Nothing else is needed from you.",
            ]
            :
            [
                "It is now safe to unplug this frame or switch it off.",
                "This frame will restart itself in a moment. That is part of checking what happened and is not "
                    + "another attempt at the update.",
                "The microphone may not work until somebody has looked at it. Nothing you did caused this, and the "
                    + "rest of the frame is unaffected.",
                ReconcileVoice.ContactLine(contact),
            ],
        Affordance = answerable ? "OK" : null,
        Hold = ArrayFlashApproval.DismissHold,
        Answerable = answerable,
        Alarming = !succeeded,
    };

    /// <summary>A write began and did not finish.</summary>
    /// <remarks>
    /// <b>The honest half of the detection boundary, and now the whole of it.</b> The frame has no
    /// reading that separates a good flash from a bad one beyond the version the unit reports, and a
    /// unit can report the right version while behaving badly. So this screen says a write was
    /// interrupted and says the frame cannot tell whether the microphone is well, rather than
    /// claiming either.
    /// <br/><br/>
    /// <b>It no longer branches on whether the unit is on the bus.</b> It used to: a unit that had
    /// not come back got a different screen carrying the Safe Mode gesture. That screen and the
    /// detection behind it went with this project's Safe Mode support, so both cases now get this
    /// one sentence — which was always the true one, because a unit that is answering is not
    /// thereby well and a unit that is silent is not thereby recoverable by anybody standing at the
    /// frame.
    /// </remarks>
    public static ArrayFlashPrompt Unfinished(bool answerable, OperatorContact? contact) => new()
    {
        Phase = ArrayFlashPhase.Unfinished,
        Headline = "A microphone update was interrupted",
        Lines =
        [
            "This frame started updating its microphone and did not reach the end.",
            "The frame cannot tell whether the microphone is well, and it will not try again on its own.",
            ReconcileVoice.ContactLine(contact),
        ],
        Affordance = answerable ? "OK" : null,
        Hold = ArrayFlashApproval.DismissHold,
        Answerable = answerable,
        Alarming = true,
    };

}

// == ArrayFlashRecovery was removed on 2026-08-24, and this is the account of it =============
//
// It held two lists and ran neither: `SafeModeSteps`, the Mute-hold gesture as the panel worded it,
// and `OperatorSteps`, the sequence a person followed afterwards — confirm Safe Mode by the third
// alt setting, erase with 4mb_all_ff.bin, expect that erase to stop at 96% with an error that is
// not a failure, power-cycle, write the fallback, read the version twice, remove the marker. Its
// doc comment carried the two procedural details that were easy to get wrong and a correction to a
// vendor claim this repository had made up.
//
// The operator removed this project's support for Safe Mode entirely: no runbook here, no
// `fl.py array runbook`, no on-screen recovery instructions, no wedged-board detection. A board that
// has stopped presenting itself over USB goes back to the maintainer. Half of these steps had
// already gone with the recovery kit — the erase image and the fallback firmware are no longer
// pinned, so four of the seven named files this frame does not carry.
//
// WHAT IT COSTS, said once. Safe Mode is firmware in the board's Factory partition that no DFU
// write can touch, and it is the only route back from a board that has stopped enumerating — the
// exact state in which ArrayFirmwareFlash cannot help, because its write detaches a *working*
// device into DFU mode. The software now has no recovery path for that state.
//
// WHAT WAS NOT DELETED. reference/xvf3800-recovery-model.md is the measured record of all of it:
// what the Factory partition guarantees, what the erase image did and did not do, the exact
// arithmetic behind the 96% stop, and which recoveries need the gesture. It is unchanged. Nothing
// in the flash path gates on any of this, so its absence cannot refuse a write — that was checked
// rather than assumed.

/// <summary>
/// <b>The interlock no software can provide, provided by a person</b> — decision 91's largest
/// unguarded risk, answered the only way it can be answered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists when five interlocks already do.</b> Every other guard in front of the DFU
/// write protects against software: an hourly self-update killing <c>dfu-util</c> through the
/// cgroup, a reconcile pass rebooting the machine, a second write onto a half-written partition, an
/// unverified image, a re-authorisation nobody meant. <b>Mains loss is not software, and no
/// interlock on this frame can reach it.</b> The frame cannot hold its own power on, cannot see a
/// hand approaching the plug and cannot survive the wall switch. The only mitigation that exists is
/// a human being standing next to the frame who has been told, in words they understand, that taking
/// the power away in the next two minutes can destroy the microphone — and who has said they will
/// not.
/// </para>
/// <para>
/// <b>So the acknowledgement is the interlock, and it is taken at the frame.</b> Not in the Fleet
/// Manager, where the person pressing the button is hundreds of kilometres from the plug; on the
/// panel, by the person who is actually in the room. It reuses the panel's evdev reader rather than
/// opening a second input path — one reader, one poll loop, one published state — and it is a hold
/// rather than a tap for decision 77's reason, at five seconds rather than three because of what it
/// starts.
/// </para>
/// <para>
/// <b>The approval is bound to one authorisation string and lives in memory.</b> Approving flash A
/// does not approve flash B, because <see cref="ApprovedFor"/> is compared against the exact
/// authorisation the flash is about to spend; and an agent restart between the hold and the write
/// loses the approval and asks again, which is the safe direction to fail in. There is deliberately
/// nothing durable here: a durable "somebody said yes" would outlive the person who said it, and
/// what the write actually needs is somebody in the room <i>now</i>.
/// </para>
/// <para>
/// <b>The ask is bounded, and that is a product decision rather than a safety one.</b> The prompt
/// covers the household's photos while it is up, so it asks for <see cref="AskWindow"/> and then
/// stands down for <see cref="RestWindow"/> before asking again. An operator's intent is never lost
/// — the authorisation stays armed, the refusal reaches the Fleet Manager, and the frame asks again
/// later — but no frame is left showing a question nobody is going to answer for the rest of the
/// week.
/// </para>
/// </remarks>
public sealed class ArrayFlashApproval
{
    /// <summary>How long a finger stays down to agree to a write.</summary>
    public static TimeSpan ApprovalHold { get; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a finger stays down to put a finished screen away.</summary>
    public static TimeSpan DismissHold { get; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the frame asks before standing down.</summary>
    public static TimeSpan AskWindow { get; } = TimeSpan.FromMinutes(30);

    /// <summary>How long it leaves the screen alone before asking again.</summary>
    public static TimeSpan RestWindow { get; } = TimeSpan.FromHours(6);

    // <b>A CompletionLinger stood here and is named rather than left as a gap.</b> It was fifteen
    // minutes, after which a finished screen took itself away, and its stated reason was that a
    // frame flashed under the operator bypass has nobody to press anything — so the screen "cannot
    // wait for ever without holding a household's photos hostage over a write that has already
    // finished". The operator has decided the other way, on both outcomes: a completed write leaves
    // a screen that stays until somebody acknowledges it. The trade is deliberate and it is worth
    // stating, because the old reasoning was not wrong about the mechanism, only about which side of
    // it matters. What is bought is that nobody can miss the result of the one operation on this
    // frame that cannot be undone; what is paid is that a frame whose panel cannot be touched
    // (Answerable is false, so Finished offers no affordance) now shows that screen until the agent
    // restarts. The outcome itself is in the event trail, the journal and the Fleet Manager
    // regardless of who read the screen, which is what makes the cost bearable rather than absent.

    private readonly AgentStatusHub _hub;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;
    private readonly Lock _gate = new();

    private long _writing;
    private int _attempt = 1;
    private string? _approvedFor;
    private string? _asked;
    private DateTimeOffset? _askingSince;
    private DateTimeOffset? _restingUntil;

    /// <summary>Creates the approval over one frame's screen.</summary>
    public ArrayFlashApproval(AgentStatusHub hub, IAgentClock clock, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        _hub = hub;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// What to do the instant somebody agrees — the budget reset that lets the pass resume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this the frame could never come back from its own question.</b>
    /// <c>firmware.xvf3800.consent</c> is a gate, so a frame nobody has agreed to a write on stops
    /// the pass and escalates — and §2.5 rung 2 means an escalated resource is not observed again
    /// until somebody resets its budget. A household pressing <i>yes</i> would otherwise be agreeing
    /// to a write on a frame that had stopped listening for the answer.
    /// </para>
    /// <para>
    /// It is §2.5 rung 5's press reaching the same reset path the Fleet Manager's retry reaches,
    /// which is what decision 72 requires of every surface that offers one. The default does nothing,
    /// which is correct where there is no loop to reset — the suite, and any catalog built off a
    /// frame.
    /// </para>
    /// </remarks>
    public Action<string>? Agreed { get; init; }

    /// <summary>The authorisation somebody agreed to, or null.</summary>
    public string? ApprovedFor
    {
        get
        {
            lock (_gate)
            {
                return _approvedFor;
            }
        }
    }

    /// <summary>What is on the frame's screen right now, or null.</summary>
    public ArrayFlashPrompt? Prompt => _hub.Current.ArrayFlash;

    /// <summary>Whether a hold on the panel would answer a firmware screen right now.</summary>
    /// <remarks>
    /// The predicate <c>TouchRetry</c> asks before it decides what a completed hold means. It is a
    /// question about the screen rather than about the flash, so a screen with no affordance — a
    /// write in progress — answers false and a hold there does nothing at all.
    /// </remarks>
    public bool Awaiting => Prompt is { Affordance: not null };

    /// <summary>How long the hold on offer is, or null when nothing is on offer.</summary>
    public TimeSpan? Hold => Prompt is { Affordance: not null } prompt ? prompt.Hold : null;

    /// <summary>Whether this frame's panel can answer at all.</summary>
    public bool Answerable => _hub.Current.Touch.Available;

    /// <summary>
    /// Asks the person at the frame, or reports that the asking window has closed for now.
    /// </summary>
    /// <param name="authorisation">The exact authorisation an approval would be bound to.</param>
    /// <returns>Whether the screen is asking right now.</returns>
    public bool Ask(string authorisation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorisation);

        var now = _clock.UtcNow;

        lock (_gate)
        {
            // A different authorisation is a different question, so it gets a fresh window rather
            // than inheriting the rest the previous one earned by going unanswered.
            if (!string.Equals(_asked, authorisation, StringComparison.Ordinal))
            {
                _asked = authorisation;
                _askingSince = null;
                _restingUntil = null;
            }

            if (_restingUntil is { } until && now < until)
            {
                return false;
            }

            if (_askingSince is not { } since)
            {
                _askingSince = now;
                _log.Info(
                    "A firmware write is authorised on this frame, and the screen is asking whoever is standing in "
                    + "front of it to agree to it. Nothing will be written until somebody does.");
            }
            else if (now - since >= AskWindow)
            {
                _askingSince = null;
                _restingUntil = now + RestWindow;
                _log.Warn(
                    "Nobody at the frame agreed to the authorised firmware write within "
                    + string.Create(CultureInfo.InvariantCulture, $"{AskWindow.TotalMinutes:F0} minutes")
                    + ", so the screen has gone back to the product. The authorisation is still armed and the frame "
                    + "will ask again later.");
                Publish(null);
                return false;
            }
        }

        Publish(ArrayFlashVoice.Asking(Answerable, _hub.Current.Contact));
        return true;
    }

    /// <summary>
    /// Ends the rest window, so the next look asks again — <b>a person has arrived</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The trap this closes only appeared once consent became a rung of the graph.</b> The screen
    /// asks for <see cref="AskWindow"/> and then rests for <see cref="RestWindow"/>, which used to
    /// be invisible: a resting frame went back to showing photographs and the authorisation simply
    /// waited. Now <c>firmware.xvf3800.consent</c> has escalated, so a resting frame shows the
    /// stopped-frame screen naming the consent it is waiting for — and offers <i>try again</i>,
    /// which resets the attempt budget and produces the identical screen, because nothing woke the
    /// question. Somebody standing in front of it would have had no way to say yes for six hours.
    /// </para>
    /// <para>
    /// A retry is somebody arriving at the frame, which is the exact condition the rest window
    /// exists to wait for, so it is reached from the one reset every surface already shares
    /// (decision 72). It does not clear which authorisation was asked about: that is the question's
    /// identity, and a different one gets a fresh window of its own in <see cref="Ask"/>.
    /// </para>
    /// </remarks>
    public void Wake()
    {
        lock (_gate)
        {
            _askingSince = null;
            _restingUntil = null;
        }
    }

    /// <summary>Records that this exact write was agreed to, and by what.</summary>
    public void Approve(string authorisation, string how)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorisation);

        lock (_gate)
        {
            _approvedFor = authorisation;
            _askingSince = null;
            _restingUntil = null;
        }

        _log.Info($"The firmware write on this frame was agreed to: {how}.");

        // After the record and outside the lock. The reset walks the journal and logs, and holding
        // this class's gate across it would put the frame's screen and the reconcile ledger behind
        // one lock for no reason anybody could name later.
        Agreed?.Invoke(authorisation);
    }

    /// <summary>Takes the affordance currently on screen, whatever it is.</summary>
    /// <param name="source">Which surface the press came from, for the journal.</param>
    /// <returns>Whether anything was taken.</returns>
    /// <remarks>
    /// One entry point for both surfaces and both meanings. The console reaches it by a hold and the
    /// browser by a button, and which of <i>yes, go ahead</i> and <i>OK, put this away</i> it means
    /// is decided by what is on the screen rather than by which caller arrived — so the two surfaces
    /// cannot come to offer different things, and a press can never mean something other than what
    /// the sentence above it said.
    /// </remarks>
    public bool Answer(string source)
    {
        if (Prompt is not { Affordance: not null } prompt)
        {
            return false;
        }

        if (prompt.Phase is ArrayFlashPhase.Asking)
        {
            string? asked;
            lock (_gate)
            {
                asked = _asked;
            }

            if (asked is not { Length: > 0 })
            {
                return false;
            }

            Approve(asked, $"somebody at the frame agreed to it on the panel ({source})");
            Publish(null);
            return true;
        }

        _log.Info($"The firmware screen was put away at the frame ({source}).");
        Dismiss();
        return true;
    }

    /// <summary>
    /// Claims the screen for a write that is about to start, and publishes nothing.
    /// </summary>
    /// <returns>
    /// The epoch every later <see cref="Writing(long, ArrayFlashProgress?)"/> for this write must
    /// present.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Claiming and painting are separated because only one of them is safe on the writing
    /// thread.</b> This is an interlocked increment and returns immediately;
    /// <see cref="Publish"/> calls every hub subscriber synchronously, and on a frame those
    /// subscribers write a frame to <c>/dev/tty8</c> and send one to the browser. So the write's own
    /// task takes the epoch here and never publishes again until the write is over — every frame in
    /// between is drawn by <see cref="ArrayFlashProgressPump"/>, on a task of its own.
    /// </para>
    /// <para>
    /// <b>The epoch is what stops a late frame resurrecting a finished write.</b> The pump is
    /// deliberately never waited for, so a publish that was in flight when the write ended can
    /// arrive after <see cref="Finished"/> has already put the outcome on the screen. Every
    /// screen-taking call on this class moves the epoch on, and a progress frame presenting an old
    /// one is dropped rather than drawn.
    /// </para>
    /// </remarks>
    public long BeginWriting(int attempt = 1)
    {
        lock (_gate)
        {
            _attempt = attempt;
        }

        return Interlocked.Increment(ref _writing);
    }

    /// <summary>Shows the write-in-progress screen, with whatever is known about it so far.</summary>
    /// <param name="epoch">The value <see cref="BeginWriting"/> returned for this write.</param>
    /// <param name="progress">How far it has got, or null before anything is known.</param>
    public void Writing(long epoch, ArrayFlashProgress? progress = null)
    {
        if (Interlocked.Read(ref _writing) != epoch)
        {
            return;
        }

        Publish(ArrayFlashVoice.Writing(progress, Attempt));
    }

    /// <summary>Which write of the operation is on the screen, counting from one.</summary>
    /// <remarks>
    /// Held here rather than passed through every publish because <see cref="Refresh"/> recomposes a
    /// screen from what is true now, and a refresh that had forgotten the attempt would take the
    /// "trying again" sentence off the panel and the next beat would put it back — a screen that
    /// blinks between two truths being worse than either.
    /// </remarks>
    public int Attempt
    {
        get
        {
            lock (_gate)
            {
                return _attempt;
            }
        }
    }

    /// <summary>Recomposes the current screen from what is true now.</summary>
    /// <remarks>
    /// <b>Both facts a screen is composed from can arrive after it goes up.</b> The touchscreen is
    /// found by a watch of its own — two file reads, on its own cadence, and there is no ordering
    /// between that and the first look at the authorisation — so a screen composed a moment too
    /// early tells a household its panel cannot be touched while they are touching it. And the
    /// operator's name and telephone number arrive over the link (decision 71), so a recovery screen
    /// that went up before the first settings push would name nobody, which is the one screen where
    /// naming somebody is the last step of the instructions. Recomposing costs one string
    /// comparison, and <see cref="Publish"/> drops it when nothing has changed.
    /// </remarks>
    public void Refresh()
    {
        if (Prompt is not { } prompt)
        {
            return;
        }

        var contact = _hub.Current.Contact;

        Publish(prompt.Phase switch
        {
            ArrayFlashPhase.Asking => ArrayFlashVoice.Asking(Answerable, contact),

            // Recomposed from the progress the screen already carries, never from nothing. A
            // refresh that dropped it would take the bar off the panel every time the touchscreen
            // watch or a settings push happened to land mid-write, and put it back on the next
            // reading — a screen that blinks between "41% done" and "getting ready" is worse than
            // one that never moved.
            ArrayFlashPhase.Writing => ArrayFlashVoice.Writing(prompt.Progress, Attempt),
            ArrayFlashPhase.Succeeded => ArrayFlashVoice.Finished(true, Answerable, contact),
            ArrayFlashPhase.Failed => ArrayFlashVoice.Finished(false, Answerable, contact),
            ArrayFlashPhase.Unfinished => ArrayFlashVoice.Unfinished(Answerable, contact),
            _ => prompt,
        });
    }

    /// <summary>Shows the outcome, and leaves it there until somebody acknowledges it.</summary>
    public void Finished(bool succeeded)
    {
        // Before the screen is taken, not after. The progress pump is never waited for — waiting on
        // it is exactly the coupling this feature exists to avoid — so a frame it had already
        // composed can arrive after this line. Moving the epoch on here is what makes that frame a
        // no-op instead of a write-in-progress screen drawn over a finished one.
        Interlocked.Increment(ref _writing);

        lock (_gate)
        {
            _approvedFor = null;
            _asked = null;
            _askingSince = null;
            _restingUntil = null;
        }

        Publish(ArrayFlashVoice.Finished(succeeded, Answerable, _hub.Current.Contact));
    }

    /// <summary>Shows the screen for a frame whose previous write did not finish.</summary>
    /// <remarks>
    /// It took a <c>bool arrayAttached</c> until 2026-08-24 and chose between two screens with it:
    /// a unit still on the bus got "the frame cannot tell", and a unit that had not come back got
    /// the Safe Mode gesture. The second screen went with this project's Safe Mode support, so the
    /// distinction has nothing left to decide and the argument went with it.
    /// </remarks>
    public void Interrupted()
    {
        var now = _clock.UtcNow;

        lock (_gate)
        {
            if (_restingUntil is { } until && now < until)
            {
                return;
            }
        }

        Publish(ArrayFlashVoice.Unfinished(Answerable, _hub.Current.Contact));
    }

    /// <summary>Clears any screen this owns. The ordinary end of every tick that writes nothing.</summary>
    /// <remarks>
    /// <b>It clears the question and nothing else.</b> A completed or interrupted write's screen is
    /// not a question and is never taken away by a tick: it stays until somebody takes its
    /// affordance, on both outcomes, which is the operator's decision and the reason there is no
    /// linger left in this class. So every caller can go on ending its tick with this call and none
    /// of them can silently retire a result.
    /// </remarks>
    public void Withdraw()
    {
        if (Prompt is not { Phase: ArrayFlashPhase.Asking })
        {
            return;
        }

        lock (_gate)
        {
            _askingSince = null;
        }

        Publish(null);
    }

    /// <summary>Puts the current screen away and rests before showing another.</summary>
    private void Dismiss()
    {
        // For the same reason Finished does it: whatever takes the screen away wins over a progress
        // frame that was already in flight when it did.
        Interlocked.Increment(ref _writing);

        lock (_gate)
        {
            _askingSince = null;
            _restingUntil = _clock.UtcNow + RestWindow;
        }

        Publish(null);
    }

    private void Publish(ArrayFlashPrompt? prompt)
    {
        if (string.Equals(_hub.Current.ArrayFlash?.Signature, prompt?.Signature, StringComparison.Ordinal))
        {
            return;
        }

        _hub.Publish(status => status with { ArrayFlash = prompt });
    }
}
