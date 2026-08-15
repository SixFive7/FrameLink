namespace FrameLink.Agent.Hosting;

/// <summary>
/// Read access to files outside the agent's own state directory — the boot partition, and
/// <c>/proc</c>.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="IStateStore"/> and deliberately read-only: the boot
/// partition and <c>/proc</c> are things the agent <i>consults</i>, never things it owns.
/// </remarks>
public interface ITextFileReader
{
    /// <summary>Reads <paramref name="path"/>, or returns <see langword="null"/> if it cannot be read.</summary>
    /// <remarks>
    /// Absent, unreadable and malformed all collapse to <see langword="null"/> on purpose:
    /// every caller is probing an optional source, and "there is nothing here" is the answer
    /// in each case.
    /// </remarks>
    string? ReadAllTextOrNull(string path);
}

/// <summary>Reads real files.</summary>
public sealed class HostTextFileReader : ITextFileReader
{
    /// <summary>The shared instance.</summary>
    public static HostTextFileReader Instance { get; } = new();

    /// <inheritdoc/>
    public string? ReadAllTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
