using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Reconcile;

/// <summary>
/// A value that changes when, and only when, the machine boots.
/// </summary>
/// <remarks>
/// <para>
/// This is what turns §2.4 from a rule into a check. "Applied is never claimed from a successful
/// write, only from an observation made after the setting had to survive a boot" is only
/// enforceable if the agent can tell a reboot from a service restart — and it cannot otherwise,
/// because <c>Restart=always</c> means the agent comes back from a crash looking exactly like it
/// came back from a boot.
/// </para>
/// <para>
/// Without it, an agent that wrote a setting and then merely crashed would re-read the value it
/// had just written, find it correct, and report <c>InSync</c>. That is precisely the write-only
/// check the hostname trap defeats.
/// </para>
/// </remarks>
public interface IBootIdentity
{
    /// <summary>The current boot's identity.</summary>
    string Current { get; }
}

/// <summary>
/// The kernel's own boot id, from <c>/proc/sys/kernel/random/boot_id</c>.
/// </summary>
/// <remarks>
/// Chosen over uptime, over <c>systemd</c>'s boot id and over a counter the agent keeps itself:
/// the kernel generates it once per boot and nothing on the frame can change it, so an agent
/// cannot be fooled into thinking it rebooted by anything short of actually rebooting.
/// </remarks>
public sealed class KernelBootIdentity : IBootIdentity
{
    /// <summary>Where the kernel publishes it.</summary>
    public const string Path = "/proc/sys/kernel/random/boot_id";

    private readonly string _current;

    /// <summary>Reads the boot id once, at construction.</summary>
    /// <param name="files">Where to read it from.</param>
    /// <remarks>
    /// Read once rather than per call because it cannot change while this process lives: if it
    /// did, the process would not be alive to notice.
    /// </remarks>
    public KernelBootIdentity(ITextFileReader files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var raw = files.ReadAllTextOrNull(Path)?.Trim();

        // A machine without the kernel file — a container, a workstation — gets a per-process
        // value. That is the conservative answer rather than a convenient one: it means every
        // process start looks like a reboot on such a host, so a resource is verified more often
        // than strictly necessary, never less.
        _current = string.IsNullOrEmpty(raw) ? Guid.NewGuid().ToString("n") : raw;
    }

    /// <inheritdoc/>
    public string Current => _current;
}

/// <summary>
/// A boot identity that can be advanced on demand, for the in-process reboot boundary.
/// </summary>
/// <remarks>
/// Lives beside the production implementation rather than in the test project because
/// <see cref="InProcessRebootBoundary"/> is production code for a virtual agent (§5.3), and the
/// two have to move together.
/// </remarks>
public sealed class MutableBootIdentity : IBootIdentity
{
    private string _current;

    /// <summary>Starts at <paramref name="initial"/>, or at a generated value.</summary>
    public MutableBootIdentity(string? initial = null) =>
        _current = initial ?? "boot-1";

    /// <summary>How many times the machine has "booted".</summary>
    public int Boots { get; private set; }

    /// <inheritdoc/>
    public string Current => _current;

    /// <summary>Moves to a new boot.</summary>
    public void Advance()
    {
        Boots++;
        _current = $"boot-{Boots + 1}";
    }
}
