using System.ComponentModel;
using System.Diagnostics;

namespace FrameLink.Agent.Hosting;

/// <summary>One GPIO line, as the agent asks for it.</summary>
/// <param name="Chip">The GPIO chip the line belongs to, for the offset form of the request.</param>
/// <param name="Line">The BCM line number — physical pin 11 is BCM 17.</param>
/// <param name="Consumer">
/// The name the claim is recorded under. It is what <c>gpioinfo</c> prints beside the line, and
/// therefore the whole of how <c>gpio.button.line</c> tells "the agent is holding it" apart from
/// "somebody else is".
/// </param>
/// <param name="Debounce">
/// How long a level must settle before it counts. A mechanical button's contacts chatter for a few
/// milliseconds as they close, and without this one press arrives as several.
/// </param>
public readonly record struct GpioLineRequest(string Chip, int Line, string Consumer, TimeSpan Debounce);

/// <summary>
/// How the agent claims a GPIO line and hears the button on it.
/// </summary>
/// <remarks>
/// <para>
/// A seam for the same reason every other Linux surface in the agent has one: there is no GPIO on
/// a workstation, and the whole of the button's behaviour — the debounce, the toggle, the
/// simulated press, the retry after a lost claim — has to be assertable without a Pi.
/// </para>
/// <para>
/// <b>It is a long-lived hold, not a poll.</b> The claim is the resource: a line is "requested" by
/// a consumer for as long as something holds it open, and that is exactly what <c>gpioinfo</c>
/// reports. So this method does not return until the claim ends, and its return value is the
/// reason it ended.
/// </para>
/// </remarks>
public interface IGpioLines
{
    /// <summary>
    /// Holds <paramref name="request"/> open, calling <paramref name="onPress"/> once per debounced
    /// press, until the claim ends or <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <returns>Why the claim ended, in one plain sentence.</returns>
    Task<string> WatchAsync(GpioLineRequest request, Action onPress, CancellationToken cancellationToken);
}

/// <summary>
/// The real claim: <c>gpiomon</c> from the stock <c>gpiod</c> package, held open as a child process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a tool and not an ioctl.</b> The catalog's Observe for this resource is <c>gpioinfo</c>,
/// which reads the kernel's own record of who holds a line — so the claim has to be visible there,
/// and a claim made by <c>gpiomon</c> is. Driving <c>/dev/gpiochip0</c> directly through
/// <c>ioctl</c> would work too and would drop the package dependency, but it would be a hundred
/// lines of struct interop written against hardware nobody here can test on, and its failure mode
/// is an <c>EINVAL</c> that looks exactly like a wiring fault. This one fails with a sentence.
/// </para>
/// <para>
/// <b>Two request forms are tried, and the reason is a real uncertainty about this hardware.</b>
/// libgpiod v2 accepts either a chip and an offset or a bare line name, and which one is right
/// depends on how the running kernel enumerates the Pi 5's RP1 pin controller — the header has
/// been <c>gpiochip0</c> and <c>gpiochip4</c> on different kernels. Trying the offset form and then
/// the name form costs one failed exec on a frame where the first form is wrong, and turns a
/// hardware-numbering surprise into a claim that still works. Whichever form holds is named in the
/// reason string, so the frame reports which one it needed rather than leaving it to be guessed.
/// </para>
/// <para>
/// <b>The child is killed on the way out.</b> A <c>gpiomon</c> that outlives the agent keeps
/// holding the line, and the next agent finds it contended by a consumer with its own name — the
/// most confusing possible failure. systemd's cgroup teardown would catch it as well; this does not
/// rely on that.
/// </para>
/// </remarks>
public sealed class GpioMonLines : IGpioLines
{
    /// <summary>The tool that holds a line and prints its edges.</summary>
    public const string Executable = "gpiomon";

