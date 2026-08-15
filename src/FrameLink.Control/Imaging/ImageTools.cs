using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FrameLink.Control.Imaging;

/// <summary>The three external programs the generator drives.</summary>
/// <remarks>
/// All three come from two Debian packages — <c>e2fsprogs</c> and <c>mtools</c>, measured at
/// 1537 kB and 400 kB installed. None of them needs privilege, a loop device, a device mapping
/// or an emulator: they are file editors that happen to understand filesystem layouts, which is
/// what lets an amd64 Fleet Manager write an arm64 image with no emulation anywhere in the path.
/// </remarks>
public enum ImageTool
{
    /// <summary>The ext4 editor. Writes files, sets modes and owners, creates symlinks.</summary>
    Debugfs,

    /// <summary>The FAT writer from mtools. Copies the boot-partition seed file in.</summary>
    Mcopy,

    /// <summary>The filesystem checker. The gate an artifact must pass before it is offered.</summary>
    E2fsck,
}

/// <summary>What running one tool produced.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Output">Standard output and standard error, interleaved in the order written.</param>
public sealed record ImageToolResult(int ExitCode, string Output);

/// <summary>Runs one external tool. The seam the tests replace.</summary>
/// <remarks>
/// The suite runs on whatever the developer is sitting at — Windows, in this project's case —
/// and none of these three tools exists there. Everything worth asserting about the generator is
/// on this side of the seam anyway: which commands it issues, in which order, with which
/// arguments, and what it does with what comes back. The other side is the part that has to be
/// proven once against a real image rather than continuously against a fixture, and a 3 GB
/// fixture in the repository would prove it no better.
/// </remarks>
public interface IImageToolRunner
{
    /// <summary>Runs <paramref name="tool"/> with <paramref name="arguments"/> and collects its output.</summary>
    /// <param name="tool">Which program to run.</param>
    /// <param name="arguments">Arguments, already split — never a shell string.</param>
    /// <param name="workingDirectory">Directory to run in. Every path in the arguments is relative to it.</param>
    /// <param name="cancellationToken">Abandons the run.</param>
    Task<ImageToolResult> RunAsync(
        ImageTool tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Decides whether a tool run succeeded — which for one of the three is not the exit code.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>debugfs -R</c> exits 0 when the request fails.</b> Measured against e2fsprogs 1.47.2 in
/// a plain <c>debian:trixie-slim</c> container, every one of these exits 0:
/// </para>
/// <code>
/// write: File not found by ext2_lookup          (the parent directory does not exist)
/// write: Ext2 file already exists               (the target is already there)
/// write: Filesystem opened read/only            (the -w flag was forgotten)
/// symlink: File not found by ext2_lookup        (the parent directory does not exist)
/// mkdir: Ext2 directory already exists          (and see below)
/// ls: Filesystem not open                       (the offset was past the end of the file)
/// </code>
/// <para>
/// A generator that trusted the exit code would therefore write a seed file, silently fail to
/// install the binary, pass its own checks and hand somebody a card that boots into a stock
/// Raspberry Pi OS. So success is decided by the <i>output</i>, and by a whitelist rather than a
/// blacklist: two benign lines are known — the version banner and <c>Allocated inode:</c> — and
/// anything else at all is a failure. An unrecognised message from a future e2fsprogs is then a
/// refusal, which is the safe direction to be wrong in.
/// </para>
/// <para>
/// <b>The <c>mkdir</c> case is worse than a wasted call and is why <see cref="ImagePlan"/>
/// contains none.</b> <c>debugfs mkdir</c> on a directory that already exists allocates and
/// initialises the inode <i>before</i> discovering the name is taken, then abandons it. The
/// result is a genuinely corrupt filesystem — <c>e2fsck</c> reports <c>Unconnected directory
/// inode 14 (was in /)</c> and exits 4 — produced by a command that printed a message and exited
/// 0. Every directory the plan writes into (<c>/usr/local/bin</c>,
/// <c>/etc/systemd/system</c>, <c>/etc/systemd/system/multi-user.target.wants</c>) exists in the
/// pinned base image, and the pin is what makes relying on that safe.
/// </para>
/// <para>
/// <c>mcopy</c> and <c>e2fsck</c> are honest: mtools returns non-zero on any failure, and
/// <c>e2fsck</c> returns 0 for clean, 4 for errors left uncorrected under <c>-n</c>, and 8 for an
/// operational error such as an unreadable superblock. The gate demands 0 exactly.
/// </para>
/// </remarks>
public static class ImageToolVerdict
{
    /// <summary>Explains why a run failed, or returns null when it succeeded.</summary>
    public static string? Diagnose(ImageTool tool, ImageToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return tool switch
        {
            ImageTool.Debugfs => DiagnoseDebugfs(result),
            ImageTool.E2fsck => result.ExitCode == 0
                ? null
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"e2fsck exited {result.ExitCode}, so the filesystem is not clean: {Summarise(result.Output)}"),
            _ => result.ExitCode == 0
                ? null
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Executable(tool)} exited {result.ExitCode}: {Summarise(result.Output)}"),
        };
    }

