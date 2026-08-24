using System.Text.Json;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Reconcile;

/// <summary>
/// A change that has been written and not yet proven to have survived a boot.
/// </summary>
/// <remarks>
/// Written <b>before</b> the reboot is requested, never after. The window between "the machine
/// is going down" and "the journal is on disk" is the one window in which an agent could lose
/// track of what it was doing, and a frame that comes back not knowing it changed something is
/// a frame that will change it again on the next pass, forever.
/// </remarks>
public sealed record PendingApply
{
    /// <summary>The resource whose change is being proven.</summary>
    public required string Resource { get; init; }

    /// <summary>Which attempt this was.</summary>
    public required int Attempt { get; init; }

    /// <summary>The value that was expected at the time of the write.</summary>
    public required string Expected { get; init; }

    /// <summary>The exact change made, verbatim.</summary>
    public required string Change { get; init; }

    /// <summary>Plain-language gloss on <see cref="Change"/>.</summary>
    public string? Gloss { get; init; }

    /// <summary>
    /// The boot the write happened in.
    /// </summary>
    /// <remarks>
    /// The load-bearing field. On resume, an identical boot id means the machine did
    /// <i>not</i> reboot — the agent merely restarted — and §2.4 forbids claiming anything from
    /// that. The reboot is requested again instead.
    /// </remarks>
    public required string BootId { get; init; }

    /// <summary>When it was written.</summary>
    public required DateTimeOffset WrittenUtc { get; init; }
}

/// <summary>What the loop remembers about one resource between passes and across boots.</summary>
/// <remarks>
/// Durable for one reason above all: an attempt counter that reset on every boot could never
/// exhaust a budget, and a budget that never exhausts is §2.4's unbounded reboot loop — "more
/// damaging than a stalled provision". The escalation ladder is only reachable because this
/// survives the very restart it is counting.
/// </remarks>
public sealed record ResourceLedgerEntry
{
    /// <summary>Which resource.</summary>
    public required string Resource { get; init; }

    /// <summary>Attempts since it was last in sync.</summary>
    public int Attempts { get; init; }

    /// <summary>How many times the budget has been exhausted (§2.5 rungs 2 and 4).</summary>
    public int Escalations { get; init; }

    /// <summary>
    /// Whether the most recent escalation actually reached the Fleet Manager.
    /// </summary>
    /// <remarks>
    /// The difference between <c>Degraded</c> and <c>Escalated(admin-notified)</c>. A frame whose
    /// server is unreachable has exhausted its budget and nobody has been told, which is
    /// <c>Degraded</c>; the same frame becomes <c>Escalated</c> the moment the buffered event
    /// drains up the link. Treating the two as one would let a frame claim an administrator had
    /// been notified while the notification sat in an offline buffer.
    /// </remarks>
    public bool EscalationNotified { get; init; }

    /// <summary>
    /// How many times in a row a value that had been observed correct came back wrong — §2.6's
    /// <b>conflict drift</b> (decision 78).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one thing the ladder could not previously remember.</b> Every other counter here is
    /// about a repair that never worked; this one is about a repair that <i>did</i> work and was
    /// then undone. Without it the two are indistinguishable, because the successful verify clears
    /// the ledger and the next pass starts from nothing — which is precisely how a frame reboots
    /// for ever while every individual pass reports success (measured: ~25 reboots in eleven
    /// minutes, attempt counter never past 1 of 3).
    /// </para>
    /// <para>
    /// <b>Consecutive, not cumulative.</b> A value that holds for
    /// <see cref="ReconcileOptions.ConflictHold"/> clears it, so an ordinary one-off repair — a
    /// package postinst rewriting a file, an operator correcting something by hand — never
    /// accumulates towards a give-up however many times it happens over a frame's life. What does
    /// accumulate is a value that will not stay put.
    /// </para>
    /// </remarks>
    public int Reversions { get; init; }

