using System.Globalization;
using System.Security.Cryptography;
using FrameLink.Control.Updates;

namespace FrameLink.Control.Imaging;

/// <summary>Why a build stopped, or that it did not.</summary>
public enum ImageBuildResult
{
    /// <summary>An image was produced and passed its filesystem check.</summary>
    Succeeded,

    /// <summary>This Fleet Manager serves no <c>linux-arm64</c> agent yet, so there is nothing to install.</summary>
    NoAgentBinary,

    /// <summary>The pinned base image is not on disk.</summary>
    BaseImageMissing,

    /// <summary>A file is there, and it is not the pinned image.</summary>
    BaseImageMismatch,

    /// <summary>The image's partition table could not be read.</summary>
    GeometryUnreadable,

    /// <summary>The image's real layout disagrees with the layout the pin records.</summary>
    PinGeometryDrift,

    /// <summary>There is not enough room to build.</summary>
    InsufficientSpace,

    /// <summary>One of the external tools refused a step.</summary>
    ToolFailed,

    /// <summary><c>e2fsck</c> did not call the result clean, so nothing is offered.</summary>
    CheckFailed,

    /// <summary>Copying, staging or renaming failed.</summary>
    WriteFailed,
}

/// <summary>A finished, checked, ready-to-flash image.</summary>
public sealed record ImageArtifact
{
    /// <summary>Filename inside the image directory.</summary>
    public required string FileName { get; init; }

    /// <summary>Size in bytes. Identical to the base image's — writing files does not resize a disk.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Digest of the produced file, so an operator can check the card they flashed from.</summary>
    public required string Sha256 { get; init; }

    /// <summary>When it was produced.</summary>
    public required DateTimeOffset BuiltUtc { get; init; }

    /// <summary>The control URL seeded into it.</summary>
    public required string ControlUrl { get; init; }

    /// <summary>The upstream release it was built from.</summary>
    public required string BaseRelease { get; init; }

    /// <summary>The agent version it carries.</summary>
    public required string AgentVersion { get; init; }
}

/// <summary>What a build produced, or why it did not.</summary>
/// <param name="Result">The verdict.</param>
/// <param name="Problem">A sentence fit to show an operator when <paramref name="Result"/> is not success.</param>
/// <param name="Artifact">The image, on success.</param>
public sealed record ImageBuildOutcome(ImageBuildResult Result, string? Problem, ImageArtifact? Artifact);

/// <summary>Reports free space. The seam that lets a test drive a full disk.</summary>
public interface IStorageProbe
{
    /// <summary>Bytes available on the volume holding <paramref name="path"/>, or -1 if unknowable.</summary>
    long AvailableFreeSpaceBytes(string path);
}

/// <summary>Asks the operating system.</summary>
public sealed class DriveStorageProbe : IStorageProbe
{
    /// <inheritdoc/>
    public long AvailableFreeSpaceBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            // A path on a filesystem DriveInfo cannot describe. Unknown is not the same as zero,
            // and refusing to build because free space could not be read would be worse than the
            // problem: the copy below fails loudly on a full disk anyway.
            return -1;
        }
    }
}