    /// <summary>The program name to execute for a tool.</summary>
    public static string Executable(ImageTool tool) => tool switch
    {
        ImageTool.Debugfs => "debugfs",
        ImageTool.Mcopy => "mcopy",
        ImageTool.E2fsck => "e2fsck",
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    private static string? DiagnoseDebugfs(ImageToolResult result)
    {
        if (result.ExitCode != 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"debugfs exited {result.ExitCode}: {Summarise(result.Output)}");
        }

        foreach (var rawLine in result.Output.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || IsBenign(line))
            {
                continue;
            }

            return $"debugfs refused the request: {line}";
        }

        return null;
    }

    /// <summary>The two lines a successful mutating request is allowed to print.</summary>
    private static bool IsBenign(string line) =>
        // "debugfs 1.47.2 (1-Jan-2025)" — the banner, on every invocation.
        (line.StartsWith("debugfs ", StringComparison.Ordinal)
            && line.Length > 8
            && char.IsAsciiDigit(line[8]))

        // "Allocated inode: 16" — what a successful `write` prints.
        || line.StartsWith("Allocated inode: ", StringComparison.Ordinal);

    private static string Summarise(string output)
    {
        var collapsed = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return collapsed.Length <= 400 ? collapsed : collapsed[..400] + "…";
    }
}

/// <summary>Runs the tools as child processes.</summary>
/// <remarks>
/// <para>
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a joined string, so
/// nothing anywhere needs quoting and an operator-supplied URL cannot become an argument. There
/// is no shell in this path at all.
/// </para>
/// <para>
/// Every run is anchored to a working directory holding the staged files, and every local
/// filename in an argument is a bare relative name. That is not tidiness either:
/// <c>debugfs -R</c> takes its whole request as one string and splits it on whitespace, so a
/// staging path containing a space would silently become two arguments. Relative names in a
/// directory this code creates removes the possibility rather than escaping it.
/// </para>
/// </remarks>
public sealed class ProcessImageToolRunner : IImageToolRunner
{
    /// <inheritdoc/>
    public async Task<ImageToolResult> RunAsync(
        ImageTool tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = ImageToolVerdict.Executable(tool),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();

        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The overwhelmingly likely cause, and the one worth naming: the container was built
            // without e2fsprogs or mtools. Saying which program is missing is the difference
            // between a one-line Dockerfile fix and an afternoon.
            return new ImageToolResult(
                127,
                $"'{startInfo.FileName}' could not be started: {exception.Message}. "
                + "The Fleet Manager image needs the e2fsprogs and mtools packages.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ImageToolResult(process.ExitCode, output.ToString());

        void Collect(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is not null)
            {
                lock (output)
                {
                    output.Append(args.Data).Append('\n');
                }
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            // It had already exited, which is the outcome wanted anyway.
        }
    }
}