    /// <summary>
    /// The expected value the last in-sync observation was made against, or null when this
    /// resource is not currently holding one.
    /// </summary>
    /// <remarks>
    /// <b>The discriminator, and the reason this is a string rather than a flag.</b> §2.6 names two
    /// different things conflict drift: a change that keeps returning after correction, and a
    /// desired-value change pushed from the Fleet Manager. Only the first is a fight. An operator
    /// who lowers <c>audio.playbackVolume</c> three times while tuning it produces three
    /// drift-after-convergence events that are all entirely legitimate, and a counter that could
    /// not tell them apart would escalate their frame for using the product as designed. Comparing
    /// the expectation separates the two exactly: the value moved, or the goalposts did.
    /// </remarks>
    public string? HeldExpected { get; init; }

    /// <summary>When the current unbroken run of in-sync observations began.</summary>
    /// <remarks>
    /// Read only against <see cref="ReconcileOptions.ConflictHold"/>, to decide whether a value has
    /// held long enough to forgive the reversions before it. A timestamp rather than a pass count
    /// because passes are not a clock: after a reboot the loop runs the next pass immediately with
    /// no wait at all, so "in sync on two consecutive passes" can be two readings a millisecond
    /// apart, which a value being reverted by a session that has not started yet would satisfy.
    /// </remarks>
    public DateTimeOffset? HeldSinceUtc { get; init; }

    /// <summary>When the backoff expires and the next attempt is allowed.</summary>
    public DateTimeOffset? NextAttemptUtc { get; init; }

    /// <summary>The last expected-versus-observed delta.</summary>
    public string? Delta { get; init; }

    /// <summary>The last change made.</summary>
    public string? Change { get; init; }

    /// <summary>
    /// The plain-language gloss on <see cref="Change"/> — §2.7 item 3's second register, made
    /// durable for the same reason <see cref="Delta"/> is.
    /// </summary>
    /// <remarks>
    /// <b>Without this the plain half of the repair screen dies with the process that wrote it.</b>
    /// The gloss is composed by the resource during its Act and published as narration, and the
    /// pass that gives up is usually a <i>different</i> process — attempt 3 writes, the machine
    /// reboots, and the frame that comes back is the one that verifies, fails and stops. It then
    /// had the exact command and the expected-versus-observed delta, both durable, and none of the
    /// sentence explaining what that command was for.
    /// </remarks>
    public string? Gloss { get; init; }
}

/// <summary>Everything the loop persists under <c>/var/lib/fl-agent</c>.</summary>
public sealed record ReconcileJournalState
{
    private static readonly IReadOnlyList<ResourceLedgerEntry> NoEntries = [];
    private static readonly IReadOnlyList<DateTimeOffset> NoReboots = [];

    /// <summary>The in-flight apply, if the machine went down mid-contract.</summary>
    public PendingApply? Pending { get; init; }

    /// <summary>Per-resource attempt and escalation history.</summary>
    public IReadOnlyList<ResourceLedgerEntry> Ledger { get; init; } = NoEntries;

    /// <summary>
    /// When this frame last requested each of its recent reboots — the whole state behind
    /// <see cref="RebootFloor"/> (decision 79).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Device-level and deliberately not per resource.</b> Every other counter in this file is
    /// keyed by resource, which is exactly why none of them can bound a reboot cycle that no
    /// resource is failing: the ladder counts failures, and a livelock is made of successes. This
    /// list counts the thing that actually wears the card out, and it is the same list whichever
    /// resource asked.
    /// </para>
    /// <para>
    /// It has to be durable for the obvious reason — the process does not survive the event it is
    /// counting — and it is pruned to <see cref="ReconcileOptions.RebootFloorWindow"/> on every
    /// write, so it is bounded by the floor rather than by the frame's age.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DateTimeOffset> Reboots { get; init; } = NoReboots;

    /// <summary>The boot the loop last ran in, so a fresh boot can be announced (§4.1 events).</summary>
    public string? LastBootId { get; init; }

