using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Identity;

/// <summary>
/// The board's own markings, read from <c>/proc/cpuinfo</c>.
/// </summary>
/// <remarks>
/// §3.3: a pending frame shows its short fingerprint <i>and</i> its hardware serial, so an
/// operator with three identical frames on a bench can tell which row is which. The serial is
/// the only one of the two printed on the board itself, which is what makes the pairing useful.
/// </remarks>
public static class HardwareFacts
{
    private const string CpuInfoPath = "/proc/cpuinfo";

    /// <summary>Reads the board serial, or <see langword="null"/> where there is none.</summary>
    public static string? ReadSerial(ITextFileReader reader, string path = CpuInfoPath)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var content = reader.ReadAllTextOrNull(path);
        if (content is null)
        {
            return null;
        }

        foreach (var line in content.Split('\n'))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            if (!name.Equals("Serial", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }
}
