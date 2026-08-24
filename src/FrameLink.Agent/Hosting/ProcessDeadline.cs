using System.Globalization;

namespace FrameLink.Agent.Hosting;

/// <summary>
/// How long each kind of command this frame runs is allowed to take before it is stopped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every external command needs a deadline, and one number for all of them would be wrong for
/// all of them.</b> <c>amixer sget</c> answers from the kernel in under a millisecond and
/// <c>apt full-upgrade</c> downloads over the household's own connection and runs maintainer
/// scripts; a bound generous enough for the second is no bound at all on the first, and a bound
/// tight enough for the first would cut the second off mid-<c>dpkg</c>. So the deadline is chosen
/// where the command is chosen, from this list.
/// </para>
/// <para>
/// <b>Every value here is deliberately far beyond the healthy case, because a false timeout is a
/// self-inflicted outage.</b> A timeout fails the resource like any other error and spends one of
/// §2.5's three attempts, so a deadline that fires on a slow-but-working frame walks the repair
/// ladder for no reason and eventually stops a frame that was fine. These numbers are set to catch
/// a command that is <i>never</i> going to answer, not one that is taking a while: where a value
/// could be derived from something the system already promises — systemd's own job timeout, the
/// documented duration of a firmware write — it is derived rather than guessed, and where it could
/// not it is set an order of magnitude above the measured cost.
/// </para>
/// <para>
/// <b>They are constants, not fleet settings, and that is the same rule §2.2 applies to the
/// catalog.</b> A server-supplied deadline is a server-supplied way to hang a frame: setting one to
/// a day would restore exactly the defect this file exists to remove, and setting one to a second
/// would stop every frame in the fleet at once. The values that are worth tuning per fleet are
/// already fleet settings; how long the agent waits for a local tool before deciding it is dead is
/// a property of the tool.
/// </para>
/// </remarks>
public static class ProcessDeadline
{
    /// <summary>
    /// Commands that answer from the kernel or a local file: <b>30 seconds</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>id</c>, <c>pgrep</c>, <c>ps</c>, <c>swapon --show</c>, <c>findmnt</c>, <c>amixer</c>,
    /// <c>dpkg-query</c>, <c>apt-config dump</c>, <c>ss</c>, <c>iw reg get</c>, <c>chown</c>,
    /// <c>usermod</c>, <c>wlr-randr</c>, <c>gpiodetect</c>. None of them opens a socket, waits on a
    /// service manager or touches a device that can stop answering; all of them are measured in
    /// milliseconds on a Pi 5.
    /// </para>
    /// <para>
    /// Thirty seconds is therefore three orders of magnitude of headroom, and it is short for a
    /// second reason: <c>ps</c> is the supervisor's memory probe and <c>pgrep</c> is the screen
    /// handover's only question, and both of those loops tick every few seconds. A deadline of
    /// minutes on a command that cannot legitimately take one would let a wedged <c>/proc</c> hold
    /// §2.10's five behaviours through several ticks before anything said so.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Local = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Anything that asks systemd or D-Bus to do something: <b>2 minutes</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>systemctl</c> in either scope, <c>loginctl</c>, <c>busctl</c>, <c>hostnamectl</c>,
    /// <c>timedatectl</c>, <c>localectl</c>, <c>wpctl</c>, and every command run through
    /// <see cref="IUserSession"/>, which reaches its target through <c>runuser</c> and the user
    /// bus.
    /// </para>
    /// <para>
    /// <b>This one is derived, not chosen.</b> Debian's <c>DefaultTimeoutStartSec</c> and
    /// <c>DefaultTimeoutStopSec</c> are 90 seconds, so a <c>systemctl start</c>, <c>stop</c> or
    /// <c>restart</c> that has not returned after 90 seconds is a job systemd is itself about to
    /// give up on — it will fail the unit and answer. Two minutes clears that with 30 seconds of
    /// slack for a job queued behind another one. A deadline shorter than systemd's own would fire
    /// on jobs systemd was seconds away from completing, which is the false timeout this file is
    /// most afraid of, because §2.7's browser stage lives entirely on these calls.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Service = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A name asked of the resolver: <b>1 minute</b>.
    /// </summary>
    /// <remarks>
    /// <c>getent hosts</c> is the only command the agent runs that can legitimately block on
    /// something off this frame. <c>nsswitch.conf</c> on Raspberry Pi OS sends it to mDNS and then
    /// to DNS, each with its own retries, so a name that nothing will ever answer for can take
    /// twenty seconds or more to fail honestly. A minute is comfortably past that and still not
    /// <see cref="Local"/>, which it would otherwise share and occasionally trip.
    /// </remarks>
    public static readonly TimeSpan Resolver = TimeSpan.FromMinutes(1);

