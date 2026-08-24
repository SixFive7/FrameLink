using System.Globalization;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Reconcile;

/// <summary>What came of asking for one of this frame's remaining automatic restarts.</summary>
/// <param name="Granted">
/// Whether the frame may restart itself. <see langword="false"/> means it must not, whatever any
/// ledger, attempt counter or status says.
/// </param>
/// <param name="Remaining">How many are left afterwards, as best as could be established.</param>
/// <param name="Refusal">
/// A whole sentence saying why not, or null when it was granted. It becomes the observed half of
/// the delta on the frame's own screen and in the operator's notification, which is why it is a
/// sentence rather than a code.
/// </param>
public readonly record struct RebootAllowanceGrant(bool Granted, int Remaining, string? Refusal);

/// <summary>
/// <b>The backstop under the attempt ladder: how many times this frame may restart <i>itself</i>
/// before it has to ask a person.</b> Kept in a file that is counted rather than read, so nothing
/// that can go wrong with it can ever hand a frame more restarts.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap it closes.</b> §2.5's ladder of three is durable state, and every protection built on
/// it inherits the durable state's failure modes. A frame whose card has gone read-only, whose
/// state directory has been removed, or whose journal is truncated on every boot reads an empty
/// ledger, calls the loop death that just happened <i>attempt one of three</i>, restarts, and reads
/// an empty ledger again. Nothing about that is a failure the ladder can see, because from the
/// ladder's point of view it is always the first attempt. <see cref="RebootFloor"/> — decision 79 —
/// does not help: its list of recent reboots lives in the same journal, so it comes back empty
/// alongside everything else. This project has already produced a 55-boot cascade from a single
/// zero, and that is the shape of it.
/// </para>
/// <para>
/// <b>So this counts down rather than up, and absence means exhausted.</b> A counter of restarts
/// <i>taken</i> reads zero when its storage is gone, which is indistinguishable from a frame that
/// has taken none — the exact mistake that buys fresh restarts. A store of restarts <i>remaining</i>
/// reads zero in the same circumstance and refuses, so the failure of this mechanism costs the frame
/// its automatic recovery and never its bounds. What is lost when this misfires is one screen a
/// person has to look at; what is lost when the ladder misfires is the card.
/// </para>
/// <para>
/// <b>The file is never parsed, and that is the whole trick.</b> It holds one
/// <see cref="Token"/> byte per remaining restart and nothing else, and the count is the number of
/// those bytes — not a number written down, not a date, not JSON. Enumerate what can go wrong with
/// such a file: truncated by a power cut mid-write, fewer tokens; garbled by a bad block, fewer
/// tokens, because a byte that is no longer <see cref="Token"/> is not counted; hand-edited and left
/// with a trailing newline, unchanged; absent, none; unreadable, none. <b>Every corruption of this
/// file can only cost the frame restarts, never grant them</b>, so it needs no schema, no version
/// field and no upgrade seam — and it is correct whether or not the write that produced it was
/// atomic, which is why it does not wait on that work.
/// </para>
/// <para>
/// <b>It is refilled by a demonstration of health or by a person, and by nothing else.</b> A process
/// that ran for <see cref="ReconcileOptions.ConflictHold"/> before its loop ended has shown that
/// whatever ended it is not the fault the previous restarts were about — the identical test
/// <see cref="AgentLoopFailures.Record"/> forgives the ladder on, so the two can never disagree
/// about how many restarts a frame gets — and a person pressing <b>Restart and try again</b> refills
/// it for decision 67's reason, that somebody has arrived. A frame in a tight loop reaches neither,
/// which is what makes the bound a bound.
/// </para>
/// <para>
/// <b>What it deliberately does not do is seed itself.</b> A frame that has never once run for the
/// hold has an empty allowance and stops on its first loop death instead of restarting three times
/// first. That is the cost of the property above and it is the right way round: a loop that has
/// never survived five minutes on this machine will not be fixed by power-cycling it, and the
/// alternative — seeding on first run — is a rule that says "no file here means a fresh three",
/// which is precisely the reading this class exists to make impossible.
/// </para>
/// <para>
/// <b>Not on <see cref="IRebootBoundary"/>, unlike decision 79's floor.</b> The floor bounds every
/// reboot on the frame, including the eighty a bare provision takes, so its number is 120 and its
/// window is six hours. This bounds one thing — a frame restarting itself because a piece of its own
/// software stopped — so its number is the ladder's own <see cref="ReconcileOptions.AttemptBudget"/>
/// and it needs no window at all. Putting it on the boundary would mean choosing between breaking a
/// provision and being useless, which is the choice decision 79 already made once.
/// </para>
/// </remarks>
public sealed class RebootAllowance
{
    /// <summary>The file inside the state store, beside the journal it does not trust.</summary>
    public const string FileName = "reboot-allowance";

    /// <summary>
    /// The byte that counts, one per remaining restart.
    /// </summary>
    /// <remarks>
    /// Printable, so <c>cat</c> and <c>wc -c</c> are the whole diagnostic, and counted by identity
    /// rather than by the file's length so that a byte a bad block has turned into something else
    /// stops counting. Length would have been simpler and wrong in the one direction that matters.
    /// </remarks>
    public const byte Token = (byte)'#';

    private readonly IStateStore _store;
    private readonly IAgentLog _log;
    private readonly int _size;

