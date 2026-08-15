using System.Globalization;

namespace FrameLink.Agent.Hosting;

/// <summary>Severity of a log line.</summary>
public enum AgentLogLevel
{
    /// <summary>Normal progress.</summary>
    Info,

    /// <summary>Something is not as expected but the agent carries on.</summary>
    Warn,

    /// <summary>Something failed.</summary>
    Fail,
}

/// <summary>
/// The agent's journal.
/// </summary>
/// <remarks>
/// Deliberately tiny. The journal is the <i>second</i> honesty surface — the screen is the
/// first (§2.7) — and nothing that identifies a secret ever reaches either (§2.9).
/// </remarks>
public interface IAgentLog
{
    /// <summary>Records a line.</summary>
    void Write(AgentLogLevel level, string message);

    /// <summary>Records normal progress.</summary>
    void Info(string message) => Write(AgentLogLevel.Info, message);

    /// <summary>Records an unexpected but survivable condition.</summary>
    void Warn(string message) => Write(AgentLogLevel.Warn, message);

    /// <summary>Records a failure.</summary>
    void Fail(string message) => Write(AgentLogLevel.Fail, message);
}

/// <summary>Writes to standard output, which systemd routes into the journal.</summary>
public sealed class StandardOutputLog : IAgentLog
{
    /// <inheritdoc/>
    public void Write(AgentLogLevel level, string message) =>
        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[{level.ToString().ToUpperInvariant()}] {message}"));
}

/// <summary>Discards everything. Used where a log would be noise.</summary>
public sealed class NullLog : IAgentLog
{
    /// <summary>The shared instance.</summary>
    public static NullLog Instance { get; } = new();

    /// <inheritdoc/>
    public void Write(AgentLogLevel level, string message)
    {
        // Intentionally empty.
    }
}