    /// <summary>
    /// <c>xvf_host</c>, talking to the microphone array over USB: <b>90 seconds</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every transaction the agent asks of this tool — read a version, read a GPO, write a GPO — is
    /// a short control transfer that completes in well under a second. What the deadline has to
    /// allow for is not the transaction but the bus: an array that has just been reset
    /// re-enumerates, and a call that lands during that window waits for the device node to come
    /// back.
    /// </para>
    /// <para>
    /// <b>It is also the number that bounds
    /// <see cref="Resources.XvfHost.Conversation"/>.</b> That process-wide semaphore serialises
    /// every <c>xvf_host</c> call in the agent because the tool has no device selector, and its
    /// unbounded wait was justified in-code by the absence of this deadline — a hung tool wedged
    /// the caller with or without the gate, so bounding the gate bought nothing. With the tool
    /// bounded, the longest a caller can wait for the semaphore is one holder's deadline, so the
    /// gate's wait can be bounded too and is.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Array = TimeSpan.FromSeconds(90);

    /// <summary>
    /// <c>dfu-util</c>, writing firmware to the array: <b>5 minutes</b>.
    /// </summary>
    /// <remarks>
    /// The streaming overload of <see cref="IProcessRunner"/> exists for this one command and its
    /// remark records the measured duration: between thirty seconds and two minutes. Five minutes
    /// is two and a half times the documented worst case. It can afford to be generous because this
    /// is the one command a person is watching — the consent screen is up and the progress bar is
    /// being drawn from the tool's own output — so a write that is slow but moving is visibly
    /// different from one that has stopped, which is not true of anything else here.
    /// </remarks>
    public static readonly TimeSpan Firmware = TimeSpan.FromMinutes(5);

    /// <summary>
    /// <c>swapoff</c>: <b>10 minutes</b>.
    /// </summary>
    /// <remarks>
    /// Turning a swap file off is not a bookkeeping change — every page that was swapped out has to
    /// be read back into RAM before the call returns, and on a frame that is short enough of memory
    /// to have used its swap heavily, off an SD card, that is minutes of legitimate work. Ten of
    /// them is far past any plausible success and still finite, which the old behaviour was not.
    /// </remarks>
    public static readonly TimeSpan Storage = TimeSpan.FromMinutes(10);

    /// <summary>
    /// <c>apt-get</c> installing, updating or upgrading: <b>60 minutes</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one command whose healthy duration is genuinely unbounded, so its deadline is
    /// set to be reached only by a hang.</b> It downloads over whatever connection the household
    /// has and then runs maintainer scripts, and an hour covers something like two hundred
    /// megabytes over half a megabit — slower than any link a frame is likely to be on, and the
    /// operator's warning was explicit that a false timeout on a slow-but-working <c>apt</c> is a
    /// self-inflicted outage.
    /// </para>
    /// <para>
    /// <b>The asymmetry is deliberate and it is about <c>dpkg</c>, not about patience.</b> Killing
    /// <c>apt</c> during a transaction leaves the package database half-configured and needing
    /// <c>dpkg --configure -a</c> by hand — a worse state than the hang, and one the agent cannot
    /// repair from. So this deadline is late on purpose: it turns "never" into "an hour", which is
    /// the whole of what is needed, and it buys that without ever being the thing that broke a
    /// working upgrade.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan PackageChange = TimeSpan.FromMinutes(60);

    /// <summary>
    /// How long the pipes are given to close once the tree has been killed: <b>2 seconds</b>.
    /// </summary>
    /// <remarks>
    /// Not a deadline on a command — a courtesy after one. A killed tree closes the write end of
    /// both pipes and the last of the child's output arrives immediately, so this is worth waiting
    /// for and worth reporting. It is short and it is bounded because on a platform where the
    /// grandchild survived the kill it will never elapse into anything: the pipe stays open for as
    /// long as that process lives, and the whole point of the deadline is that the agent stops
    /// waiting for it.
    /// </remarks>
    public static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long this is, in the register the rest of the agent writes durations in.</summary>
    public static string Describe(TimeSpan deadline) =>
        deadline.TotalMinutes >= 1 && deadline.TotalMinutes == Math.Floor(deadline.TotalMinutes)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)deadline.TotalMinutes} minute{((int)deadline.TotalMinutes == 1 ? string.Empty : "s")}")
            : string.Create(CultureInfo.InvariantCulture, $"{deadline.TotalSeconds:0.##} seconds");
}