    /// <summary>
    /// When every resource was first verified at once — null while this frame has never been
    /// green.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole input to decision 51</b>, which scopes §2.7's pre-reboot countdown to drift
    /// repair and keeps it out of initial provisioning. It is a timestamp rather than a flag
    /// because "when did this frame first come up" is worth having in a post-mortem and costs
    /// nothing over a boolean; the decision only ever asks whether it is set.
    /// </para>
    /// <para>
    /// It lives here, beside the attempt ledger, for the same reason the ledger does: it has to
    /// survive the reboot every resource takes (§2.4) and the version change every update brings
    /// (§2.8). Inferred from anything transient — a field on the loop, the hub's current
    /// condition, the presence of a link — it would reset on every boot, and a frame in a living
    /// room would silently drop back to provisioning behaviour for the one repair a person was
    /// standing there to watch.
    /// </para>
    /// <para>
    /// Set once and never cleared. A frame that has been green and later drifts is still a frame
    /// that has been green; the countdown is owed to the viewer it acquired, not to its current
    /// health.
    /// </para>
    /// </remarks>
    public DateTimeOffset? FirstInSyncUtc { get; init; }

    /// <summary>Monotonic telemetry sequence, so a drained buffer can be ordered server-side.</summary>
    public long TelemetrySequence { get; init; }
}

/// <summary>
/// The progress journal of §2.1, read and written through <see cref="IStateStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// §2.1 lists "progress journal" among the persisted state that lives in
/// <c>/var/lib/fl-agent</c> and is "never touched by an update". Both halves matter here: the
/// journal has to outlive a reboot for §2.4, and it has to outlive a version change for §2.8,
/// or an agent that updates itself mid-provision forgets what it was proving.
/// </para>
/// <para>
/// <b>An unreadable journal is a fault, not an empty journal.</b> Those were the same thing here
/// once, and the difference is three reboots: an empty journal means every attempt counter is
/// zero, so a frame that could not read this file awarded itself a fresh budget and a fresh set
/// of reboots, silently, every time — which is the unbounded reboot loop §2.4 calls "more
/// damaging than a stalled provision", reached by the mechanism built to prevent it. The
/// forgiving reading was correct about one thing and wrong about the other: <i>absent</i> really
/// is an empty journal, because a frame that has never written one has nothing to forget.
/// <i>Present and unreadable</i> is a frame that has forgotten something.
/// </para>
/// <para>
/// <b>It still cannot cost the frame its ability to start</b>, which is the constraint that
/// shapes the whole of <see cref="Load"/>: nothing in it throws, whatever the file contains. A
/// state store that bricks a frame would be worse than the bug it was fixing. So a fault is
/// loud rather than fatal — it is logged at <see cref="IAgentLog.Fail"/>, the bytes are kept
/// beside the journal as <c>.unreadable</c> so a post-mortem can still see them, and the
/// <see cref="Unreadable"/> flag is what refuses the reboot. The frame boots, observes, repairs
/// what needs no reboot, draws its screen and reports.
/// </para>
/// </remarks>
public sealed class ReconcileJournal
{
    /// <summary>File name inside the state store.</summary>
    public const string FileName = "reconcile-journal.json";

    /// <summary>
    /// File the journal's bytes are kept under when they could not be read.
    /// </summary>
    /// <remarks>
    /// The evidence outlives the fault on purpose. The next <see cref="Update"/> overwrites the
    /// journal with a state this build can serialise, so without this the only trace of what the
    /// frame actually found would be one log line on a card whose logs may not have survived
    /// either. It is a fixed name rather than a timestamped one because the interesting copy is
    /// the latest, and an unbounded family of them on a full card is its own fault.
    /// </remarks>
    public const string UnreadableFileName = FileName + ".unreadable";

    private readonly IStateStore _store;
    private readonly IAgentLog _log;
    private readonly Lock _gate = new();

    private ReconcileJournalState _state = new();
    private bool _loaded;
    private bool _unreadable;

    /// <summary>Creates a journal over <paramref name="store"/>.</summary>
    public ReconcileJournal(IStateStore store, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _log = log;
    }

    /// <summary>Where the journal file lives.</summary>
    public string Path => _store.PathOf(FileName);

    /// <summary>Where the bytes of an unreadable journal are kept.</summary>
    public string UnreadablePath => _store.PathOf(UnreadableFileName);

