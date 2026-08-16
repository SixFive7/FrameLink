using System.Globalization;

namespace FrameLink.Protocol;

/// <summary>
/// The §2.3 resource status vocabulary, as the agent writes it into its self-report.
/// </summary>
/// <remarks>
/// <see cref="HandshakeHello.AgentStatus"/> is frozen as <i>free text</i> and stays free text —
/// it is what a hopelessly broken agent uses to say something nobody anticipated, and
/// constraining it would take that away. What these constants add is a convention for the part
/// that <i>can</i> be structured: the leading word. An agent that has something to report
/// writes <c>Degraded(audio.volume, expected 75 observed 40, attempt 3)</c>, and the head of
/// that string is a vocabulary term rather than a sentence somebody happened to phrase that way.
/// </remarks>
public static class AgentResourceStatus
{
    /// <summary>Everything verified.</summary>
    public const string InSync = "InSync";

    /// <summary>A resource is being worked on.</summary>
    public const string Progressing = "Progressing";

    /// <summary>Applied, waiting for the reboot that proves it stuck (§2.4).</summary>
    public const string AwaitingReboot = "AwaitingReboot";

    /// <summary>Attempt budget exhausted; carries the expected-vs-observed delta (§2.5).</summary>
    public const string Degraded = "Degraded";

    /// <summary>Not attempted, because something it depends on is not in sync (§2.2).</summary>
    /// <remarks>
    /// Not "a <i>failed</i> dependency", which is what this said and is narrower than the loop
    /// that writes it: <c>ReconcileLoop.Blocker</c> returns any dependency that is not
    /// <see cref="InSync"/> — including one that is merely still being applied, and including the
    /// whole catalog on a bare frame whose adoption has not landed yet. It is also what an
    /// unevaluable observation lands on, where the frame could not ask the Fleet Manager at all.
    /// None of those is a failure, which is why <see cref="AgentHealth.Classify"/> does not read
    /// it as one.
    /// </remarks>
    public const string Blocked = "Blocked";

    /// <summary>
    /// The operator has been notified (§2.5 rung 3). <b>The last one; nothing sits below it.</b>
    /// </summary>
    public const string Escalated = "Escalated";
}

/// <summary>
/// The coarse health of an agent, derived once from its free-text self-report.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that the presence ladder of §3.5 is a <b>fact the server states</b> rather
/// than a guess each consumer makes. The GUI used to classify a device by string-matching
/// <see cref="HandshakeHello.AgentStatus"/> against the §2.3 vocabulary, which made a browser a
/// second consumer of a field the protocol documents as free text and the agent may reword at
/// will — and got it wrong immediately, because a real agent's self-report reads
/// <c>linux-arm64, endpoints resolved by boot file</c> and matched nothing, so every healthy
/// frame in the fleet would have rendered as <i>Online — degraded</i>.
/// </para>
/// <para>
/// The classification lives here rather than in the Fleet Manager because it is a property of
/// the vocabulary, and the vocabulary belongs to both programs: the agent writes the strings
/// with <see cref="AgentResourceStatus"/> and <see cref="Describe"/>, the server reads them
/// with <see cref="Classify"/>, and a term added to one side cannot be missed by the other.
/// </para>
/// <para><b>Frozen once shipped</b>, like everything else on the wire: values are added, never
/// renamed. An unrecognised value is <see cref="Unknown"/>, never an error.</para>
/// </remarks>
public static class AgentHealth
{
    /// <summary>The agent said nothing, or something outside the vocabulary.</summary>
    /// <remarks>
    /// Deliberately <i>not</i> a problem. An agent is under no obligation to speak the
    /// vocabulary, and treating silence as trouble is how a whole fleet ends up amber.
    /// </remarks>
    public const string Unknown = "unknown";

    /// <summary>The agent reports everything verified.</summary>
    public const string InSync = "in-sync";

    /// <summary>
    /// Busy, not broken: progressing, waiting for a verifying reboot, or waiting on something else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="AgentResourceStatus.Blocked"/> belongs here and not under
    /// <see cref="Degraded"/>, and the specification says so three times.</b> §2.3 lists it as its
    /// own term beside <c>Degraded(reason, delta, attempts)</c> rather than as a flavour of it.
    /// §2.5's ladder — budget exhausted, notify, give up — never passes through it, and a blocked
    /// resource spends no attempt against that budget. And §2.2 introduces it for the express
    /// purpose of stopping a dependent from reading as a failure: the loop "marks dependents
    /// <c>Blocked(dependency)</c> rather than letting them fail confusingly on their own".
    /// Classifying it as a fault undoes the one thing it was added to do.
    /// </para>
    /// <para>
    /// It is also the common case rather than an edge one. §2.4 reboots for every resource, so
    /// for most of a first provision most of the catalog is waiting on something upstream — the
    /// first full provision reported twelve blocked behind one escalation, and a bare frame that
    /// has not been adopted yet has the entire catalog blocked behind
    /// <c>agent.adoption</c>. Amber on all of that is the same false alarm this type was written
    /// to remove, arriving from the server instead of from the browser.
    /// </para>
    /// <para>
    /// The coarse vocabulary cannot see which kind of waiting it is, because
    /// <see cref="Classify"/> reads only the head and the dependency is in the parenthesis. A
    /// resource blocked on <c>the Fleet Manager</c> — the frame could not ask, so it concluded
    /// nothing — is therefore also <c>working</c> here. That is the safe direction: the fine
    /// distinction is drawn on the reconciliation screen, which has the whole report, and the one
    /// thing this must not do is report a defect on a frame that behaved correctly while a server
    /// was quiet.
    /// </para>
    /// </remarks>
    public const string Working = "working";

