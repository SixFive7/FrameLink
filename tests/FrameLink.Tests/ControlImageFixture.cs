using System.Buffers.Binary;
using System.Security.Cryptography;
using FrameLink.Control;
using FrameLink.Control.Imaging;
using FrameLink.Control.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameLink.Tests;

/// <summary>One recorded invocation of an external tool.</summary>
/// <param name="Tool">Which program.</param>
/// <param name="Arguments">Exactly what it was given.</param>
/// <param name="WorkingDirectory">Where it was run.</param>
internal sealed record ToolRun(ImageTool Tool, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>
/// Stands in for <c>debugfs</c>, <c>mcopy</c> and <c>e2fsck</c>, which do not exist here.
/// </summary>
/// <remarks>
/// <para>
/// It records every invocation, and — the part that earns its keep — it snapshots the text files
/// sitting in the working directory each time it is called. The staged seed file and unit are
/// written to disk by the builder before the first tool runs and deleted when the build ends, so
/// this is what lets a test assert on the <i>actual bytes that would have been written into the
/// image</i> rather than on the plan that described them.
/// </para>
/// <para>
/// It can also be told to refuse, in both of the two ways that matter: a non-zero exit, and the
/// nastier one — exit 0 with a diagnostic on the output, which is how <c>debugfs</c> really
/// reports failure.
/// </para>
/// </remarks>
internal sealed class RecordingImageToolRunner : IImageToolRunner
{
    /// <summary>Every invocation, in order.</summary>
    public List<ToolRun> Runs { get; } = [];

    /// <summary>Every distinct small text file seen staged in a working directory.</summary>
    public List<string> StagedText { get; } = [];

    /// <summary>A tool that should exit non-zero, or null.</summary>
    public ImageTool? FailTool { get; set; }

    /// <summary>Output <c>debugfs</c> should produce. Exit code stays 0, as it really does.</summary>
    public string DebugfsOutput { get; set; } = "debugfs 1.47.2 (1-Jan-2025)\nAllocated inode: 16\n";

    /// <summary>Held before every run, when set, so a test can observe a build in flight.</summary>
    public SemaphoreSlim? Block { get; set; }

    /// <inheritdoc/>
    public async Task<ImageToolResult> RunAsync(
        ImageTool tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (Block is not null)
        {
            await Block.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (Runs)
        {
            Runs.Add(new ToolRun(tool, [.. arguments], workingDirectory));

            foreach (var file in Directory.EnumerateFiles(workingDirectory))
            {
                // Skip the working image itself; everything else in there is a staged text file.
                if (new FileInfo(file).Length > 64 * 1024)
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                if (!StagedText.Contains(text, StringComparer.Ordinal))
                {
                    StagedText.Add(text);
                }
            }
        }

        if (FailTool == tool)
        {
            return new ImageToolResult(
                tool is ImageTool.E2fsck ? 4 : 1,
                "Pass 5: Checking group summary information\nFree blocks count wrong");
        }

        return tool switch
        {
            ImageTool.Debugfs => new ImageToolResult(0, DebugfsOutput),
            _ => new ImageToolResult(0, string.Empty),
        };
    }
}

/// <summary>Free space a test decides.</summary>
internal sealed class FakeStorageProbe : IStorageProbe
{
    /// <summary>What to report. Negative means "unknowable", as a real one does off-volume.</summary>
    public long FreeBytes { get; set; } = 64L * 1024 * 1024 * 1024;

    /// <inheritdoc/>
    public long AvailableFreeSpaceBytes(string path) => FreeBytes;
}

/// <summary>
/// A whole image generator standing on a 1 MiB synthetic base image.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the answer to "how is a 2.8 GB capability tested without a 2.8 GB fixture".</b> The
/// base image is 1 MiB of zeroes with a real master boot record describing a FAT partition and a
/// Linux one, and the pin is generated to match it — same code path, same length check, same
/// digest check, same geometry cross-check, same refusal messages, in a millisecond. What is
/// <i>not</i> exercised here is the three external tools' own behaviour, which is not something a
/// fixture could prove anyway: that was measured directly against the real
/// <c>2026-06-18-raspios-trixie-arm64-lite.img</c> and is recorded on
/// <see cref="ImageToolVerdict"/> and <see cref="ImagePlan"/>.
/// </para>
/// </remarks>
internal sealed class ImageFixture : IDisposable
{
    /// <summary>Offset of the synthetic FAT partition.</summary>
    public const long BootOffset = 64 * 1024;

    /// <summary>Length of the synthetic FAT partition.</summary>
    public const long BootLength = 256 * 1024;

    /// <summary>Offset of the synthetic Linux partition.</summary>
    public const long RootOffset = 384 * 1024;

    /// <summary>Length of the synthetic Linux partition.</summary>
    public const long RootLength = 640 * 1024;

    /// <summary>Total length of the synthetic image.</summary>
    public const long ImageLength = 1024 * 1024;

    private readonly TempWorkspace _workspace = new();

    /// <summary>Creates the fixture, optionally without an agent build to install.</summary>
    public ImageFixture(bool withAgentBinary = true)
    {
        Directory.CreateDirectory(ImageDirectory);

        if (withAgentBinary)
        {
            _workspace.WriteAgentBinary(
                AgentReleaseCatalog.PrimaryRuntimeIdentifier,
                "an agent binary that is not really an ELF",
                version: "0.1.0+test");
        }

        var basePath = Path.Combine(ImageDirectory, BaseImagePin.Current.ImageFileName);
        File.WriteAllBytes(basePath, SyntheticImageBytes());

        Rebuild(PinFor(basePath, BaseImagePin.Current.ImageFileName));
    }

    /// <summary>
    /// Secret classes a Fleet Manager really holds, none of which may reach a card.
    /// </summary>
    /// <remarks>
    /// Decision 17's forbidden list, made concrete. <see cref="ImageSeed"/> has no field capable of
    /// carrying any of them — which is the actual guarantee — and the assertion in
    /// <c>The_built_image_carries_no_secret_this_server_holds</c> is what keeps that true if the
    /// record ever widens. An image carrying any of these arrives pre-adopted, and a frame that
    /// arrives pre-adopted has skipped the one step where a person decides it belongs.
    /// </remarks>
    public static IReadOnlyList<string> SecretsThisServerHolds { get; } =
    [
        "a-long-operator-passphrase-for-the-fleet",
        "APIsecretLiveKitWouldMint0000000000",
        "KSMS-CQDX-HHCN-FE9S",
        "BEGIN EC PRIVATE KEY",
        "adoption-token",
    ];

    /// <summary>The builder under test.</summary>
    public ImageBuilder Builder { get; private set; } = null!;

    /// <summary>The pin it is honouring.</summary>
    public BaseImagePin Pin { get; private set; } = null!;

    /// <summary>The stand-in tools.</summary>
    public RecordingImageToolRunner Runner { get; } = new();

    /// <summary>The free-space reading.</summary>
    public FakeStorageProbe Storage { get; } = new();

    /// <summary>Where base images and artifacts live.</summary>
    public string ImageDirectory => Path.Combine(_workspace.Root, "images");

    /// <summary>Rebuilds the builder around a different pin.</summary>
    public void Rebuild(BaseImagePin pin)
    {
        Pin = pin;

        var options = new ControlOptions
        {
            DataDirectory = _workspace.Root,
            ReleaseDirectory = _workspace.ReleaseDirectory,
            ImageDirectory = ImageDirectory,
        };

        Builder = new ImageBuilder(
            options,
            new AgentReleaseCatalog(options, NullLogger<AgentReleaseCatalog>.Instance),
            Runner,
            Storage,
            TimeProvider.System,
            NullLogger<ImageBuilder>.Instance)
        {
            Pin = pin,
        };
    }

    /// <summary>Runs one build against a control URL.</summary>
    public Task<ImageBuildOutcome> BuildAsync(string controlUrl)
    {
        Assert.True(ImageSeed.TryCreate(controlUrl, null, out var seed, out var problem), problem);
        return Builder.BuildAsync(seed, progress: null, TestContext.Current.CancellationToken);
    }

    /// <summary>The bytes of the synthetic base image: an MBR and two empty partitions.</summary>
    public static byte[] SyntheticImageBytes()
    {
        var image = new byte[ImageLength];

        WriteEntry(image.AsSpan(446, 16), 0x0c, BootOffset, BootLength);
        WriteEntry(image.AsSpan(462, 16), 0x83, RootOffset, RootLength);

        image[510] = 0x55;
        image[511] = 0xAA;

        // A recognisable body, so a test that flips a byte is flipping something rather than a
        // zero, and so two synthetic images are not accidentally identical.
        for (var offset = 512; offset < image.Length; offset++)
        {
            image[offset] = (byte)(offset % 251);
        }

        return image;

        static void WriteEntry(Span<byte> entry, byte type, long offset, long length)
        {
            entry[4] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..12], (uint)(offset / ImageGeometry.SectorSize));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..16], (uint)(length / ImageGeometry.SectorSize));
        }
    }

    /// <summary>A pin that exactly describes the synthetic image at <paramref name="path"/>.</summary>
    public static BaseImagePin PinFor(string path, string fileName)
    {
        using var stream = File.OpenRead(path);

        return BaseImagePin.Current with
        {
            ImageFileName = fileName,
            ImageSha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
            ImageSizeBytes = new FileInfo(path).Length,
            BootPartitionOffsetBytes = BootOffset,
            BootPartitionLengthBytes = BootLength,
            RootPartitionOffsetBytes = RootOffset,
            RootPartitionLengthBytes = RootLength,
        };
    }

    /// <inheritdoc/>
    public void Dispose() => _workspace.Dispose();
}