    /// <summary>
    /// How long a started <c>gpiomon</c> has to stay up before the claim counts as held.
    /// </summary>
    /// <remarks>
    /// A refused claim — no such chip, no such line, somebody else holding it — exits immediately
    /// with a message on stderr. Anything still running after this has the line.
    /// </remarks>
    public static TimeSpan ClaimGrace { get; } = TimeSpan.FromSeconds(2);

    /// <summary>The shared instance.</summary>
    public static GpioMonLines Instance { get; } = new();

    /// <summary>
    /// The argument vectors tried, in order: chip and offset first, then the bare line name.
    /// </summary>
    /// <remarks>
    /// Public because it is the only part of this class that can be asserted off a frame, and it
    /// carries the three properties the catalog's resource is about — the internal pull-up, the
    /// falling edge a button to ground produces, and the 50 ms debounce.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<string>> Vectors(GpioLineRequest request)
    {
        var common = new[]
        {
            "--bias=pull-up",
            "--edges=falling",
            $"--debounce-period={(int)request.Debounce.TotalMilliseconds}ms",
            "--consumer",
            request.Consumer,
        };

        var offset = request.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return
        [
            [.. common, "--chip", request.Chip, offset],
            [.. common, "GPIO" + offset],
        ];
    }

    /// <inheritdoc/>
    public async Task<string> WatchAsync(
        GpioLineRequest request,
        Action onPress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onPress);

        var refusals = new List<string>(2);

        foreach (var vector in Vectors(request))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attempt = await AttemptAsync(vector, onPress, cancellationToken).ConfigureAwait(false);
            if (attempt.Held)
            {
                return attempt.Reason;
            }

            refusals.Add($"{Executable} {string.Join(' ', vector)} — {attempt.Reason}");
        }

        return string.Join("; ", refusals);
    }

    private static async Task<(bool Held, string Reason)> AttemptAsync(
        IReadOnlyList<string> vector,
        Action onPress,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in vector)
        {
            start.ArgumentList.Add(argument);
        }

        Process? started;

        try
        {
            started = Process.Start(start);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The one case worth naming outright: the tools are not installed. The catalog calls
            // `gpiod` stock, so this sentence is either a frame whose package set was cut down or
            // a gap in the catalog — and both are things a person has to be told, not worked around.
            return (false, $"{Executable} could not be started ({exception.Message}). The gpiod package provides it.");
        }

        if (started is null)
        {
            return (false, $"{Executable} did not start");
        }

        using var process = started;

        // Drained from the first moment. A full stderr pipe blocks the child, which on this path
        // would be a claim that looks held and reports nothing.
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var exit = process.WaitForExitAsync(cancellationToken);

        var held = await Task.WhenAny(exit, Task.Delay(ClaimGrace, cancellationToken)).ConfigureAwait(false) != exit;

        if (!held && !cancellationToken.IsCancellationRequested)
        {
            var why = (await errors.ConfigureAwait(false)).Trim();
            return (false, why.Length == 0 ? $"exited with code {process.ExitCode}" : why.Replace('\n', ' '));
        }

        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // Every line gpiomon prints is one debounced falling edge — the tool does the
                // debouncing in the kernel request, so there is nothing to filter here beyond the
                // blank line a flushed stream can leave behind.
                if (line.Trim().Length > 0)
                {
                    onPress();
                }
            }

            await exit.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killed rather than left running: a gpiomon that outlives its claim keeps holding the
            // line, and the next attempt would find it contended by a consumer with its own name.
            Stop(process);
            return (true, "the claim was released");
        }
        catch (IOException exception)
        {
            Stop(process);
            return (true, $"the connection to {Executable} broke: {exception.Message}");
        }

        var last = (await errors.ConfigureAwait(false)).Trim();

        return (true, last.Length == 0
            ? $"{Executable} exited with code {process.ExitCode}"
            : $"{Executable} exited with code {process.ExitCode}: {last.Replace('\n', ' ')}");
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // It exited between the check and the kill, or the platform will not say. Either way
            // the claim is gone, which is all the caller needs.
        }
    }
}