/// <summary>
/// Turns the pinned stock image into a ready-to-flash FrameLink one.
/// </summary>
/// <remarks>
/// <para>
/// The whole capability is <c>e2fsprogs</c> and <c>mtools</c> editing a file. No privilege, no
/// <c>--cap-add</c>, no device mapping, no loop mount — <c>mount -o loop</c> was measured failing
/// in the same container these succeed in, which is the proof that loopback is not involved. And
/// because it is file manipulation rather than execution, an <b>amd64</b> Fleet Manager writes an
/// <b>arm64</b> image with no qemu, no binfmt and no emulation of any kind in the path.
/// </para>
/// <para>
/// The order below is the design. Verification of the base image comes before anything is copied,
/// so a wrong or truncated download costs nothing. The free-space check comes before the copy, so
/// a full disk is a sentence rather than a half-written image. And <c>e2fsck -fn</c> comes before
/// the artifact is named, so the only way to reach the output filename is to have passed. The
/// failure this is all aimed at is not an untidy server: it is somebody driving to a house with a
/// card that does not boot.
/// </para>
/// </remarks>
public sealed class ImageBuilder(
    ControlOptions options,
    AgentReleaseCatalog releases,
    IImageToolRunner runner,
    IStorageProbe storage,
    TimeProvider clock,
    ILogger<ImageBuilder> logger)
{
    /// <summary>Name given to the finished image.</summary>
    public const string ArtifactFileName = "framelink.img";

    /// <summary>Subdirectory a build happens in, so a failure leaves nothing beside the artifact.</summary>
    public const string WorkDirectoryName = "work";

    /// <summary>
    /// The pin this builder honours. <see cref="BaseImagePin.Current"/> unless overridden.
    /// </summary>
    /// <remarks>
    /// Settable so a test can pin a small synthetic image instead of a 2.8 GB one. That is the
    /// whole reason <see cref="BaseImagePin"/> is a record with required properties rather than a
    /// bag of constants: everything the verification path does — length check, digest check,
    /// geometry cross-check, the refusal messages — is then exercised against a file a test can
    /// write in a millisecond, at the same code, rather than against a fixture nobody would put in
    /// a repository.
    /// </remarks>
    public BaseImagePin Pin { get; init; } = BaseImagePin.Current;

    /// <summary>Where the base image is expected and the artifact is written.</summary>
    public string ImageDirectory => options.ImageDirectory;

    /// <summary>Full path of the pinned base image.</summary>
    public string BaseImagePath => Path.Combine(options.ImageDirectory, Pin.ImageFileName);

    /// <summary>Full path of the produced image, whether or not it exists.</summary>
    public string ArtifactPath => Path.Combine(options.ImageDirectory, ArtifactFileName);

    /// <summary>Builds one image.</summary>
    /// <param name="seed">What it will carry.</param>
    /// <param name="progress">Told each step's description as it starts.</param>
    /// <param name="cancellationToken">Abandons the build and removes the working directory.</param>
    public async Task<ImageBuildOutcome> BuildAsync(
        ImageSeed seed,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var release = releases.TryGet(AgentReleaseCatalog.PrimaryRuntimeIdentifier);
        var agentBinary = releases.ResolveBinaryPath(AgentReleaseCatalog.PrimaryRuntimeIdentifier);
        if (release is null || agentBinary is null)
        {
            return Fail(
                ImageBuildResult.NoAgentBinary,
                $"This Fleet Manager serves no {AgentReleaseCatalog.PrimaryRuntimeIdentifier} agent build yet, "
                + "so there is nothing to put in an image.");
        }

        progress?.Report("Verify the pinned base image");
        var mismatch = await Pin.VerifyAsync(BaseImagePath, cancellationToken).ConfigureAwait(false);
        if (mismatch is not null)
        {
            var missing = !File.Exists(BaseImagePath);
            logger.BaseImageRejected(BaseImagePath, mismatch);
            return Fail(
                missing ? ImageBuildResult.BaseImageMissing : ImageBuildResult.BaseImageMismatch,
                missing
                    ? $"{mismatch} Put it there with: {Pin.PreparationCommand}"
                    : mismatch);
        }

        if (!ImageGeometry.TryRead(BaseImagePath, out var geometry, out var geometryProblem))
        {
            return Fail(ImageBuildResult.GeometryUnreadable, geometryProblem);
        }

        if (DescribeGeometryDrift(geometry!) is { } drift)
        {
            // The digest already proved this is the pinned file, so reaching here means the pin's
            // own recorded layout is wrong — a pin updated with a new hash and stale offsets. That
            // is exactly the review failure the recorded geometry exists to catch, and it must be
            // caught here rather than by an operator holding a card.
            return Fail(ImageBuildResult.PinGeometryDrift, drift);
        }

        var required = Pin.ImageSizeBytes + options.ImageFreeSpaceSlackBytes;
        var available = storage.AvailableFreeSpaceBytes(options.ImageDirectory);
        if (available >= 0 && available < required)
        {
            return Fail(
                ImageBuildResult.InsufficientSpace,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Building needs {required} bytes free in {options.ImageDirectory} and there are "
                    + $"{available}. §3.1 budgets one volume for the Fleet Manager and does not account "
                    + $"for a base image plus a generated one; point FRAMELINK_IMAGE_DIR at a volume "
                    + $"with room for roughly three times the {Pin.ImageSizeBytes}-byte base image."));
        }

        var work = Path.Combine(options.ImageDirectory, WorkDirectoryName);
        var workingImage = Path.Combine(work, ImagePlan.WorkingImageName);

        try
        {
            progress?.Report("Take a working copy of the base image");
            ResetDirectory(work);
            File.Copy(BaseImagePath, workingImage, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Discard(work);
            return Fail(ImageBuildResult.WriteFailed, $"The working copy could not be made: {exception.Message}");
        }

        try
        {
            var plan = ImagePlan.Create(geometry!, seed, agentBinary, AgentUnitText.Read());

            foreach (var step in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(step.Description);

                var failure = await RunStepAsync(step, work, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    logger.ImageStepFailed(step.Description, failure.Problem ?? "");
                    Discard(work);
                    return failure;
                }
            }

            progress?.Report("Publish the checked image");

            // Only reachable once every step, e2fsck included, has passed. Move rather than copy:
            // the working directory is inside the image directory, so this is a rename on the same
            // volume and costs no space and no time. Overwriting replaces the previous artifact,
            // which is what keeps exactly one generated image on disk at a time.
            File.Move(workingImage, ArtifactPath, overwrite: true);

            var artifact = await DescribeAsync(seed, release.Version, cancellationToken).ConfigureAwait(false);
            Discard(work);

            logger.ImageBuilt(artifact.FileName, artifact.Sha256, seed.ControlUrl.AbsoluteUri);
            return new ImageBuildOutcome(ImageBuildResult.Succeeded, null, artifact);
        }
        catch (OperationCanceledException)
        {
            Discard(work);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Discard(work);
            return Fail(ImageBuildResult.WriteFailed, exception.Message);
        }
    }

    /// <summary>Whether the base image is present and matches the pin, and why not if it does not.</summary>
    public Task<string?> InspectBaseImageAsync(CancellationToken cancellationToken) =>
        Pin.VerifyAsync(BaseImagePath, cancellationToken);

    /// <summary>Reads back the artifact currently on disk, or null when there is none.</summary>
    public async Task<ImageArtifact?> ReadArtifactAsync(
        ImageSeed seed,
        string agentVersion,
        CancellationToken cancellationToken) =>
        File.Exists(ArtifactPath)
            ? await DescribeAsync(seed, agentVersion, cancellationToken).ConfigureAwait(false)
            : null;

    private string? DescribeGeometryDrift(ImageGeometry geometry)
    {
        var boot = geometry.Boot;
        var root = geometry.Root;

        if (boot is null || root is null)
        {
            return "The pinned image no longer has both a FAT boot partition and an ext4 root partition.";
        }

        if (boot.OffsetBytes == Pin.BootPartitionOffsetBytes
            && boot.LengthBytes == Pin.BootPartitionLengthBytes
            && root.OffsetBytes == Pin.RootPartitionOffsetBytes
            && root.LengthBytes == Pin.RootPartitionLengthBytes)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The pinned image's partitions are boot {boot.OffsetBytes}+{boot.LengthBytes} and root "
            + $"{root.OffsetBytes}+{root.LengthBytes}, but the pin records boot "
            + $"{Pin.BootPartitionOffsetBytes}+{Pin.BootPartitionLengthBytes} and root "
            + $"{Pin.RootPartitionOffsetBytes}+{Pin.RootPartitionLengthBytes}. The pin's digest and its "
            + $"recorded layout disagree, so one of them was updated without the other.");
    }

    private async Task<ImageBuildOutcome?> RunStepAsync(
        ImageStep step,
        string work,
        CancellationToken cancellationToken)
    {
        switch (step)
        {
            case StageTextStep text:
                await File.WriteAllTextAsync(
                        Path.Combine(work, text.FileName),
                        text.Content,
                        cancellationToken)
                    .ConfigureAwait(false);
                return null;

            case StageCopyStep copy:
                File.Copy(copy.SourcePath, Path.Combine(work, copy.FileName), overwrite: true);
                return null;

            case RunToolStep tool:
            {
                var result = await runner
                    .RunAsync(tool.Tool, tool.Arguments, work, cancellationToken)
                    .ConfigureAwait(false);

                var problem = ImageToolVerdict.Diagnose(tool.Tool, result);
                if (problem is null)
                {
                    return null;
                }

                return new ImageBuildOutcome(
                    tool.Tool is ImageTool.E2fsck ? ImageBuildResult.CheckFailed : ImageBuildResult.ToolFailed,
                    $"{step.Description}: {problem}",
                    null);
            }

            default:
                throw new InvalidOperationException($"Unknown image step '{step.GetType().Name}'.");
        }
    }

    private async Task<ImageArtifact> DescribeAsync(
        ImageSeed seed,
        string agentVersion,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(ArtifactPath);

        string digest;
        await using (var stream = new FileStream(
            ArtifactPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            digest = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        return new ImageArtifact
        {
            FileName = ArtifactFileName,
            SizeBytes = info.Length,
            Sha256 = digest,
            BuiltUtc = clock.GetUtcNow(),
            ControlUrl = seed.ControlUrl.AbsoluteUri,
            BaseRelease = Pin.Release,
            AgentVersion = agentVersion,
        };
    }

    private static ImageBuildOutcome Fail(ImageBuildResult result, string? problem) =>
        new(result, problem, null);

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void Discard(string work)
    {
        try
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stranded working directory costs disk and nothing else; the next build resets it.
            // Failing a completed build over cleanup would be the worse trade.
        }
    }
}
