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

    /// <summary>Set once this resource has stopped the whole device (§2.5 rung 4).</summary>
    public bool Halted { get; init; }

    /// <summary>When the backoff expires and the next attempt is allowed.</summary>
    public DateTimeOffset? NextAttemptUtc { get; init; }

    /// <summary>The last expected-versus-observed delta.</summary>
    public string? Delta { get; init; }

    /// <summary>The last change made.</summary>
    public string? Change { get; init; }
}

/// <summary>Everything the loop persists under <c>/var/lib/fl-agent</c>.</summary>
public sealed record ReconcileJournalState
{
    private static readonly IReadOnlyList<ResourceLedgerEntry> NoEntries = [];

    /// <summary>The in-flight apply, if the machine went down mid-contract.</summary>
    public PendingApply? Pending { get; init; }

    /// <summary>Per-resource attempt and escalation history.</summary>
    public IReadOnlyList<ResourceLedgerEntry> Ledger { get; init; } = NoEntries;

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

    private ReconcileJournalState Load()
    {
        var text = _store.ReadText(FileName);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ReconcileJournalState();
        }

        try
        {
            return JsonSerializer.Deserialize(text, AgentJson.Default.ReconcileJournalState)
                ?? new ReconcileJournalState();
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
