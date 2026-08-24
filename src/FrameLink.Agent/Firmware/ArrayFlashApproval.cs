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

    /// <summary>A write began, and no microphone unit is on the bus at all.</summary>
    Wedged,

    /// <summary>A write began and did not finish, but a microphone unit is still answering.</summary>
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
/// <b>The recovery screen is the one that has to work when nothing else does.</b> An array that will
/// not enumerate cannot beep, cannot answer the control tool and cannot be diagnosed from a desk —
/// so the instructions live on the panel, which is the surface that still works, and they are the
/// vendor's own Safe Mode gesture: power off, hold Mute, power on, watch for the blinking red LED.
/// Safe Mode lives in the <b>Factory</b> partition and a write touches the <b>Upgrade</b> partition,
/// which is the whole reason a way back exists at all.
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
    /// </remarks>
    public static ArrayFlashPrompt Writing(ArrayFlashProgress? progress = null) => new()
    {
        Phase = ArrayFlashPhase.Writing,
        Headline = "Updating the microphone — please do not unplug this frame",
        Lines =
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
        ArrayFlashPhase.Wedged => "wedged",
        ArrayFlashPhase.Unfinished => "unfinished",
        _ => "idle",
    };

    /// <summary>The screen a finished write leaves, whichever way it went.</summary>
    public static ArrayFlashPrompt Finished(bool succeeded, bool answerable, OperatorContact? contact) => new()
    {
        Phase = succeeded ? ArrayFlashPhase.Succeeded : ArrayFlashPhase.Failed,
        Headline = succeeded ? "The microphone is up to date" : "The update did not finish",
        Lines = succeeded
            ?
            [
                "It worked, and it is finished.",
                "It is now safe to unplug this frame or switch it off, if you need to.",
                "Nothing else is needed from you.",
            ]
            :
            [
                "It is now safe to unplug this frame or switch it off.",
                "The microphone may not work until somebody has looked at it. Nothing you did caused this, and the "
                    + "rest of the frame is unaffected.",
                ReconcileVoice.ContactLine(contact),
            ],
        Affordance = answerable ? "OK" : null,
        Hold = ArrayFlashApproval.DismissHold,
        Answerable = answerable,
        Alarming = !succeeded,
    };

    /// <summary>
    /// The recovery screen: no microphone unit is on the bus at all, and here is the way back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Five steps, in the order a pair of hands does them.</b> This is the vendor's documented
    /// Safe Mode entry and the only route back from a half-written Upgrade partition. It is written
    /// out on the frame rather than left to a support call because the frame is the thing the person
    /// is standing next to, and because an array in this state has no other way to say anything —
    /// there is no audio cue available from a microphone that will not enumerate.
    /// </para>
    /// <para>
    /// <b>What it does not say is what happens after that</b>, and that is on purpose: the erase and
    /// the re-write are an attended operation with a laptop, not something to talk somebody through
    /// on a living-room screen. The last step names the operator, and the sequence they follow is
    /// recorded in <see cref="ArrayFlashRecovery"/>.
    /// </para>
    /// </remarks>
    public static ArrayFlashPrompt Wedged(bool answerable, OperatorContact? contact) => new()
    {
        Phase = ArrayFlashPhase.Wedged,
        Headline = "The microphone is not answering",
        Lines =
        [
            "The frame cannot find the microphone bar at all. There is a way to wake it up again, and it needs "
                + "somebody to do it by hand:",
            "1. Take the power away from the microphone bar completely.",
            "2. Press and hold the Mute button on the microphone bar, and keep holding it.",
            "3. Still holding it, put the power back on.",
            "4. Watch for a red light that blinks. Once it is blinking you can let the button go.",
            "5. Then say what you have done to whoever looks after your frames — they can put the rest right from there.",
            ReconcileVoice.ContactLine(contact),
        ],
        Affordance = answerable ? "OK" : null,
        Hold = ArrayFlashApproval.DismissHold,
        Answerable = answerable,
        Alarming = true,
    };

    /// <summary>
    /// A write began and did not finish, and a microphone unit is still on the bus.
    /// </summary>
    /// <remarks>
    /// <b>The honest half of the detection boundary.</b> An array that does not enumerate is
    /// something this agent can see. An array that enumerates and misbehaves is not — the frame has
    /// no reading that separates a good flash from a bad one beyond the version the unit reports,
    /// and a unit can report the right version while behaving badly. So this screen says a write was
    /// interrupted, and says the frame cannot tell whether the microphone is well, rather than
    /// claiming either.
    /// </remarks>
    public static ArrayFlashPrompt Unfinished(bool answerable, OperatorContact? contact) => new()
    {
        Phase = ArrayFlashPhase.Unfinished,
        Headline = "A microphone update was interrupted",
        Lines =
        [
            "This frame started updating its microphone and did not reach the end.",
            "The microphone is still answering, so it may well be fine — but the frame cannot tell, and it will not "
                + "try again on its own.",
            ReconcileVoice.ContactLine(contact),
        ],
        Affordance = answerable ? "OK" : null,
        Hold = ArrayFlashApproval.DismissHold,
        Answerable = answerable,
        Alarming = true,
    };
}

