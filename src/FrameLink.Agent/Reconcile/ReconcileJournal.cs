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
/// A corrupt or unreadable journal is treated as an empty one. That is deliberately the
/// forgiving choice: an empty journal costs one extra reconcile pass, because every resource is
/// simply observed again and the level-triggered loop finds the same drift it found before. A
/// journal that threw would cost the frame its ability to start.
/// </para>
/// </remarks>
public sealed class ReconcileJournal
{
    /// <summary>File name inside the state store.</summary>
    public const string FileName = "reconcile-journal.json";

    private readonly IStateStore _store;
    private readonly IAgentLog _log;
    private readonly Lock _gate = new();

    private ReconcileJournalState _state = new();
    private bool _loaded;

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

    /// <summary>Reads the journal, caching it for the life of this object.</summary>
    public ReconcileJournalState Read()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return _state;
            }

            _loaded = true;
            _state = Load();
            return _state;
        }
    }

    /// <summary>Applies <paramref name="update"/> and writes the result to disk.</summary>
    public ReconcileJournalState Update(Func<ReconcileJournalState, ReconcileJournalState> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                _state = Load();
            }

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

    private ReconcileJournalState Load()
    {
        var text = _store.ReadText(FileName);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ReconcileJournalState();
        }

        try
        {
            return Normalise(
                JsonSerializer.Deserialize(text, AgentJson.Default.ReconcileJournalState)
                    ?? new ReconcileJournalState());
        }
        catch (JsonException exception)
        {
            _log.Warn($"The reconcile journal at {Path} could not be read ({exception.Message}); starting from nothing.");
            return new ReconcileJournalState();
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
