using System.Globalization;

namespace FrameLink.Control.Imaging;

/// <summary>One thing the generator does, in order.</summary>
/// <param name="Description">What this step is for, in words an operator can read while it runs.</param>
public abstract record ImageStep(string Description);

/// <summary>Writes a generated text file into the working directory.</summary>
/// <param name="Description">What this step is for.</param>
/// <param name="FileName">Bare relative name inside the working directory.</param>
/// <param name="Content">The exact bytes, LF-terminated.</param>
public sealed record StageTextStep(string Description, string FileName, string Content)
    : ImageStep(Description);

/// <summary>Copies an existing file into the working directory.</summary>
/// <param name="Description">What this step is for.</param>
/// <param name="SourcePath">Absolute path of the file to copy.</param>
/// <param name="FileName">Bare relative name to give it inside the working directory.</param>
public sealed record StageCopyStep(string Description, string SourcePath, string FileName)
    : ImageStep(Description);

/// <summary>Runs one external tool against the image.</summary>
/// <param name="Description">What this step is for.</param>
/// <param name="Tool">Which program.</param>
/// <param name="Arguments">Arguments, already split.</param>
public sealed record RunToolStep(string Description, ImageTool Tool, IReadOnlyList<string> Arguments)
    : ImageStep(Description);

/// <summary>
/// The complete, ordered recipe for turning a verified stock image into a FrameLink one.
/// </summary>
/// <remarks>
/// <para>
/// A value rather than a method body, so the whole thing can be asserted. What the generator
/// does <i>is</i> this list: the tests read it and check every argument, and
/// <see cref="ImageBuilder"/> only walks it. There is no second place where a command is
/// assembled.
/// </para>
/// <para>
/// <b>Nothing an operator typed ever reaches an argument.</b> Every filename in the list is a
/// compile-time constant and every path inside a <c>debugfs -R</c> request string is absolute
/// and fixed. The one operator-controlled thing in a generated image — the control URL — travels
/// as the <i>content</i> of a staged file, which <c>mcopy</c> reads from disk. That is the
/// property that makes the absence of shell quoting in <see cref="ProcessImageToolRunner"/> safe
/// rather than lucky.
/// </para>
/// <para>
/// <b>There is no <c>mkdir</c> anywhere in this plan and there must never be one.</b>
/// <c>debugfs mkdir</c> on an existing directory leaves an orphaned inode and a filesystem
/// <c>e2fsck</c> calls corrupt, while exiting 0 — the full measurement is on
/// <see cref="ImageToolVerdict"/>. All three directories written into exist in the pinned base
/// image, and <see cref="BaseImagePin.VerifyAsync"/> running first is what makes depending on
/// that a fact rather than a hope.
/// </para>
/// <para>
/// The last step is the gate. <c>e2fsck -fn</c> is read-only — <c>-n</c> answers "no" to every
/// repair question — and the artifact is not offered unless it exits 0. It is the only thing
/// standing between a mistake anywhere above and a card in somebody's hand, and it is the reason
/// the failure mode being designed against is a wasted rebuild rather than a wasted trip.
/// </para>
/// </remarks>
public static class ImagePlan
{
    /// <summary>Where the agent binary lands, per §2.1 and the unit's <c>ExecStart</c>.</summary>
    public const string AgentBinaryPath = "/usr/local/bin/fl-agent";

    /// <summary>Where systemd reads operator-installed units.</summary>
    public const string UnitPath = "/etc/systemd/system/fl-agent.service";

    /// <summary>
    /// The symlink that <i>is</i> <c>systemctl enable</c>.
    /// </summary>
    /// <remarks>
    /// Enablement is not a database somewhere; it is this symlink. Creating it beside the stock
    /// <c>userconfig.service</c>, <c>NetworkManager.service</c> and the rest is exactly what
    /// <c>systemctl enable fl-agent.service</c> would have produced, so a frame built from a
    /// generated image starts the agent on its very first boot with nobody logged in.
    /// </remarks>
    public const string WantsLinkPath = "/etc/systemd/system/multi-user.target.wants/fl-agent.service";

    /// <summary>Name the working copy of the image is given.</summary>
    public const string WorkingImageName = "framelink.img";

    /// <summary>Staged name of the agent binary.</summary>
    public const string StagedAgentName = "fl-agent";

    /// <summary>Staged name of the systemd unit.</summary>
    public const string StagedUnitName = "fl-agent.service";

