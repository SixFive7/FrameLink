using System.Text;
using System.Text.Json;
using FrameLink.Agent.Hosting;
using FrameLink.Protocol;

namespace FrameLink.Agent.State;

/// <summary>
/// The <b>last-known desired values</b> of §2.1, as they sit on disk.
/// </summary>
/// <remarks>
/// <para>
/// Every field here answers the same question — <i>what was the last thing an authority actually
/// told this frame?</i> — and every one of them was previously a field on <c>AgentHost</c> that a
/// reboot erased. The absence of a field and an empty value are kept distinct throughout, because
/// "the Fleet Manager has never said" and "the Fleet Manager said nothing is set" are different
/// answers and the agent acts differently on each (§2.6).
/// </para>
/// <para>
/// <b>Nothing here expires.</b> See <see cref="AgentMemory"/> for why.
/// </para>
/// </remarks>
public sealed record AgentMemoryState
{
    /// <summary>
    /// The <see cref="HandshakeStatus"/> the Fleet Manager last authoritatively answered with, or
    /// null if it never has.
    /// </summary>
    public string? Verdict { get; init; }

    /// <summary>When that answer arrived, for a post-mortem. Nothing decides anything on it.</summary>
    public DateTimeOffset? VerdictUtc { get; init; }

    /// <summary>
    /// The display name the Fleet Manager assigned at adoption, or null if it never has.
    /// </summary>
    /// <remarks>
    /// Null and empty are different answers — see <see cref="Reconcile.DeviceNameResource"/>,
    /// whose whole reason for distinguishing them is that collapsing the two once erased a name
    /// an operator had set and then reported the resource green for agreeing with itself.
    /// </remarks>
    public string? DeviceName { get; init; }

    /// <summary>The revision of <see cref="Settings"/>, as the Fleet Manager numbered it (§3.4).</summary>
    public long SettingsRevision { get; init; }

    /// <summary>When those settings arrived.</summary>
    public DateTimeOffset? SettingsUtc { get; init; }

    /// <summary>
    /// The effective settings the Fleet Manager last pushed — fleet defaults with this device's
    /// overrides already applied, server-side (§3.4).
    /// </summary>
    /// <remarks>
    /// Replaced wholesale by each push rather than merged. A push is the complete effective set,
    /// so merging would make a setting the operator <i>deleted</i> immortal on the frame.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Settings { get; init; }
}

/// <summary>
/// <b>The agent's durable memory</b> — §2.1's "last-known desired values", beside the device
/// keypair and the progress journal under <c>/var/lib/fl-agent</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> Two failures found on the mule share one cause: the agent held what it
/// had been told in process memory and lost it at every reboot.
/// </para>
/// <list type="number">
/// <item><description>
/// A power cut during a server outage blanked a green frame. <see cref="AgentStatus.LastAuthoritative"/>
/// was process-local, so <see cref="DeviceStateLadder.NoContact"/> computed <i>was not green</i>
/// on every boot and the frame showed a repair screen instead of photos — the exact outcome §2.6
/// forbids: "an outage in the operator's house must never blank a frame in someone else's".
/// </description></item>
/// <item><description>
/// Fleet settings reverted to catalog defaults during an outage, because
/// <see cref="Resources.FleetValues"/> could not tell "the operator never set this" from "we have
/// not been told yet". Each reversion costs a real apply-and-reboot to go there and another to
/// come back when the server returns — and
/// <see cref="Resources.JournalStorageResource"/> has no <c>dependsOn</c>, so it runs during
/// <i>any</i> outage.
/// </description></item>
/// </list>
/// <para>
/// <b>§1.2.2's catalog defaults stay.</b> Memory does not replace them: a key that has never been
/// pushed is still absent, and <see cref="Resources.FleetValues.Get(string, string)"/> still falls
/// back to the built-in value, so a never-configured fleet still produces a fully specified frame.
/// What changes is that a <i>known</i> desired value is remembered instead of forgotten and
/// re-derived.
/// </para>
/// <para>
/// <b>Nothing here expires, and that is a decision rather than an omission.</b> A frame offline
/// for a month runs on month-old desired values, and the alternative to a stale value is not a
/// fresher one — it is the catalog default, which is <i>older</i> and which the operator
/// explicitly did not choose. §2.6's entire argument is that a frame keeps doing the last thing it
/// was legitimately told; a timeout would blank a frame at 3am for a reason no viewer could
/// understand, and it would do so on evidence nobody had asked for. The scenario a timeout is
/// imagined to protect — a frame un-adopted while it was offline — is already covered better by
/// the rule below: the moment the server is reachable it says so, and until then the frame cannot
/// fetch new photos anyway. A timeout would also have to be evaluated against a clock that, after
/// the very power cut this file exists for, has not yet been corrected by NTP — which needs the
/// network that is down. So the age is recorded (<see cref="AgentMemoryState.VerdictUtc"/>,
/// <see cref="AgentMemoryState.SettingsUtc"/>) and nothing acts on it.
/// </para>
/// <para>
/// <b>An authoritative answer always wins.</b> A live verdict replaces what was remembered in
/// both directions, including telling a frame that was green it is no longer adopted. Memory is
/// only ever consulted for the interval before the first answer of a process arrives.
/// </para>
/// <para>
/// <b>Secret-bearing by construction (§2.9).</b> The settings map is whatever the Fleet Manager
/// decided to send — today an Immich album id and a journal cap, tomorrow an Immich API key or a
/// LiveKit token — so the file is written <c>0600</c> through
/// <see cref="IStateStore.WriteSecretAtomic"/> and no value is ever logged. Keys and counts are
/// logged; values are not.
/// </para>
/// <para>
/// <b>Corruption degrades to ignorance, never to a crash.</b> A truncated or unparseable file is
/// read as an empty one: catalog defaults and not-green, which is precisely where an agent with no
/// memory at all stands. A power cut mid-write is the scenario this whole feature exists for, so
/// the write itself is atomic and the read is forgiving — losing the file costs one outage's worth
/// of remembering, while throwing on it would cost the frame its ability to start.
/// </para>
/// </remarks>
public sealed class AgentMemory
{
    /// <summary>File name inside the state store.</summary>
    public const string FileName = "agent-memory.json";

