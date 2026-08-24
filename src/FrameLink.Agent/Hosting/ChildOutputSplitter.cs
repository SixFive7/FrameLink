namespace FrameLink.Agent.Hosting;

/// <summary>
/// Cuts a child process's raw output into the segments a person would see, as they arrive.
/// </summary>
/// <remarks>
/// <para>
/// <b>It splits on the carriage return as well as the newline, and that is the whole reason it
/// exists.</b> A progress bar is not a sequence of lines: <c>dfu-util</c> draws one bar and rewrites
/// it in place with a bare <c>\r</c> for every transfer block, so a reader that waited for
/// <c>\n</c> would receive the entire download as a single enormous line at the moment the write
/// finished — which is precisely the "nothing at all until it is over" this feature was written to
/// remove.
/// </para>
/// <para>
/// <b>The sink is called on the pipe drain, so it may not block.</b> A child's stdout is a pipe with
/// a fixed kernel buffer: a reader that stops reading stops the writer, and the writer here is the
/// program writing an array's flash. So the contract on <paramref name="sink"/> is that it returns
/// promptly and does no I/O — the one sink in this product is
/// <c>ArrayFlashProgressBox.Read</c>, which is a scan of one short line and a single reference
/// write, and everything that can hang or block is downstream of it on a task of its own
/// (<c>ArrayFlashProgressPump</c>).
/// </para>
/// <para>
/// <b>What it does guard is throwing</b>, which is the failure a sink can have without meaning to.
/// An exception out of the sink would end the drain, fill the pipe and stall the write; so every
/// call is wrapped and a sink that throws is simply not told about that segment. Nothing is
/// reported about it, because the surface that would report it is the one that just failed.
/// </para>
/// </remarks>
/// <param name="sink">Where each segment goes. Must return promptly; may throw.</param>
public sealed class ChildOutputSplitter(Action<string> sink)
{
    private readonly Action<string> _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly System.Text.StringBuilder _partial = new(160);

    /// <summary>How many segments have been handed to the sink.</summary>
    public int Segments { get; private set; }

    /// <summary>Takes the next chunk of raw output. Never throws.</summary>
    public void Write(ReadOnlySpan<char> chunk)
    {
        foreach (var character in chunk)
        {
            if (character is '\n' or '\r')
            {
                Emit();
                continue;
            }

            _partial.Append(character);

            // A guard, not a limit anybody should reach. A child that writes a megabyte with no
            // separator in it would otherwise grow this buffer without bound on a frame with two
            // gigabytes of memory; cutting it is strictly better than the alternative, and no line
            // this parser cares about is anywhere near this long.
            if (_partial.Length >= 4096)
            {
                Emit();
            }
        }
    }

    /// <summary>Hands over whatever is left when the child has closed its stream. Never throws.</summary>
    public void Flush() => Emit();

    private void Emit()
    {
        if (_partial.Length == 0)
        {
            return;
        }

        var segment = _partial.ToString();
        _partial.Clear();
        Segments++;

        try
        {
            _sink(segment);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Swallowed on purpose, and with nothing said. This runs between a child process and
            // the drain of its own pipe; there is no diagnostic worth stalling that for.
        }
    }
}
