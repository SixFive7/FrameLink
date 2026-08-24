namespace FrameLink.Agent.Hosting;

/// <summary>
/// A command one of the loops beside the reconcile pass depends on was stopped for taking too long.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the loops have no ledger of their own, and it deliberately reuses the one
/// they already have.</b> Seven of the fifteen loops run external commands, and six of them are not
/// the reconcile pass: §2.10's supervisor, §2.7's browser stage, the screen handover, the package
/// inventory, the array firmware reporter and the array flash. Each of those has an attempt-free
/// tick — it tries, it fails, it tries again in a few seconds — which is exactly right for a
/// command that answered "no" and exactly wrong for one that will never answer at all. Retried for
/// ever, a wedged <c>systemctl</c> would leave §2.7's browser stage in the state that section
/// forbids with nothing anywhere counting the failures.
/// </para>
/// <para>
/// <b>What it does not do is invent a second failure model.</b> A loop that ends while the agent is
/// still running is already a failure recorded as <c>agent.loop.&lt;name&gt;</c> against §2.5's same
/// budget of three: attempts one and two stand the agent down so systemd brings it straight back,
/// and the third holds the screen with the surviving loops still painting it. So the whole of this
/// mechanism is that the timeout leaves the loop, and <c>AgentHost</c>'s existing supervision does
/// the rest. Its message becomes the <i>observed</i> half of the delta on the frame's own screen,
/// which is why the message carries the command, the deadline and whatever output arrived first
/// rather than a type name.
/// </para>
/// <para>
/// <b>The reconcile pass must never see this thrown</b>, and it does not: nothing on the resource
/// path calls <see cref="ThrowIfTimedOut"/>. A resource whose command timed out reads
/// <see cref="ProcessResult.TimedOut"/> as ordinary failure data, reports the drift it genuinely
/// found, and spends one of its own three attempts — the operator's decision, unchanged, and a
/// resource that threw here would instead take the whole pass down with it.
/// </para>
/// </remarks>
public sealed class ProcessTimeoutException : Exception
{
    /// <summary>Creates the failure from <paramref name="result"/>.</summary>
    public ProcessTimeoutException(ProcessResult result)
        : base(Describe(result))
    {
        Deadline = result.Deadline ?? TimeSpan.Zero;
        Output = result.StandardOutput;
    }

    /// <summary>Creates the failure with <paramref name="message"/>.</summary>
    public ProcessTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the failure with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    public ProcessTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the failure with nothing said.</summary>
    public ProcessTimeoutException()
    {
    }

    /// <summary>The deadline that was exceeded.</summary>
    public TimeSpan Deadline { get; }

    /// <summary>Whatever the command had written before it was stopped.</summary>
    public string Output { get; } = string.Empty;

    /// <summary>
    /// Nothing, unless <paramref name="result"/> is a timeout — in which case it leaves the loop.
    /// </summary>
    /// <remarks>
    /// Called at the process call sites in the six loops beside the reconcile pass, and nowhere on
    /// the resource path. It returns the result so it can be used in place of the call it wraps.
    /// </remarks>
    public static ProcessResult ThrowIfTimedOut(ProcessResult result) =>
        result.TimedOut ? throw new ProcessTimeoutException(result) : result;

    /// <summary>The whole of what the loop's supervisor will put on the screen.</summary>
    /// <remarks>
    /// <see cref="ProcessResult.Combined"/> already holds the explanation the runner wrote plus
    /// whatever the command managed to say, and newlines are collapsed because this lands in a
    /// one-line delta.
    /// </remarks>
    private static string Describe(ProcessResult result) =>
        result.Combined.Replace('\n', ' ').Replace('\r', ' ').Trim();
}