    private static readonly IReadOnlyDictionary<string, string> NoSettings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly IStateStore _store;
    private readonly IAgentLog _log;
    private readonly IAgentClock _clock;
    private readonly Lock _gate = new();

    private AgentMemoryState _state = new();
    private bool _loaded;

    /// <summary>Creates the memory over <paramref name="store"/>.</summary>
    public AgentMemory(IStateStore store, IAgentLog log, IAgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _log = log;
        _clock = clock;
    }

    /// <summary>Where the file lives.</summary>
    public string Path => _store.PathOf(FileName);

    /// <summary>Everything remembered, read once and cached for the life of this object.</summary>
    public AgentMemoryState Read()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                _state = Load();
            }

            return _state;
        }
    }

    /// <summary>The settings to start from — empty when nothing has ever been pushed.</summary>
    public IReadOnlyDictionary<string, string> Settings => Read().Settings ?? NoSettings;

    /// <summary>The name to start from, or null if the Fleet Manager has never said one.</summary>
    public string? DeviceName => Read().DeviceName;

    /// <summary>
    /// The condition a restarted agent may carry over — §2.6's <i>"provided the frame was fully
    /// green when contact dropped"</i>, decided from disk rather than from a memory this process
    /// does not have.
    /// </summary>
    /// <param name="hasEverBeenInSync">
    /// Whether the reconciler has ever had every resource verified at once —
    /// <see cref="Reconcile.ReconcileJournalState.FirstInSyncUtc"/> being set.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Two durable facts, and both are required.</b> The Fleet Manager's last authoritative
    /// answer must have been <see cref="HandshakeStatus.Ok"/>, <i>and</i> this frame must have
    /// reached a converged pass. Neither alone is "fully green": the first is the server's half of
    /// the sentence — adopted, version-matched, not blocked — and the second is the frame's half,
    /// every resource observed to be what it should be. A frame in the middle of its first
    /// provision has the first and not the second, and §2.6 gives it a repair screen.
    /// </para>
    /// <para>
    /// <b>Why this cannot be claimed by a frame that was never green.</b> The <c>ok</c> is written
    /// only from a completed handshake, which requires a signature over a server nonce made with
    /// the device's private key — a frame that was never adopted is answered <c>pending</c>,
    /// <c>blocked</c> or <c>bad-signature</c> and can therefore never write <c>ok</c> into this
    /// file. The convergence half is written only by <see cref="Reconcile.ReconcileLoop"/> on a
    /// pass that observed every resource in sync. Both files are <c>0600</c> in a <c>0700</c>
    /// directory owned by root, so forging either means already being root on the frame. And the
    /// value on disk carries no authority of its own: what is returned here is a compiled-in
    /// constant, not a <c>ProductRuns</c> flag read out of the file, so the strongest thing an
    /// edited file can do is name a different handshake status.
    /// </para>
    /// <para>
    /// <b>Memory can only ever grant this, never withhold it.</b> Null is what every process
    /// started with before this existed, so the failure mode of an absent, unreadable or
    /// unconvincing memory is exactly today's behaviour.
    /// </para>
    /// </remarks>
    public DeviceCondition? ResumeCondition(bool hasEverBeenInSync)
    {
        var state = Read();

        if (!string.Equals(state.Verdict, HandshakeStatus.Ok, StringComparison.Ordinal))
        {
            return null;
        }

        if (!hasEverBeenInSync)
        {
            // Adopted, and never finished provisioning. Coming back green would show photos from
            // a frame that has never once had every setting verified.
            _log.Info(
                "The Fleet Manager last said this frame was adopted, but it has never had every setting verified at once, so it comes back on its repair screen.");

            return null;
        }

        _log.Info("This frame was green when it last had contact, so it carries on showing the product while it looks for its Fleet Manager (§2.6).");
        return DeviceStateLadder.Remembered;
    }

    /// <summary>Records what the Fleet Manager authoritatively answered (§2.6).</summary>
    /// <remarks>
    /// Ignores <see cref="DeviceStateLadder.RememberedCause"/> — that condition is this file being
    /// read back, not a new answer, and recording it would let a frame keep re-confirming its own
    /// memory long after the server had stopped agreeing with it. Ignores an unchanged verdict
    /// too, so a healthy frame writes here once per adoption rather than once per publish.
    /// </remarks>
    public void RememberAnswer(DeviceCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        if (!condition.IsAuthoritative
            || string.Equals(condition.Cause, DeviceStateLadder.RememberedCause, StringComparison.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            var current = Current();
            if (string.Equals(current.Verdict, condition.Cause, StringComparison.Ordinal))
            {
                return;
            }

            Save(current with
            {
                Verdict = condition.Cause,
                VerdictUtc = _clock.UtcNow,
            });
        }

        _log.Info($"Remembering that the Fleet Manager answered '{condition.Cause}'.");
    }

    /// <summary>Records the display name the Fleet Manager assigned (§3.4).</summary>
    public void RememberDeviceName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (_gate)
        {
            var current = Current();
            if (string.Equals(current.DeviceName, name, StringComparison.Ordinal))
            {
                return;
            }

            Save(current with { DeviceName = name });
        }
    }

    /// <summary>Records the effective settings the Fleet Manager pushed (§3.4).</summary>
    /// <remarks>
    /// The values themselves never reach the log (§2.9). A settings map is the one place on this
    /// frame where an Immich API key or a LiveKit token would arrive, and a log line is the second
    /// honesty surface, not a place for a credential to be copied to.
    /// </remarks>
    public void RememberSettings(SettingsPush push)
    {
        ArgumentNullException.ThrowIfNull(push);

        lock (_gate)
        {
            Save(Current() with
            {
                Settings = push.Values,
                SettingsRevision = push.Revision,
                SettingsUtc = _clock.UtcNow,
            });
        }

        _log.Info($"Remembering {push.Values.Count} setting(s) from the Fleet Manager, revision {push.Revision}.");
    }

    /// <summary>Reads the current state, loading it first if this is the first touch.</summary>
    private AgentMemoryState Current()
    {
        if (!_loaded)
        {
            _loaded = true;
            _state = Load();
        }

        return _state;
    }

    private AgentMemoryState Load()
    {
        string? text;

        try
        {
            text = _store.ReadText(FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"The agent's memory at {Path} could not be read ({exception.Message}); starting from nothing.");
            return new AgentMemoryState();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new AgentMemoryState();
        }

        try
        {
            return JsonSerializer.Deserialize(text, AgentJson.Default.AgentMemoryState) ?? new AgentMemoryState();
        }
        catch (JsonException exception)
        {
            // Truncated, half-written, or written by a build that shaped this differently. All
            // three mean the same thing to a frame: it knows nothing, so it uses catalog defaults
            // and comes back not-green. Losing the memory costs an outage's worth of remembering;
            // throwing here would cost the frame its ability to start at all.
            _log.Warn($"The agent's memory at {Path} could not be read ({exception.Message}); starting from nothing.");
            return new AgentMemoryState();
        }
    }

    private void Save(AgentMemoryState state)
    {
        _state = state;

        try
        {
            _store.WriteSecretAtomic(
                FileName,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, AgentJson.Default.AgentMemoryState)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The in-process copy above is already updated, so this run behaves correctly and only
            // the next boot loses what could not be written. Said loudly, because a frame whose
            // memory silently stopped persisting is a frame that will blank itself at the next
            // power cut for a reason nobody can see.
            _log.Fail($"Could not write the agent's memory at {Path}: {exception.Message}");
        }
    }
}
