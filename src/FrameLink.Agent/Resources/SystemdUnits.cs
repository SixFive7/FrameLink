using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Resources;

/// <summary>
/// The two questions every resource that owns a systemd unit asks, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Three resources now read <c>systemctl is-enabled</c> against the <i>system</i> manager and have
/// to tell a mask apart from a plain "no" — the apt timers, the journal daemon and the agent's own
/// unit. The mask spellings in particular must have exactly one definition: reading only
/// <c>masked</c> and not <c>masked-runtime</c> is the bug that sends an agent into an
/// enable-fails-every-time loop, and a predicate copied into three files is three chances to
/// reintroduce it in two of them.
/// </para>
/// <para>
/// The <c>systemctl --user</c> readers are deliberately <b>not</b> folded in here. They ask a
/// different manager through a different seam (<see cref="IUserSession"/>), and their answers
/// arrive as a <see cref="ProcessResult"/> with standard output and standard error apart rather
/// than as <see cref="SystemControlResult.Output"/> with the two already joined — so sharing would
/// mean a parameter that exists only to say which of two worlds the caller is in.
/// </para>
/// </remarks>
public static class SystemdUnits
{
    /// <summary>The enablement word that means the unit comes back after a reboot.</summary>
    public const string EnabledState = "enabled";

    /// <summary>
    /// The enablement word that most resembles success and is never accepted as it.
    /// </summary>
    /// <remarks>
    /// systemd's word for an enablement written under <c>/run</c>, which is a tmpfs. The unit reads
    /// as enabled to anything asking for a boolean and the want is gone at the next boot, so every
    /// caller here compares against <see cref="EnabledState"/> exactly rather than asking "is it
    /// enabled in some sense".
    /// </remarks>
    public const string RuntimeEnabledState = "enabled-runtime";

    /// <summary>What a <c>systemctl</c> that said nothing at all is reported as.</summary>
    public const string NoAnswer = "no answer from systemctl";

    /// <summary>
    /// One <c>Property=value</c> line out of a <c>systemctl show</c> answer, or null.
    /// </summary>
    /// <remarks>
    /// Null for an absent property and for a present-but-empty one alike, which is what systemd
    /// prints for a unit it has no answer for — <c>FragmentPath=</c> on a unit that does not exist.
    /// Collapsing the two is right here: neither is a value, and a caller that needs to tell
    /// "no answer at all" from "answered nothing" reads several properties and finds them all null.
    /// </remarks>
    public static string? PropertyIn(string shown, string name)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var prefix = name + "=";

        foreach (var line in shown.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim() is { Length: > 0 } value ? value : null;
            }
        }

        return null;
    }

    /// <summary>Whether an enablement word is one of systemd's two masked spellings.</summary>
    /// <remarks>
    /// Both spellings, because <c>masked-runtime</c> is a mask written under <c>/run</c> and
    /// behaves identically for the only question asked of it here: <c>systemctl enable</c> refuses
    /// against it. Reading only the first spelling would send the agent into the
    /// enable-fails-three-times loop for exactly the masks that are temporary.
    /// </remarks>
    public static bool IsMasked(string enablement) => enablement is "masked" or "masked-runtime";

    /// <summary>
    /// One <c>systemctl</c> question, reduced to the line that answers it.
    /// </summary>
    /// <remarks>
    /// <b>The text, not the exit code.</b> <c>is-enabled</c> exits non-zero for <c>disabled</c>,
    /// for <c>masked</c> and for a unit that does not exist alike, so the exit code cannot tell
    /// those three apart and the word systemd printed can. Anything unrecognised travels verbatim
    /// rather than being collapsed: <see cref="SystemControlResult.Output"/> is standard output
    /// followed by standard error, so a frame with no such unit puts systemd's own sentence about
    /// it into the delta instead of a word this code invented.
    /// </remarks>
    public static async Task<string> AnswerAsync(
        ISystemControl systemControl,
        string question,
        string unit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(systemControl);

        var result = await systemControl.RunAsync([question, unit], cancellationToken).ConfigureAwait(false);

        foreach (var raw in result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return NoAnswer;
    }
}