    /// <summary>Something is wrong and named: degraded or escalated.</summary>
    public const string Degraded = "degraded";



    /// <summary>Composes a self-report: a vocabulary head, then free-text detail.</summary>
    /// <param name="status">One of the <see cref="AgentResourceStatus"/> terms.</param>
    /// <param name="detail">Anything the agent wants to say, in its own words.</param>
    /// <remarks>
    /// The shape is <c>Head(detail)</c> because that is what §2.5 already specifies for
    /// <c>Degraded(reason, delta, attempts)</c>, and having one shape rather than two means
    /// <see cref="Classify"/> has one thing to understand.
    /// </remarks>
    public static string Describe(string status, string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? status
            : string.Create(CultureInfo.InvariantCulture, $"{status}({detail})");

    /// <summary>
    /// Composes the self-report of a loop that is in <paramref name="loopState"/>.
    /// </summary>
    /// <param name="loopState">
    /// One of <see cref="LoopStateNames"/> — what the reconciliation loop published with its last
    /// census — or <see langword="null"/> before there has been one.
    /// </param>
    /// <param name="detail">Anything the agent wants to say, in its own words.</param>
    /// <returns>
    /// The string to write into <see cref="HandshakeHello.AgentStatus"/>, or
    /// <see langword="null"/> when the agent has nothing at all to say.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The loop is the only thing that knows, so this is the only place the two vocabularies
    /// meet.</b> §2.2's loop publishes a <c>loopState</c> with every census and the Fleet Manager
    /// classifies a §2.3 term, and until this existed nothing joined them: the agent composed one
    /// <c>Progressing(…)</c> string when the process started and never recomputed it, so a frame
    /// converged at 81 of 81 reported itself part-way through applying something for as long as it
    /// stayed up. Every device in the fleet list read <i>Online — working</i>, always, and in the
    /// direction that hides trouble.
    /// </para>
    /// <para>
    /// <b><see cref="LoopStateNames.BackingOff"/> is <see cref="AgentResourceStatus.Progressing"/>
    /// and not <see cref="AgentResourceStatus.Degraded"/>.</b> A resource waiting out §2.4's delay
    /// has attempts left and is going to take them; §2.5 reaches <c>Degraded</c> only when the
    /// budget is exhausted, at which point the loop is <see cref="LoopStateNames.Escalated"/>.
    /// Reporting a fault during an ordinary retry is the false alarm <see cref="Working"/>'s
    /// remarks are about, arriving one layer earlier.
    /// </para>
    /// <para>
    /// <b>A loop state this build does not recognise — including none at all — reports the detail
    /// alone</b>, with no vocabulary head, which <see cref="Classify"/> reads as
    /// <see cref="Unknown"/>. That is the honest answer for the seconds between a process starting
    /// and its first pass finishing, and for a frame whose loop has stopped saying anything: it
    /// has not observed itself, so it says what it does know — what it is running, and how it
    /// found its Fleet Manager — and claims nothing about its own convergence. §2.6's rule runs in
    /// both directions, and <c>InSync</c> before the first observation would break it in the
    /// direction that hides trouble.
    /// </para>
    /// </remarks>
    public static string? ReportFor(string? loopState, string? detail) =>
        HeadFor(loopState) is { } head ? Describe(head, detail) : detail;

    /// <summary>The §2.3 term a loop in <paramref name="loopState"/> is reporting, or null.</summary>
    private static string? HeadFor(string? loopState) => loopState switch
    {
        LoopStateNames.Converged => AgentResourceStatus.InSync,
        LoopStateNames.Reconciling => AgentResourceStatus.Progressing,
        LoopStateNames.AwaitingReboot => AgentResourceStatus.AwaitingReboot,
        LoopStateNames.BackingOff => AgentResourceStatus.Progressing,
        LoopStateNames.Escalated => AgentResourceStatus.Escalated,
        _ => null,
    };

    /// <summary>Reads the coarse health out of a free-text self-report.</summary>
    /// <param name="agentStatus">
    /// <see cref="HandshakeHello.AgentStatus"/> verbatim, or <see langword="null"/>.
    /// </param>
    /// <returns>One of the constants on this class. Never null, never throws.</returns>
    public static string Classify(string? agentStatus)
    {
        if (string.IsNullOrWhiteSpace(agentStatus))
        {
            return Unknown;
        }

        var span = agentStatus.AsSpan().Trim();
        var open = span.IndexOf('(');
        var head = (open < 0 ? span : span[..open]).Trim();

        // Ordinal-ignore-case rather than exact: the vocabulary is a contract about words, and
        // refusing to recognise `degraded` because the agent wrote it lowercase would be the
        // brittleness this whole type exists to remove.
        return
            Is(head, AgentResourceStatus.InSync) ? InSync
            : Is(head, AgentResourceStatus.Progressing)
                || Is(head, AgentResourceStatus.AwaitingReboot)
                || Is(head, AgentResourceStatus.Blocked) ? Working
            : Is(head, AgentResourceStatus.Degraded)
                || Is(head, AgentResourceStatus.Escalated) ? Degraded
            : Unknown;

        static bool Is(ReadOnlySpan<char> head, string term) =>
            head.Equals(term, StringComparison.OrdinalIgnoreCase);
    }
}