    /// <summary>
    /// Whether the state being served came from a journal that was there and could not be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What every counter in this file being zero actually means.</b> A caller cannot tell an
    /// empty ledger that means "nothing has been attempted" from one that means "this frame has
    /// forgotten what it attempted", and the two call for opposite behaviour. This is that bit.
    /// </para>
    /// <para>
    /// <b>It stays set for the life of this object, including after a good journal is written.</b>
    /// Rewriting the file makes the <i>next</i> load clean; it does not recover the history this
    /// load lost, so this process still cannot count what came before it. Clearing the flag on the
    /// first successful write would restore exactly the fresh budget the flag exists to withhold —
    /// the loop writes the journal on every pass, so the fault would clear within a second and
    /// nothing would ever see it. A frame gets its reboots back when a process starts on a journal
    /// that parses, or when a person presses retry.
    /// </para>
    /// </remarks>
    public bool Unreadable
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _unreadable;
            }
        }
    }

    /// <summary>
    /// Forgets that the journal was unreadable — what a person pressing <b>retry</b> means for the
    /// fault.
    /// </summary>
    /// <remarks>
    /// <b>The escape hatch, and the reason the fault cannot strand a frame.</b>
    /// <see cref="Unreadable"/> deliberately does not clear itself, so without this a frame whose
    /// card hiccuped once would decline to reboot until somebody reached it over SSH — and the
    /// people who own these frames do not have SSH. A person pressing retry can see the frame and
    /// has read the reason on it, which is exactly the standing decision 67 already gives them for
    /// a fresh attempt budget.
    /// </remarks>
    public void Forgive()
    {
        lock (_gate)
        {
            _unreadable = false;
        }
    }

    /// <summary>Reads the journal, caching it for the life of this object.</summary>
    public ReconcileJournalState Read()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _state;
        }
    }

    /// <summary>Applies <paramref name="update"/> and writes the result to disk.</summary>
    public ReconcileJournalState Update(Func<ReconcileJournalState, ReconcileJournalState> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            EnsureLoaded();

            var next = update(_state);
            ArgumentNullException.ThrowIfNull(next);
            _state = next;
            Write(next);
            return next;
        }
    }

    /// <summary>Reads one resource's ledger entry, or a fresh one.</summary>
    public static ResourceLedgerEntry EntryFor(ReconcileJournalState state, string resource)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (var entry in state.Ledger)
        {
            if (string.Equals(entry.Resource, resource, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return new ResourceLedgerEntry { Resource = resource };
    }

    /// <summary>Returns a state with <paramref name="entry"/> replacing whatever was stored.</summary>
    public static ReconcileJournalState WithEntry(ReconcileJournalState state, ResourceLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(entry);

        var ledger = new List<ResourceLedgerEntry>(state.Ledger.Count + 1);
        var replaced = false;

        foreach (var existing in state.Ledger)
        {
            if (string.Equals(existing.Resource, entry.Resource, StringComparison.Ordinal))
            {
                ledger.Add(entry);
                replaced = true;
            }
            else
            {
                ledger.Add(existing);
            }
        }

        if (!replaced)
        {
            ledger.Add(entry);
        }

        return state with { Ledger = ledger };
    }

    /// <summary>
    /// Every list on the state non-null, whatever the file on disk did or did not contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is an upgrade seam, and it cost a frame twenty-nine silent minutes.</b> A property's
    /// initialiser only runs for a property the deserialiser does not touch <i>and</i> does not have
    /// to construct — an absent <c>reboots</c> key and an explicit <c>"reboots": null</c> both arrive
    /// here as a null list, not as the empty one the declaration appears to promise. So a frame
    /// carrying a journal written before <see cref="ReconcileJournalState.Reboots"/> existed read it
    /// back as null, and <see cref="RebootFloor.Within"/> threw <c>ArgumentNullException</c> on the
    /// first reboot request after the upgrade — inside the reconcile loop's task, where nothing was
    /// waiting to hear about it.
    /// </para>
    /// <para>
    /// <b>Self-perpetuating, which is why it could not heal.</b> <c>WhenWritingNull</c> means a null
    /// list is omitted again on every rewrite, so the missing key survived every subsequent write of
    /// the new build. Only a read that repairs the shape ends it.
    /// </para>
    /// <para>
    /// Applied to every list rather than to the one that broke: the next field added to this record
    /// would inherit the identical defect, and an empty list is the correct reading of "this build
    /// wrote nothing here" for all of them.
    /// </para>
    /// </remarks>
    public static ReconcileJournalState Normalise(ReconcileJournalState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state with
        {
            Ledger = state.Ledger ?? [],
            Reboots = state.Reboots ?? [],
        };
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _state = Load();
    }

    /// <summary>
    /// The journal on disk, or an empty one — with <see cref="_unreadable"/> set if the empty one
    /// is a guess rather than the truth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing in here throws.</b> Every branch ends in a state, because the alternative is a
    /// frame that will not start because a file will not parse, and that is worse than anything
    /// this method is defending against.
    /// </para>
    /// <para>
    /// <b>Absent and empty are not the same file, and telling them apart is the whole fix.</b>
    /// <see cref="IStateStore.ReadText"/> returns null for a file that is not there and
    /// <c>""</c> for a file that is there and holds nothing, and the old
    /// <c>IsNullOrWhiteSpace</c> collapsed the two into "start from nothing". That mattered more
    /// than the malformed-JSON case it was written for: the write this replaced was
    /// <c>File.WriteAllText</c>, which <i>truncates before it writes</i>, so the single most
    /// likely thing a power cut left behind was a zero-length journal — and a zero-length journal
    /// took the silent branch. No exception, no log line, every counter zero, three more reboots.
    /// </para>
    /// </remarks>
    private ReconcileJournalState Load()
    {
        string? text;
        try
        {
            text = _store.ReadText(FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not readable at all — a dying card, a mode somebody changed. There is nothing to
            // quarantine because there are no bytes, and it is still a fault rather than a fresh
            // frame. Previously this threw straight out of the reconcile loop's task.
            return Faulted($"it could not be opened ({exception.Message})", quarantine: null);
        }

        if (text is null)
        {
            // Genuinely absent: this frame has never written one, so there is nothing it could
            // have forgotten. The only branch that is allowed to be an empty journal.
            return new ReconcileJournalState();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Faulted("it is there and holds nothing", quarantine: text);
        }

        ReconcileJournalState? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(text, AgentJson.Default.ReconcileJournalState);
        }
        catch (JsonException exception)
        {
            return Faulted($"it is not valid JSON ({exception.Message})", quarantine: text);
        }

        if (parsed is null)
        {
            // Well-formed JSON `null`. Nothing this build writes can produce it, so it is a file
            // that has been damaged or edited rather than a journal, and reading it as an empty
            // one would be the same silent reset by a narrower door.
            return Faulted("it parsed as nothing at all", quarantine: text);
        }

        return Normalise(parsed);
    }

    private ReconcileJournalState Faulted(string because, string? quarantine)
    {
        _unreadable = true;

        var kept = quarantine is not null && Quarantine(quarantine);

        _log.Fail(
            $"The reconcile journal at {Path} could not be read: {because}. This frame has lost its "
            + "record of what it has already attempted and how recently it has rebooted, so it will "
            + "not reboot again until that record can be trusted"
            + (kept ? $" — the bytes it found are kept at {UnreadablePath}." : "."));

        return new ReconcileJournalState();
    }

    private bool Quarantine(string text)
    {
        try
        {
            _store.WriteText(UnreadableFileName, text);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keeping the evidence is worth a try and never worth a throw. The fault is already
            // being reported; losing the copy makes the post-mortem harder, not the frame worse.
            _log.Warn($"The unreadable journal could not be kept at {UnreadablePath}: {exception.Message}");
            return false;
        }
    }

    private void Write(ReconcileJournalState state)
    {
        try
        {
            _store.WriteText(FileName, JsonSerializer.Serialize(state, AgentJson.Default.ReconcileJournalState));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A journal that cannot be written is serious — the attempt budget stops being
            // durable and §2.4's protection against an unbounded reboot loop weakens to
            // whatever this process remembers. It is still not a reason to stop reconciling,
            // so it is said loudly and the pass continues.
            _log.Fail($"Could not write the reconcile journal at {Path}: {exception.Message}");
        }
    }
}
