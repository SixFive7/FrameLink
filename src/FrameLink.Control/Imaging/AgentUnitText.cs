namespace FrameLink.Control.Imaging;

/// <summary>
/// The <c>fl-agent.service</c> text, so the Fleet Manager can put it in a generated image.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file, three consumers, and no third copy.</b> The unit already had two committed homes
/// — the agent's embedded resource and the harness's deploy asset — held equal by test because no
/// build step hands one artifact to both. A generated image needs the same text at
/// <c>/etc/systemd/system/fl-agent.service</c>, and a third committed copy would have been a
/// third thing to keep in step and a third chance for a frame to run a unit its neighbour does
/// not.
/// </para>
/// <para>
/// So this project embeds <i>the agent's file</i>. The <c>EmbeddedResource</c> item in
/// <c>FrameLink.Control.csproj</c> points across at
/// <c>../FrameLink.Agent/Systemd/fl-agent.service</c>, which is the same bytes on disk rather
/// than a copy of them. Drift is impossible by construction here, which is strictly better than
/// the equality test the other two need — and
/// <c>AgentSystemdUnitTests.The_generated_image_carries_the_same_unit</c> asserts it anyway,
/// because the thing that could still go wrong is somebody editing that csproj item.
/// </para>
/// <para>
/// The repository's <c>.gitattributes</c> declares <c>* text=auto eol=lf</c>, so the working-tree
/// copy is LF on every operating system. Nothing here re-normalises: a systemd unit whose values
/// carried a trailing carriage return would fail on the frame, and the right place to guarantee
/// that is the one rule that already covers every text file in the repository rather than a
/// defensive rewrite in one consumer.
/// </para>
/// </remarks>
public static class AgentUnitText
{
    /// <summary>Logical name of the embedded unit, matching the agent's own.</summary>
    public const string ResourceName = "fl-agent.service";

    /// <summary>Reads the unit text out of this assembly.</summary>
    public static string Read()
    {
        using var stream = typeof(AgentUnitText).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{ResourceName}' is missing from this build. "
                + "FrameLink.Control.csproj links it from ../FrameLink.Agent/Systemd/.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