    /// <summary><c>0100755</c>: a regular file, rwxr-xr-x. The mode <c>sif</c> takes is the raw inode mode.</summary>
    public const string AgentBinaryMode = "0100755";

    /// <summary><c>0100644</c>: a regular file, rw-r--r--.</summary>
    public const string UnitMode = "0100644";

    /// <summary>Builds the plan for one image.</summary>
    /// <param name="geometry">Layout read from the image itself, never from the pin.</param>
    /// <param name="seed">What the image will carry.</param>
    /// <param name="agentBinaryPath">Absolute path of the <c>linux-arm64</c> agent this server serves.</param>
    /// <param name="unitText">The <c>fl-agent.service</c> text, from the embedded resource.</param>
    public static IReadOnlyList<ImageStep> Create(
        ImageGeometry geometry,
        ImageSeed seed,
        string agentBinaryPath,
        string unitText)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentBinaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitText);

        var boot = geometry.Boot
            ?? throw new ArgumentException("The image has no FAT boot partition.", nameof(geometry));
        var root = geometry.Root
            ?? throw new ArgumentException("The image has no ext4 root partition.", nameof(geometry));

        // mtools addresses a partition inside a file with `image@@offset`; e2fsprogs with
        // `image?offset=`. Two syntaxes for one idea, and both are byte offsets, which is why
        // ImageGeometry hands out bytes rather than sectors.
        var fat = string.Create(CultureInfo.InvariantCulture, $"{WorkingImageName}@@{boot.OffsetBytes}");
        var ext = string.Create(CultureInfo.InvariantCulture, $"{WorkingImageName}?offset={root.OffsetBytes}");

        return
        [
            new StageTextStep(
                "Write the discovery seed the agent reads on first boot",
                ImageSeed.BootFileName,
                seed.RenderBootFile()),

            new StageCopyStep(
                "Take a copy of the agent binary this Fleet Manager serves",
                agentBinaryPath,
                StagedAgentName),

            new StageTextStep(
                "Write the systemd unit that will start the agent",
                StagedUnitName,
                unitText),

            new RunToolStep(
                $"Copy {ImageSeed.BootFileName} onto the boot partition",
                ImageTool.Mcopy,
                ["-i", fat, "-o", ImageSeed.BootFileName, $"::{ImageSeed.BootFileName}"]),

            new RunToolStep(
                $"Install the agent at {AgentBinaryPath}",
                ImageTool.Debugfs,
                ["-w", "-R", $"write {StagedAgentName} {AgentBinaryPath}", ext]),

            // debugfs stamps a newly written inode with the *caller's* uid and gid, so on a Fleet
            // Manager container that does not run as root the binary would land owned by whoever
            // the server runs as — and systemd would start it, as root, from a file that user can
            // rewrite. These three are the difference between that and 0755 root:root.
            new RunToolStep(
                "Make the agent executable",
                ImageTool.Debugfs,
                ["-w", "-R", $"sif {AgentBinaryPath} mode {AgentBinaryMode}", ext]),

            new RunToolStep(
                "Give the agent to root",
                ImageTool.Debugfs,
                ["-w", "-R", $"sif {AgentBinaryPath} uid 0", ext]),

            new RunToolStep(
                "Give the agent to the root group",
                ImageTool.Debugfs,
                ["-w", "-R", $"sif {AgentBinaryPath} gid 0", ext]),

            new RunToolStep(
                $"Install the unit at {UnitPath}",
                ImageTool.Debugfs,
                ["-w", "-R", $"write {StagedUnitName} {UnitPath}", ext]),

            new RunToolStep(
                "Set the unit's permissions",
                ImageTool.Debugfs,
                ["-w", "-R", $"sif {UnitPath} mode {UnitMode}", ext]),

            new RunToolStep(
                "Give the unit to root",
                ImageTool.Debugfs,
                ["-w", "-R", $"sif {UnitPath} uid 0", ext]),

            new RunToolStep(
                "Give the unit to the root group",
                ImageTool.Debugfs,
                ["-w", "-R", $"sif {UnitPath} gid 0", ext]),

            new RunToolStep(
                "Enable the agent, so it starts on the first boot",
                ImageTool.Debugfs,
                ["-w", "-R", $"symlink {WantsLinkPath} {UnitPath}", ext]),

            new RunToolStep(
                "Check the filesystem before offering the image",
                ImageTool.E2fsck,
                ["-fn", ext]),
        ];
    }
}