/// <summary>
/// The sequence a person follows once the Safe Mode gesture has been done — recorded, never run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing in this product performs any of this.</b> Decision 91 makes exactly one write
/// reachable from the agent — the pinned target image onto alt setting 1 — and recovery is an
/// attended operation with somebody's finger on a button. What lives here is the sequence itself,
/// beside the screen that tells a person to start it, because the two easy-to-miss details below
/// were established once and would otherwise be rediscovered by whoever is holding the board.
/// </para>
/// <para>
/// <b>The erase terminates at about 96% with an error, and that is the expected outcome.</b>
/// <c>dfu-util</c> reports <c>dfuERROR status(8) … out of range</c> near the end of writing
/// <c>4mb_all_ff.bin</c>; the erase has done its job and the message is not a failure to react to.
/// Anyone who reads it as one retries, and retrying a partial write is the documented route from a
/// recoverable board to an unrecoverable one.
/// </para>
/// <para>
/// <b>A power cycle is required between the erase and the next write</b>, or the download fails at
/// 0%. That is not in the upstream instructions, and it is the second of the two details decision 91
/// records for exactly that reason.
/// </para>
/// </remarks>
public static class ArrayFlashRecovery
{
    /// <summary>The gesture, as the panel words it.</summary>
    public static IReadOnlyList<string> SafeModeSteps { get; } =
    [
        "Take the power away from the microphone bar completely.",
        "Press and hold the Mute button, and keep holding it.",
        "Still holding it, put the power back on.",
        "Watch for a red light that blinks; once it blinks, let go.",
    ];