    /// <summary>Creates the allowance over <paramref name="store"/>.</summary>
    /// <param name="store">Where the file lives — the same state directory as the journal.</param>
    /// <param name="log">Where a refusal and a failed write are recorded.</param>
    /// <param name="size">
    /// How many restarts a refill grants. <see cref="ReconcileOptions.AttemptBudget"/> on a frame:
    /// the ladder's own three, so that neither mechanism can promise a restart the other forbids.
    /// Zero or less means a frame never restarts itself, which is what the ladder does with the same
    /// number.
    /// </param>
    public RebootAllowance(IStateStore store, IAgentLog log, int size)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _log = log;
        _size = size;
    }

    /// <summary>What a refill grants.</summary>
    public int Size => _size;

    /// <summary>Where the file is, for the log and for a person with an SSH session.</summary>
    public string Path => _store.PathOf(FileName);

    /// <summary>
    /// The tokens in <paramref name="content"/> — the entire reading of this file.
    /// </summary>
    /// <remarks>
    /// A scan for one byte value, with no encoding, no structure and nothing to throw on. It is
    /// static and pure so that the property the whole design rests on — that no content can produce
    /// a count above the number of <see cref="Token"/> bytes actually present — is assertable
    /// against arbitrary bytes rather than only against bytes this class wrote.
    /// </remarks>
    public static int Count(ReadOnlySpan<byte> content)
    {
        var counted = 0;

        foreach (var value in content)
        {
            if (value == Token)
            {
                counted++;
            }
        }

        return counted;
    }

    /// <summary>
    /// How many automatic restarts are left, or zero when that cannot be established.
    /// </summary>
    /// <remarks>
    /// Read from the card every time rather than cached. The one process that matters here is the
    /// one about to take the frame down, and a cached count is a count from before whatever went
    /// wrong with the card.
    /// </remarks>
    public int Remaining()
    {
        try
        {
            var content = _store.ReadBytes(FileName);
            return content is null ? 0 : Count(content);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Unreadable is none left, never "start again". This is the branch the whole class is
            // built around, so it is said loudly rather than swallowed.
            _log.Fail(
                $"This frame's restart allowance at {Path} could not be read ({exception.Message}), "
                + "so it is treated as spent.");
            return 0;
        }
    }

    /// <summary>
    /// Grants a full allowance again — what a demonstration of health and a person's press both
    /// mean.
    /// </summary>
    /// <returns>Whether the new allowance reached the card.</returns>
    /// <remarks>
    /// A failure here is safe by construction: the allowance stays at whatever it was, which is
    /// never more than it should be. It is still reported, because a frame that cannot write this
    /// file cannot write the journal either, and that is worth knowing before the next fault rather
    /// than after it.
    /// </remarks>
    public bool Refill() => Write(Math.Max(_size, 0), "refill");

    /// <summary>
    /// <b>Takes one restart, or refuses.</b> Called immediately before the frame is asked to
    /// restart itself, never after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three ways this says no, and all three are the same answer: there is nothing left; the
    /// spend could not be written; or the spend was written and reading it back did not show it.
    /// The last one exists because this is the guard whose failure is invisible — a write that
    /// reports success and does not persist is exactly how a bounded counter becomes an unbounded
    /// one, and a frame with a full card or a filesystem remounted read-only under it is the case
    /// where that happens. Verifying costs one read of a file three bytes long.
    /// </para>
    /// <para>
    /// The order is the order the journal uses for the same reason: written before the machine is
    /// asked to go down, because on a frame the process does not come back and anything recorded
    /// after the request is recorded never.
    /// </para>
    /// </remarks>
    public RebootAllowanceGrant TrySpend()
    {
        var remaining = Remaining();

        if (remaining <= 0)
        {
            return new RebootAllowanceGrant(false, 0, Exhausted(_size));
        }

        if (!Write(remaining - 1, "spend"))
        {
            return new RebootAllowanceGrant(false, remaining, NotRecorded);
        }

        var left = Remaining();

        if (left >= remaining)
        {
            _log.Fail(
                $"This frame wrote its restart allowance down to {remaining - 1} at {Path} and read "
                + $"back {left}, so the restart has not been taken.");
            return new RebootAllowanceGrant(false, left, NotRecorded);
        }

        return new RebootAllowanceGrant(true, left, null);
    }

    /// <summary>Why a frame that has spent its allowance is not restarting itself again.</summary>
    /// <remarks>
    /// Written for the person in front of the frame, in the register §2.7 asks for, and it says the
    /// number out loud: somebody who has watched it restart three times has counted, and a screen
    /// that agrees with them is a screen they can believe.
    /// </remarks>
    public static string Exhausted(int size) => string.Create(
        CultureInfo.InvariantCulture,
        $"this frame has already restarted itself {Math.Max(size, 0)} times over this, so it has "
        + $"stopped instead of restarting again");

    /// <summary>Why a frame that cannot count its own restarts is not taking another.</summary>
    /// <remarks>
    /// The honest sentence for the case that matters most. It does not name a cause, because the
    /// causes — a full card, a filesystem remounted read-only, a state directory that has been
    /// removed under a running agent — are indistinguishable from here and the answer is the same
    /// for all of them.
    /// </remarks>
    public const string NotRecorded =
        "this frame could not write down that it was about to restart itself, and it will not take a "
        + "restart it cannot count";

    private bool Write(int tokens, string what)
    {
        try
        {
            _store.WriteText(FileName, new string((char)Token, Math.Max(tokens, 0)));
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _log.Fail($"This frame could not {what} its restart allowance at {Path}: {exception.Message}");
            return false;
        }
    }
}
