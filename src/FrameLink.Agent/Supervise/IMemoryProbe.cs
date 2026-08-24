using System.Globalization;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Supervise;

/// <summary>One reading of the two numbers §2.10's memory watchdog compares.</summary>
/// <param name="BrowserTreeRssKb">
/// The <b>sum of the whole Chromium process tree's</b> resident memory, in kB.
/// </param>
/// <param name="BrowserProcesses">How many processes went into that sum.</param>
/// <param name="MemAvailableKb">
/// The kernel's own estimate of memory available without swapping, in kB.
/// </param>
public readonly record struct MemorySample(long BrowserTreeRssKb, int BrowserProcesses, long MemAvailableKb);

/// <summary>Reads the two numbers the memory watchdog acts on.</summary>
public interface IMemoryProbe
{
    /// <summary>Takes one reading.</summary>
    ValueTask<MemorySample> SampleAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The real probe: <c>/proc/meminfo</c> and <c>ps</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sum the whole tree, never the main process.</b> This is the single most important sentence
/// in §2.10's evidence, and it is a measured failure rather than a design preference: a leaking
/// renderer grew past 1.4 GB and was OOM-killed while Chromium's main process sat at an innocent
/// 130 MB. A watchdog that reads the main process never fires — it would have watched v1 die every
/// ninety minutes and reported a healthy 130 MB throughout.
/// </para>
/// <para>
/// <c>MemAvailable</c> rather than <c>MemFree</c>, because the kernel's estimate accounts for
/// reclaimable page cache and <c>MemFree</c> does not; on a frame with a busy card, <c>MemFree</c>
/// is near zero at all times and would fire the floor constantly.
/// </para>
/// </remarks>
public sealed class ProcMemoryProbe : IMemoryProbe
{
    /// <summary>Where the kernel publishes its memory summary.</summary>
    public const string MemInfoPath = "/proc/meminfo";

    /// <summary>The substring that identifies a browser process in <c>ps</c> output.</summary>
    public const string BrowserProcessName = "chromium";

    private readonly ISystemFiles _files;
    private readonly IProcessRunner _processes;

    /// <summary>Creates the probe.</summary>
    public ProcMemoryProbe(ISystemFiles files, IProcessRunner processes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(processes);

        _files = files;
        _processes = processes;
    }

    /// <inheritdoc/>
    public async ValueTask<MemorySample> SampleAsync(CancellationToken cancellationToken)
    {
        // <b>Thirty seconds on a command that answers in milliseconds, and short on purpose.</b>
        // This is the memory watchdog's only measurement and it is the frame's last defence against
        // an OOM kill, so a /proc that has stopped answering has to become a reported failure
        // quickly rather than hold §2.10's five behaviours through tick after tick — they share one.
        var listed = ProcessTimeoutException.ThrowIfTimedOut(await _processes
            .RunAsync("ps", ["-eo", "rss=,comm="], ProcessDeadline.Local, cancellationToken)
            .ConfigureAwait(false));

        var (rss, processes) = SumBrowserTree(listed.StandardOutput);

        return new MemorySample(rss, processes, MemAvailableKb(_files.ReadText(MemInfoPath)));
    }

    /// <summary>Sums every browser process's RSS out of <c>ps -eo rss=,comm=</c> output.</summary>
    public static (long Kilobytes, int Processes) SumBrowserTree(string psOutput)
    {
        ArgumentNullException.ThrowIfNull(psOutput);

        long total = 0;
        var counted = 0;

        foreach (var line in psOutput.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 2
                || !fields[1].Contains(BrowserProcessName, StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes))
            {
                continue;
            }

            total += kilobytes;
            counted++;
        }

        return (total, counted);
    }

    /// <summary>The <c>MemAvailable</c> line of <c>/proc/meminfo</c>, in kB, or -1.</summary>
    /// <remarks>
    /// Minus one rather than zero for "could not read", because zero is a real and catastrophic
    /// value: a floor comparison against it would restart the browser on every tick of a machine
    /// with no <c>/proc/meminfo</c> at all, which is every workstation the suite runs on.
    /// </remarks>
    public static long MemAvailableKb(string? memInfo)
    {
        if (string.IsNullOrEmpty(memInfo))
        {
            return -1;
        }

        foreach (var line in memInfo.Split('\n'))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line["MemAvailable:".Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (fields.Length >= 1
                && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes))
            {
                return kilobytes;
            }
        }

        return -1;
    }
}