    /// <summary>What an operator does from there, in order.</summary>
    public static IReadOnlyList<string> OperatorSteps { get; } =
    [
        "Confirm Safe Mode was entered: the DFU tool lists a third alt setting on the unit.",
        "Erase the Upgrade partition with the pinned 4mb_all_ff.bin.",
        "Expect the erase to stop at about 96% with dfuERROR status(8) ... out of range. That is the expected outcome, not a failure, and retrying it is what turns a recoverable board into an unrecoverable one.",
        "Power-cycle the microphone unit. Without this the next download fails at 0%.",
        "Write the pinned fallback firmware back onto the unit.",
        "Read the version twice — the USB descriptor and the control tool — and check the two agree.",
        "Remove " + ArrayFlashWindow.MarkerFileName + " from the agent's state directory, which is what lets this frame flash again.",
    ];
}

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

    /// <summary>How long a finished screen waits for somebody who may never come.</summary>
    /// <remarks>
    /// The completion screen is dismissed by a person — the operator asked for an OK-to-continue
    /// affordance rather than a silent resumption. A frame flashed under the operator bypass has
    /// nobody to press it, so the screen cannot wait for ever without holding a household's photos
    /// hostage over a write that has already finished. This is the bound; the outcome itself is in
    /// the event trail, the journal and the Fleet Manager regardless of who read the screen.
    /// </remarks>
    public static TimeSpan CompletionLinger { get; } = TimeSpan.FromMinutes(15);

    private readonly AgentStatusHub _hub;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;
    private readonly Lock _gate = new();

    private long _writing;
    private string? _approvedFor;
    private string? _asked;
    private DateTimeOffset? _askingSince;
    private DateTimeOffset? _restingUntil;
    private DateTimeOffset? _shownAt;

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
    public long BeginWriting() => Interlocked.Increment(ref _writing);

    /// <summary>Shows the write-in-progress screen, with whatever is known about it so far.</summary>
    /// <param name="epoch">The value <see cref="BeginWriting"/> returned for this write.</param>
    /// <param name="progress">How far it has got, or null before anything is known.</param>
    public void Writing(long epoch, ArrayFlashProgress? progress = null)
    {
        if (Interlocked.Read(ref _writing) != epoch)
        {
            return;
        }

        Publish(ArrayFlashVoice.Writing(progress));
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
            ArrayFlashPhase.Writing => ArrayFlashVoice.Writing(prompt.Progress),
            ArrayFlashPhase.Succeeded => ArrayFlashVoice.Finished(true, Answerable, contact),
            ArrayFlashPhase.Failed => ArrayFlashVoice.Finished(false, Answerable, contact),
            ArrayFlashPhase.Wedged => ArrayFlashVoice.Wedged(Answerable, contact),
            ArrayFlashPhase.Unfinished => ArrayFlashVoice.Unfinished(Answerable, contact),
            _ => prompt,
        });
    }

    /// <summary>Shows the outcome, and starts the clock on how long it stays there.</summary>
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
            _shownAt = _clock.UtcNow;
        }

        Publish(ArrayFlashVoice.Finished(succeeded, Answerable, _hub.Current.Contact));
    }

    /// <summary>
    /// Shows the recovery screen for a frame whose previous write did not finish.
    /// </summary>
    /// <param name="arrayAttached">Whether any microphone unit is on this frame's USB bus.</param>
    public void Interrupted(bool arrayAttached)
    {
        var now = _clock.UtcNow;

        lock (_gate)
        {
            if (_restingUntil is { } until && now < until)
            {
                return;
            }

            _shownAt ??= now;
        }

        Publish(arrayAttached
            ? ArrayFlashVoice.Unfinished(Answerable, _hub.Current.Contact)
            : ArrayFlashVoice.Wedged(Answerable, _hub.Current.Contact));
    }

    /// <summary>Clears any screen this owns. The ordinary end of every tick that writes nothing.</summary>
    /// <remarks>
    /// A completed or interrupted write's screen is the exception and is left alone until somebody
    /// takes its affordance or <see cref="CompletionLinger"/> runs out, whichever comes first.
    /// </remarks>
    public void Withdraw()
    {
        if (Prompt is not { } prompt)
        {
            return;
        }

        if (prompt.Phase is ArrayFlashPhase.Asking)
        {
            lock (_gate)
            {
                _askingSince = null;
            }

            Publish(null);
            return;
        }

        var now = _clock.UtcNow;

        lock (_gate)
        {
            if (_shownAt is { } shown && now - shown < CompletionLinger)
            {
                return;
            }
        }

        Dismiss();
    }

    /// <summary>Puts the current screen away and rests before showing another.</summary>
    private void Dismiss()
    {
        // For the same reason Finished does it: whatever takes the screen away wins over a progress
        // frame that was already in flight when it did.
        Interlocked.Increment(ref _writing);

        lock (_gate)
        {
            _shownAt = null;
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
