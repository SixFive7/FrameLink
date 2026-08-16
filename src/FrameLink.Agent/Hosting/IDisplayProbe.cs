namespace FrameLink.Agent.Hosting;

/// <summary>What was found when the agent asked whether anything can actually show a picture.</summary>
/// <param name="Visible">Whether a frame written to the console would produce pixels.</param>
/// <param name="Reason">One sentence a person can read.</param>
/// <param name="Evidence">What was inspected, verbatim, for the journal and telemetry.</param>
public readonly record struct DisplayVisibility(bool Visible, string Reason, string Evidence);

/// <summary>
/// Asks whether the console stage can be seen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured on the mule, 2026-08-15.</b> On a stock Raspberry Pi OS image the DSI panel is
/// dark and there is no framebuffer at all: <c>config.txt</c> carries only
/// <c>dtoverlay=vc4-kms-v3d</c>, both HDMI connectors report <c>disconnected</c>, there is no
/// DSI connector, <c>dmesg</c> repeats <c>vc4-drm axi:gpu: [drm] Cannot find any crtc or
/// sizes</c>, <c>/dev/fb0</c> does not exist and <c>/sys/class/backlight/</c> is empty.
/// </para>
/// <para>
/// <b>The write is no evidence either way.</b> The stage's terminal is a live virtual console —
/// <c>console=tty1</c> is on the kernel command line and <c>/sys/class/tty/console/active</c>
/// reads <c>ttyAMA10 tty1</c>, and the VT layer takes a write to any of its terminals whether or
/// not a framebuffer is behind it — so opening <c>/dev/tty8</c> and writing a whole designed frame
/// to it usually returns without error and produces nothing. Usually, not always: the same frame,
/// same darkness, has also answered <c>EIO</c> and taken the process down with it (see
/// <see cref="TerminalFailure"/>). That is exactly the shape of failure §2.4 exists to catch — a
/// successful write is not evidence of an applied state, and a failed one says nothing about the
/// picture either. The console stage cannot self-verify by writing, so it asks something else
/// instead.
/// </para>
/// <para>
/// This probe does not fix the darkness and is not meant to. It makes the darkness a
/// <i>reported condition</i> rather than silence, which is what §1.2.3 requires — every
/// abnormal state named, on the frame and in the Fleet Manager. When the frame's own screen is
/// the thing that is broken, the Fleet Manager is the only surface left.
/// </para>
/// </remarks>
public interface IDisplayProbe
{
    /// <summary>Looks for a framebuffer or a connected DRM output.</summary>
    DisplayVisibility Probe();
}

/// <summary>Reads <c>/dev/fb0</c>, <c>/sys/class/drm</c> and <c>/sys/class/backlight</c>.</summary>
/// <remarks>
/// Three independent signals rather than one, because each has a false negative on its own:
/// a KMS-only setup can lack <c>/dev/fb0</c> while driving a panel perfectly, a DRM card can
/// exist with every connector disconnected, and a backlight is present on the DSI panel but not
/// on HDMI. A connected DRM connector is the strongest single piece of evidence and is what the
/// verdict turns on; the rest is recorded so a human reading the telemetry sees what was
/// inspected.
/// </remarks>
public sealed class SysfsDisplayProbe : IDisplayProbe
{
    /// <summary>The legacy framebuffer device.</summary>
    public const string FramebufferPath = "/dev/fb0";

    /// <summary>Where DRM cards and connectors are published.</summary>
    public const string DrmPath = "/sys/class/drm";

    /// <summary>Where a panel's backlight appears once its overlay is loaded.</summary>
    public const string BacklightPath = "/sys/class/backlight";

    private readonly ISystemFiles _files;

    /// <summary>Creates the probe over <paramref name="files"/>.</summary>
    public SysfsDisplayProbe(ISystemFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
    }

    /// <inheritdoc/>
    public DisplayVisibility Probe()
    {
        var framebuffer = _files.FileExists(FramebufferPath);
        var backlights = _files.ListDirectories(BacklightPath).Count;

        var connectors = new List<string>();
        var connected = new List<string>();

        foreach (var card in _files.ListDirectories(DrmPath))
        {
            var status = _files.ReadText(card + "/status")?.Trim();
            if (string.IsNullOrEmpty(status))
            {
                // Cards themselves have no status file; only connectors do. Skipping them here
                // is what makes the connector list a connector list.
                continue;
            }

            var name = card[(card.LastIndexOf('/') + 1)..];
            connectors.Add($"{name}={status}");

            if (string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase))
            {
                connected.Add(name);
            }
        }

        var evidence = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{FramebufferPath}={(framebuffer ? "present" : "absent")}; "
            + $"drm=[{(connectors.Count == 0 ? "none" : string.Join(", ", connectors))}]; "
            + $"backlight={backlights}");

        if (connected.Count > 0)
        {
            return new DisplayVisibility(
                true,
                $"A display is connected on {string.Join(", ", connected)}.",
                evidence);
        }

        if (framebuffer)
        {
            // No connected connector but a framebuffer node exists. Weaker evidence, and worth
            // treating as visible rather than dark: reporting a working frame as blind would
            // teach an operator to ignore the warning.
            return new DisplayVisibility(
                true,
                $"No DRM connector reports connected, but {FramebufferPath} exists.",
                evidence);
        }

        return new DisplayVisibility(
            false,
            "Nothing on this frame can show a picture yet: there is no framebuffer and no DRM "
            + "connector reports a display. Writes to the console produce no pixels whether they "
            + "succeed or fail. The panel overlay has not been applied.",
            evidence);
    }
}

/// <summary>A probe with a fixed answer, for hosts where the question does not arise.</summary>
public sealed class StaticDisplayProbe : IDisplayProbe
{
    private readonly DisplayVisibility _result;

    /// <summary>Creates a probe that always answers <paramref name="result"/>.</summary>
    public StaticDisplayProbe(DisplayVisibility result) => _result = result;

    /// <summary>A probe that reports a working display.</summary>
    public static StaticDisplayProbe Visible { get; } =
        new(new DisplayVisibility(true, "Assumed visible.", "not probed"));

    /// <inheritdoc/>
    public DisplayVisibility Probe() => _result;
}
