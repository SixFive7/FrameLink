using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FrameLink.Agent.Discovery;
using FrameLink.Agent.Systemd;
using FrameLink.Control;
using FrameLink.Control.Imaging;
using FrameLink.Control.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameLink.Tests;

/// <summary>
/// SD image generation (§3.9) — the geometry, the pin, the plan and the gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no 2.8 GB fixture in this repository and there must never be one.</b> Everything
/// here runs against a 1 MiB synthetic image with a real MBR, and against a recording stand-in
/// for the three external tools. That is not a compromise: <c>debugfs</c>, <c>mcopy</c> and
/// <c>e2fsck</c> do not exist on the workstation this suite runs on, and what is worth asserting
/// about the generator is on this side of them anyway — which commands it issues, in which order,
/// with which arguments, what it refuses to do, and what it does with what comes back.
/// </para>
/// <para>
/// The strings fed to <see cref="ImageToolVerdict"/> below are not invented. Every one was
/// captured from e2fsprogs 1.47.2 in a plain <c>debian:trixie-slim</c> container on 2026-08-15,
/// including the exit codes, which is what makes the "trust the output, not the exit code" rule
/// a measurement rather than a precaution.
/// </para>
/// </remarks>
public sealed class ControlImageGenerationTests
{
    private const string ControlUrl = "https://framelink.example/";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ── Geometry ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_real_images_partition_table_is_read_as_the_offsets_the_tools_are_aimed_by()
    {
        // The sector numbers `fdisk -l` printed for 2026-06-18-raspios-trixie-arm64-lite.img on
        // 2026-08-15. Reproducing them in a synthetic table means the arithmetic that produces
        // `image@@8388608` and `image?offset=545259520` is asserted against the real layout
        // without the real file.
        var mbr = BuildMasterBootRecord(
            (0x0c, 16_384u, 1_048_576u),
            (0x83, 1_064_960u, 4_751_360u));

        Assert.True(ImageGeometry.TryRead(mbr, 2_977_955_840, out var geometry, out var problem));
        Assert.Null(problem);

        var pin = BaseImagePin.Current;
        Assert.Equal(pin.BootPartitionOffsetBytes, geometry!.Boot!.OffsetBytes);
        Assert.Equal(pin.BootPartitionLengthBytes, geometry.Boot.LengthBytes);
        Assert.Equal(pin.RootPartitionOffsetBytes, geometry.Root!.OffsetBytes);
        Assert.Equal(pin.RootPartitionLengthBytes, geometry.Root.LengthBytes);
    }

    [Fact]
    public void Bytes_with_no_boot_signature_are_not_a_disk_image()
    {
        var mbr = new byte[ImageGeometry.MasterBootRecordSize];

        Assert.False(ImageGeometry.TryRead(mbr, 1024 * 1024, out _, out var problem));
        Assert.Contains("signature", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_partition_that_runs_past_the_end_of_the_file_is_refused()
    {
        // The shape a truncated download has, and the one case where handing the offset on would
        // mean debugfs reading a superblock out of nothing at all.
        var mbr = BuildMasterBootRecord((0x83, 1_064_960u, 4_751_360u));

        Assert.False(ImageGeometry.TryRead(mbr, 600_000_000, out _, out var problem));
        Assert.Contains("does not describe this file", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signed_but_empty_table_is_refused()
    {
        var mbr = BuildMasterBootRecord();

        Assert.False(ImageGeometry.TryRead(mbr, 1024 * 1024, out _, out var problem));
        Assert.Contains("no partitions", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_with_only_one_kind_of_partition_yields_no_plan()
    {
        var mbr = BuildMasterBootRecord((0x0c, 16_384u, 1_024u));

        Assert.True(ImageGeometry.TryRead(mbr, 32 * 1024 * 1024, out var geometry, out _));
        Assert.NotNull(geometry!.Boot);
        Assert.Null(geometry.Root);
    }

    // ── The pin ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_pinned_partitions_are_sector_aligned_and_inside_the_pinned_length()
    {
        // Not circular: this checks the recorded numbers against each other and against the
        // recorded file length, which is what catches a pin edited in one place and not another.
        var pin = BaseImagePin.Current;

        Assert.Equal(0, pin.BootPartitionOffsetBytes % ImageGeometry.SectorSize);
        Assert.Equal(0, pin.RootPartitionOffsetBytes % ImageGeometry.SectorSize);
        Assert.True(pin.BootPartitionOffsetBytes + pin.BootPartitionLengthBytes <= pin.RootPartitionOffsetBytes);
        Assert.True(pin.RootPartitionOffsetBytes + pin.RootPartitionLengthBytes <= pin.ImageSizeBytes);
        Assert.Equal(64, pin.ImageSha256.Length);
        Assert.Equal(64, pin.ArchiveSha256.Length);
    }

    [Fact]
    public async Task A_base_image_of_the_wrong_length_is_rejected_without_being_hashed()
    {
        using var workspace = new TempWorkspace();
        var pin = SyntheticPin(workspace, out var path);

        await File.WriteAllBytesAsync(path, new byte[16], Token);

        var problem = await pin.VerifyAsync(path, Token);
        Assert.Contains("truncated, or it is a different release", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_base_image_whose_bytes_are_not_the_pinned_bytes_is_rejected()
    {
        using var workspace = new TempWorkspace();
        var pin = SyntheticPin(workspace, out var path);

        // Same length, one byte different — the case a length check alone would wave through, and
        // exactly what a corrupted decompression or a substituted mirror looks like.
        var bytes = await File.ReadAllBytesAsync(path, Token);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes, Token);

        var problem = await pin.VerifyAsync(path, Token);
        Assert.Contains("Nothing has been written to it", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_base_image_that_matches_the_pin_verifies()
    {
        using var workspace = new TempWorkspace();
        var pin = SyntheticPin(workspace, out var path);

        Assert.Null(await pin.VerifyAsync(path, Token));
    }

    [Fact]
    public void A_missing_base_image_is_reported_without_touching_the_disk_twice()
    {
        using var workspace = new TempWorkspace();
        var pin = BaseImagePin.Current;

        var problem = pin.InspectWithoutHashing(Path.Combine(workspace.Root, pin.ImageFileName));

        Assert.Contains(pin.ImageFileName, problem, StringComparison.Ordinal);
        Assert.Contains("curl", pin.PreparationCommand, StringComparison.Ordinal);
        Assert.Contains(pin.ArchiveSha256, pin.PreparationCommand, StringComparison.Ordinal);
    }

    // ── The seed, and decision 17 ───────────────────────────────────────────────────────────

    [Fact]
    public void The_seed_keys_are_the_ones_the_agent_actually_reads()
    {
        // FrameLink.Control cannot reference FrameLink.Agent — two separately published binaries
        // that meet only at FrameLink.Protocol — and a boot-partition file format is not a wire
        // type, so it does not belong there either. The constants are therefore duplicated, and
        // this is what keeps them equal. Edit one, edit both.
        Assert.Equal(BootFileEndpointSource.ControlUrlKey, ImageSeed.ControlUrlKey);
        Assert.Equal(BootFileEndpointSource.LanUrlKey, ImageSeed.LanUrlKey);
        Assert.Contains(
            "/boot/firmware/" + ImageSeed.BootFileName,
            BootFileEndpointSource.DefaultPaths);
    }

    [Fact]
    public async Task The_seed_file_parses_back_through_the_agents_own_boot_file_source()
    {
        Assert.True(ImageSeed.TryCreate(ControlUrl, "http://192.168.1.50:8080/", out var seed, out _));

        var files = new MemoryTextFiles();
        files.Files["/boot/firmware/framelink.conf"] = seed.RenderBootFile();

        var found = await new BootFileEndpointSource(files).DiscoverAsync(Token);

        // The whole point of the generated file: a frame flashed from this image finds its Fleet
        // Manager on first boot with nobody logged in. Asserting the rendered text against the
        // real parser is the only assertion that proves it, since the writer and the reader ship
        // in different binaries.
        Assert.Equal(2, found.Count);
        Assert.Equal(ControlUrl, found[0].AbsoluteUri);
        Assert.Equal("http://192.168.1.50:8080/", found[1].AbsoluteUri);
    }

    [Theory]
    [InlineData("https://token@framelink.example/", "credential")]
    [InlineData("https://user:secret@framelink.example/", "credential")]
    [InlineData("https://framelink.example/?adopt=abc123", "smuggled")]
    [InlineData("https://framelink.example/#token=abc123", "smuggled")]
    public void A_control_url_that_could_carry_a_credential_is_refused(string url, string expected)
    {
        // Decision 17: "generic image, no secrets". ImageSeed has no field for a token, so the
        // only way one reaches a card is inside the URL — which is why these are refusals rather
        // than values that get quietly stripped.
        Assert.False(ImageSeed.TryCreate(url, null, out _, out var problem));
        Assert.Contains(expected, problem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decision 17", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://framelink.example/\ncontrol-lan-url=http://evil.example/")]
    [InlineData("https://framelink.example/\rmore=1")]
    public void A_newline_in_the_url_cannot_append_a_line_to_the_seed_file(string url)
    {
        // The seed is a key=value file. A newline inside a value does not corrupt it, it appends
        // to it — which is an injection of further keys into a file the agent trusts on first
        // boot, before adoption, before anything.
        Assert.False(ImageSeed.TryCreate(url, null, out _, out var problem));
        Assert.Contains("control character", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://framelink.example/")]
    [InlineData("framelink.example")]
    [InlineData("")]
    [InlineData("   ")]
    public void Only_an_absolute_http_or_https_url_is_a_control_url(string url)
    {
        Assert.False(ImageSeed.TryCreate(url, null, out _, out _));
    }

    [Fact]
    public void The_lan_address_is_optional_and_held_to_the_same_rules()
    {
        Assert.True(ImageSeed.TryCreate(ControlUrl, null, out var seed, out _));
        Assert.Null(seed.LanUrl);
        Assert.DoesNotContain(ImageSeed.LanUrlKey, seed.RenderBootFile(), StringComparison.Ordinal);

        Assert.False(ImageSeed.TryCreate(ControlUrl, "https://token@lan.example/", out _, out var problem));
        Assert.Contains("LAN address", problem, StringComparison.Ordinal);
    }

    // ── The plan ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_plan_installs_the_agent_as_a_root_owned_executable()
    {
        var plan = SamplePlan();

        // 0755 root:root, and all three requests are load-bearing: debugfs stamps a new inode with
        // the CALLER's uid, so on a container that does not run as root the binary would otherwise
        // land owned by the server's user — a file that user could rewrite and that systemd starts
        // as root.
        AssertDebugfsRequest(plan, $"write fl-agent {ImagePlan.AgentBinaryPath}");
        AssertDebugfsRequest(plan, $"sif {ImagePlan.AgentBinaryPath} mode 0100755");
        AssertDebugfsRequest(plan, $"sif {ImagePlan.AgentBinaryPath} uid 0");
        AssertDebugfsRequest(plan, $"sif {ImagePlan.AgentBinaryPath} gid 0");
    }

    [Fact]
    public void The_plan_enables_the_agent_with_the_symlink_systemctl_would_have_made()
    {
        var plan = SamplePlan();

        AssertDebugfsRequest(plan, $"write fl-agent.service {ImagePlan.UnitPath}");
        AssertDebugfsRequest(plan, $"sif {ImagePlan.UnitPath} mode 0100644");

        // Enablement is not a database; it is this symlink, beside the stock userconfig.service.
        AssertDebugfsRequest(plan, $"symlink {ImagePlan.WantsLinkPath} {ImagePlan.UnitPath}");
        Assert.StartsWith("/etc/systemd/system/multi-user.target.wants/", ImagePlan.WantsLinkPath, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plan_never_creates_a_directory()
    {
        // Measured 2026-08-15: `debugfs mkdir` on a directory that already exists allocates and
        // initialises the inode before noticing the name is taken, then abandons it — leaving a
        // filesystem e2fsck calls corrupt ("Unconnected directory inode 14 (was in /)", exit 4)
        // while debugfs itself exits 0. Every directory this plan writes into exists in the
        // pinned base image, which was confirmed against the real file, and the pin is what makes
        // depending on that safe. This test is the guard on that reasoning surviving contact with
        // a future edit.
        foreach (var step in SamplePlan().OfType<RunToolStep>())
        {
            Assert.DoesNotContain("mkdir", string.Join(' ', step.Arguments), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_plan_ends_with_a_read_only_filesystem_check()
    {
        var plan = SamplePlan();
        var last = Assert.IsType<RunToolStep>(plan[^1]);

        Assert.Equal(ImageTool.E2fsck, last.Tool);

        // -n answers "no" to every repair question, so the gate cannot itself modify the image it
        // is judging.
        Assert.Equal("-fn", last.Arguments[0]);
        Assert.Single(plan.OfType<RunToolStep>(), s => s.Tool is ImageTool.E2fsck);
    }

    [Fact]
    public void The_two_partition_syntaxes_carry_the_offsets_read_from_the_image()
    {
        var plan = SamplePlan();

        var mcopy = plan.OfType<RunToolStep>().Single(s => s.Tool is ImageTool.Mcopy);
        Assert.Contains($"{ImagePlan.WorkingImageName}@@{BaseImagePin.Current.BootPartitionOffsetBytes}", mcopy.Arguments);

        var ext = $"{ImagePlan.WorkingImageName}?offset={BaseImagePin.Current.RootPartitionOffsetBytes}";
        foreach (var step in plan.OfType<RunToolStep>().Where(s => s.Tool is not ImageTool.Mcopy))
        {
            Assert.Equal(ext, step.Arguments[^1]);
        }
    }

    [Fact]
    public void Nothing_an_operator_typed_ever_reaches_a_tool_argument()
    {
        const string Distinctive = "zzq-operator-typed-this";
        Assert.True(ImageSeed.TryCreate($"https://{Distinctive}.example/", null, out var seed, out _));

        var plan = ImagePlan.Create(SampleGeometry(), seed, "/srv/agent/fl-agent", "unit text");

        // It reaches the card as the CONTENT of a staged file, which mcopy reads off disk. That
        // is what makes ProcessImageToolRunner's total absence of quoting safe rather than lucky:
        // there is no shell, and there is no operator-controlled argument either.
        Assert.Contains(
            plan.OfType<StageTextStep>(),
            step => step.Content.Contains(Distinctive, StringComparison.Ordinal));

        foreach (var step in plan.OfType<RunToolStep>())
        {
            Assert.DoesNotContain(Distinctive, string.Join(' ', step.Arguments), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_generated_image_carries_the_same_unit_the_agent_installs()
    {
        // Not a copy — FrameLink.Control.csproj embeds ../FrameLink.Agent/Systemd/fl-agent.service
        // itself, so these are the same bytes on disk. What this catches is somebody replacing
        // that csproj item with a local copy, which is the only way drift could return.
        Assert.Equal(UnitInstaller.ReadUnit(), AgentUnitText.Read());
        Assert.Contains($"ExecStart={ImagePlan.AgentBinaryPath} run", AgentUnitText.Read(), StringComparison.Ordinal);
        Assert.Contains("WantedBy=multi-user.target", AgentUnitText.Read(), StringComparison.Ordinal);
    }

    // ── The verdict on each tool, from measured output ───────────────────────────────────────

    [Theory]
    [InlineData("debugfs 1.47.2 (1-Jan-2025)\n/nope/deeper: File not found by ext2_lookup while looking up \"/nope/deeper\"\nwrite: File not found by ext2_lookup\n")]
    [InlineData("debugfs 1.47.2 (1-Jan-2025)\nwrite: Ext2 file already exists\n")]
    [InlineData("debugfs 1.47.2 (1-Jan-2025)\nwrite: Filesystem opened read/only\n")]
    [InlineData("debugfs 1.47.2 (1-Jan-2025)\n/nope: File not found by ext2_lookup \nsymlink: File not found by ext2_lookup\n")]
    [InlineData("debugfs 1.47.2 (1-Jan-2025)\next2fs_mkdir: Ext2 directory already exists while creating directory \"bin\"\nmkdir: Ext2 directory already exists\n")]
    [InlineData("debugfs 1.47.2 (1-Jan-2025)\ndebugfs: Attempt to read block from filesystem resulted in short read while trying to open x\nls: Filesystem not open\n")]
    public void A_debugfs_failure_is_caught_from_its_output_because_it_exits_zero(string output)
    {
        // Every one of these was captured with exit code 0. A generator that trusted the exit code
        // would write the seed file, silently fail to install the agent, pass its own checks, and
        // hand somebody a card that boots into stock Raspberry Pi OS.
        Assert.NotNull(ImageToolVerdict.Diagnose(ImageTool.Debugfs, new ImageToolResult(0, output)));
    }

    [Theory]
    [InlineData("debugfs 1.47.2 (1-Jan-2025)\nAllocated inode: 16\n")]
    [InlineData("debugfs 1.47.2 (1-Jan-2025)\n")]
    [InlineData("debugfs 1.47.2 (1-Jan-2025)\n\n")]
    public void A_successful_debugfs_request_is_not_mistaken_for_a_failure(string output)
    {
        Assert.Null(ImageToolVerdict.Diagnose(ImageTool.Debugfs, new ImageToolResult(0, output)));
    }

    [Fact]
    public void An_unrecognised_debugfs_message_is_treated_as_a_failure()
    {
        // A whitelist, not a blacklist. A message a future e2fsprogs invents becomes a refused
        // build rather than a silently incomplete image, which is the safe direction to be wrong.
        var verdict = ImageToolVerdict.Diagnose(
            ImageTool.Debugfs,
            new ImageToolResult(0, "debugfs 1.99.0 (1-Jan-2030)\nSomething entirely new happened\n"));

        Assert.Contains("Something entirely new happened", verdict, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, false)]
    [InlineData(8, false)]
    public void The_filesystem_check_must_exit_zero_exactly(int exitCode, bool clean)
    {
        // 4 is "errors left uncorrected", which is what -n reports on a filesystem debugfs damaged;
        // 8 is an operational error such as an unreadable superblock. Neither is a card anyone
        // should be handed, so the gate is 0 and not "less than 8".
        var verdict = ImageToolVerdict.Diagnose(ImageTool.E2fsck, new ImageToolResult(exitCode, "…"));
        Assert.Equal(clean, verdict is null);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Mtools_is_judged_by_its_exit_code_because_mtools_is_honest(int exitCode, bool ok)
    {
        var verdict = ImageToolVerdict.Diagnose(ImageTool.Mcopy, new ImageToolResult(exitCode, "init :: non DOS media"));
        Assert.Equal(ok, verdict is null);
    }

    // ── The builder ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_successful_build_publishes_exactly_one_checked_artifact()
    {
        using var fixture = new ImageFixture();

        var outcome = await fixture.BuildAsync(ControlUrl);

        Assert.Equal(ImageBuildResult.Succeeded, outcome.Result);
        Assert.NotNull(outcome.Artifact);
        Assert.Equal(ImageBuilder.ArtifactFileName, outcome.Artifact.FileName);
        Assert.Equal(ControlUrl, outcome.Artifact.ControlUrl);
        Assert.Equal(BaseImagePin.Current.Release, outcome.Artifact.BaseRelease);
        Assert.True(File.Exists(fixture.Builder.ArtifactPath));

        // The base image and the artifact, and nothing else: no working directory, no second
        // generated image. §3.1's volume is sized for a database, and every stray copy is 2.8 GB
        // of it in production.
        Assert.False(Directory.Exists(Path.Combine(fixture.Builder.ImageDirectory, ImageBuilder.WorkDirectoryName)));
        Assert.Equal(
            new[] { ImageBuilder.ArtifactFileName, fixture.Pin.ImageFileName }.Order(StringComparer.Ordinal),
            Directory.GetFiles(fixture.Builder.ImageDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(await File.ReadAllBytesAsync(fixture.Builder.ArtifactPath, Token)));
        Assert.Equal(digest, outcome.Artifact.Sha256);
    }

    [Fact]
    public async Task An_image_is_never_offered_when_the_filesystem_check_fails()
    {
        using var fixture = new ImageFixture();
        fixture.Runner.FailTool = ImageTool.E2fsck;

        var outcome = await fixture.BuildAsync(ControlUrl);

        // The failure mode this whole design is aimed at: a corrupt image that reaches a card
        // costs a person a trip. So the artifact filename is unreachable except through e2fsck.
        Assert.Equal(ImageBuildResult.CheckFailed, outcome.Result);
        Assert.Null(outcome.Artifact);
        Assert.False(File.Exists(fixture.Builder.ArtifactPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Builder.ImageDirectory, ImageBuilder.WorkDirectoryName)));
    }

    [Fact]
    public async Task A_refused_step_stops_the_build_and_publishes_nothing()
    {
        using var fixture = new ImageFixture();

        // Exit 0 with a diagnostic — the exact shape that would otherwise sail through.
        fixture.Runner.DebugfsOutput = "debugfs 1.47.2 (1-Jan-2025)\nwrite: Ext2 file already exists\n";

        var outcome = await fixture.BuildAsync(ControlUrl);

        Assert.Equal(ImageBuildResult.ToolFailed, outcome.Result);
        Assert.Contains("Ext2 file already exists", outcome.Problem, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Builder.ArtifactPath));

        // Stopped at the first refusal rather than carrying on: e2fsck must never be reached on a
        // build already known to be wrong, or a passing check would look like a passing build.
        Assert.DoesNotContain(fixture.Runner.Runs, run => run.Tool is ImageTool.E2fsck);
    }

    [Fact]
    public async Task A_base_image_that_fails_verification_is_never_touched()
    {
        using var fixture = new ImageFixture();

        var bytes = await File.ReadAllBytesAsync(fixture.Builder.BaseImagePath, Token);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(fixture.Builder.BaseImagePath, bytes, Token);

        var outcome = await fixture.BuildAsync(ControlUrl);

        Assert.Equal(ImageBuildResult.BaseImageMismatch, outcome.Result);

        // "Before touching the image" is the claim; zero tool runs and no working copy is what
        // makes it a fact.
        Assert.Empty(fixture.Runner.Runs);
        Assert.False(Directory.Exists(Path.Combine(fixture.Builder.ImageDirectory, ImageBuilder.WorkDirectoryName)));
    }

    [Fact]
    public async Task A_missing_base_image_says_exactly_how_to_get_it()
    {
        using var fixture = new ImageFixture();
        File.Delete(fixture.Builder.BaseImagePath);

        var outcome = await fixture.BuildAsync(ControlUrl);

        Assert.Equal(ImageBuildResult.BaseImageMissing, outcome.Result);
        Assert.Contains(fixture.Pin.PreparationCommand, outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(fixture.Runner.Runs);
    }

    [Fact]
    public async Task A_full_disk_is_refused_before_anything_is_copied()
    {
        using var fixture = new ImageFixture();

        // §3.1 budgets one container and one volume and does not account for a base image plus a
        // generated one. That is a real unbudgeted requirement, so it is a sentence an operator
        // reads rather than a half-written image and a database that can no longer write.
        fixture.Storage.FreeBytes = 1024;

        var outcome = await fixture.BuildAsync(ControlUrl);

        Assert.Equal(ImageBuildResult.InsufficientSpace, outcome.Result);
        Assert.Contains("FRAMELINK_IMAGE_DIR", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(fixture.Runner.Runs);
    }

    [Fact]
    public async Task An_unknowable_free_space_reading_does_not_block_a_build()
    {
        using var fixture = new ImageFixture();
        fixture.Storage.FreeBytes = -1;

        // A filesystem DriveInfo cannot describe is not a full one, and the copy fails loudly on a
        // genuinely full disk anyway. Refusing here would break the container case to guard the
        // desktop one.
        Assert.Equal(ImageBuildResult.Succeeded, (await fixture.BuildAsync(ControlUrl)).Result);
    }

    [Fact]
    public async Task Without_an_agent_binary_there_is_nothing_worth_building()
    {
        using var fixture = new ImageFixture(withAgentBinary: false);

        var outcome = await fixture.BuildAsync(ControlUrl);

        Assert.Equal(ImageBuildResult.NoAgentBinary, outcome.Result);
        Assert.Empty(fixture.Runner.Runs);
    }

    [Fact]
    public async Task A_pin_whose_recorded_layout_disagrees_with_the_image_is_refused()
    {
        using var fixture = new ImageFixture();

        // The digest already proved this is the pinned file, so reaching this state means the pin
        // itself was edited with a new hash and stale offsets — the review failure the recorded
        // geometry exists to catch, caught here rather than by somebody holding a card.
        fixture.Rebuild(fixture.Pin with { RootPartitionOffsetBytes = 4096 });

        var outcome = await fixture.BuildAsync(ControlUrl);

        Assert.Equal(ImageBuildResult.PinGeometryDrift, outcome.Result);
        Assert.Contains("updated without the other", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(fixture.Runner.Runs);
    }

    [Fact]
    public async Task A_failed_build_leaves_the_previous_image_flashable()
    {
        using var fixture = new ImageFixture();

        Assert.Equal(ImageBuildResult.Succeeded, (await fixture.BuildAsync(ControlUrl)).Result);
        var good = await File.ReadAllBytesAsync(fixture.Builder.ArtifactPath, Token);

        fixture.Runner.FailTool = ImageTool.E2fsck;
        Assert.Equal(ImageBuildResult.CheckFailed, (await fixture.BuildAsync("https://other.example/")).Result);

        // Publishing is a rename that happens only after the check passes, so a later failure
        // cannot damage or replace an image the operator was about to flash.
        Assert.Equal(good, await File.ReadAllBytesAsync(fixture.Builder.ArtifactPath, Token));
    }

    [Fact]
    public async Task The_built_image_carries_no_secret_this_server_holds()
    {
        using var fixture = new ImageFixture();

        Assert.Equal(ImageBuildResult.Succeeded, (await fixture.BuildAsync(ControlUrl)).Result);

        // Decision 17 as an assertion rather than an intention. Everything the generator staged
        // or passed to a tool is searched for the classes of secret a Fleet Manager actually holds
        // — the operator password, the LiveKit secret, a device fingerprint, a private key. An
        // image carrying any of them would arrive pre-adopted, which is not a shortcut through
        // enrollment but the destruction of it.
        var everything = new StringBuilder();
        foreach (var content in fixture.Runner.StagedText)
        {
            everything.Append(content);
        }

        foreach (var run in fixture.Runner.Runs)
        {
            everything.Append(string.Join(' ', run.Arguments));
        }

        foreach (var secret in ImageFixture.SecretsThisServerHolds)
        {
            Assert.DoesNotContain(secret, everything.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        // And the seed file positively contains only the two keys it is allowed to.
        var seedFile = fixture.Runner.StagedText.Single(text => text.Contains(ImageSeed.ControlUrlKey, StringComparison.Ordinal));
        var keys = seedFile
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line[0] is not '#')
            .Select(line => line[..line.IndexOf('=', StringComparison.Ordinal)])
            .ToArray();

        Assert.Equal([ImageSeed.ControlUrlKey], keys);
    }

    // ── The routes ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_image_routes_are_behind_the_operator_password()
    {
        await using var server = await ControlServer.StartAsync("a-long-operator-passphrase-for-the-fleet");

        foreach (var route in new[] { "/api/image", "/api/image/artifact" })
        {
            var response = await server.Client.GetAsync(route, Token);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var started = await server.Client.PostAsync("/api/image", content: null, Token);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, started.StatusCode);
    }

    [Fact]
    public async Task The_status_route_names_the_pin_and_what_is_missing()
    {
        const string Password = "a-long-operator-passphrase-for-the-fleet";
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        var response = await server.Client.GetAsync("/api/image", Token);
        var status = await response.ReadAsync(ControlJson.Default.ImageStatusResponse);

        Assert.Equal("Idle", status.State);
        Assert.False(status.ArtifactAvailable);
        Assert.Equal(BaseImagePin.Current.Release, status.Base.Release);
        Assert.Equal(BaseImagePin.Current.ImageSha256, status.Base.ImageSha256);
        Assert.Equal(BaseImagePin.Current.ArchiveSha256, status.Base.ArchiveSha256);

        // §7.1 asks for a reviewable pin. An operator with no base image on disk is told the
        // filename, the digest and the one command that produces it.
        Assert.Contains(BaseImagePin.Current.ImageFileName, status.Base.Problem, StringComparison.Ordinal);
        Assert.Contains(BaseImagePin.Current.ArchiveSha256, status.Base.PreparationCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_control_url_that_would_carry_a_credential_is_refused_by_the_route()
    {
        const string Password = "a-long-operator-passphrase-for-the-fleet";
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        var response = await server.Client.PostAsJsonAsync(
            "/api/image",
            new ImageRequest { ControlUrl = "https://token@framelink.example/" },
            ControlJson.Default.ImageRequest,
            Token);

        var error = await response.ReadAsync(ControlJson.Default.ApiError);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad-request", error.Error);
        Assert.Contains("decision 17", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task There_is_nothing_to_download_before_an_image_is_built()
    {
        const string Password = "a-long-operator-passphrase-for-the-fleet";
        await using var server = await ControlServer.StartAsync(Password);
        await server.SignInAsync(Password);

        var response = await server.Client.GetAsync("/api/image/artifact", Token);
        var error = await response.ReadAsync(ControlJson.Default.ApiError);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-image", error.Error);
    }

    [Fact]
    public async Task Only_one_build_runs_at_a_time()
    {
        using var fixture = new ImageFixture();
        using var block = new SemaphoreSlim(0);
        fixture.Runner.Block = block;

        using var service = new ImageBuildService(fixture.Builder, TimeProvider.System, NullLogger<ImageBuildService>.Instance);

        Assert.True(ImageSeed.TryCreate(ControlUrl, null, out var seed, out _));
        Assert.True(service.TryStart(seed, out _));

        // Two at once is two 2.8 GB working copies on a volume §3.1 sized for a SQLite file, so a
        // second request is answered rather than queued.
        Assert.False(service.TryStart(seed, out var refusal));
        Assert.Contains("already being built", refusal, StringComparison.Ordinal);
        Assert.Equal(ImageBuildState.Running, service.Status.State);

        block.Release(int.MaxValue / 2);
        await service.Completion;

        Assert.Equal(ImageBuildState.Succeeded, service.Status.State);
    }

    [Fact]
    public async Task A_build_reports_where_it_got_to_and_what_it_produced()
    {
        using var fixture = new ImageFixture();
        using var service = new ImageBuildService(fixture.Builder, TimeProvider.System, NullLogger<ImageBuildService>.Instance);

        Assert.True(ImageSeed.TryCreate(ControlUrl, null, out var seed, out _));
        Assert.True(service.TryStart(seed, out _));
        await service.Completion;

        var status = service.Status;
        Assert.Equal(ImageBuildState.Succeeded, status.State);
        Assert.Equal(nameof(ImageBuildResult.Succeeded), status.Result);
        Assert.NotNull(status.Artifact);
        Assert.NotNull(status.StartedUtc);
        Assert.NotNull(status.CompletedUtc);
    }

    [Fact]
    public async Task A_failed_build_reports_why_without_taking_the_server_down()
    {
        using var fixture = new ImageFixture();
        fixture.Runner.FailTool = ImageTool.E2fsck;

        using var service = new ImageBuildService(fixture.Builder, TimeProvider.System, NullLogger<ImageBuildService>.Instance);

        Assert.True(ImageSeed.TryCreate(ControlUrl, null, out var seed, out _));
        Assert.True(service.TryStart(seed, out _));
        await service.Completion;

        Assert.Equal(ImageBuildState.Failed, service.Status.State);
        Assert.Equal(nameof(ImageBuildResult.CheckFailed), service.Status.Result);
        Assert.NotNull(service.Status.Problem);

        // And the slot is free again, so the operator can fix the cause and press the button.
        Assert.True(service.TryStart(seed, out _));
        await service.Completion;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ImageStep> SamplePlan()
    {
        Assert.True(ImageSeed.TryCreate(ControlUrl, null, out var seed, out _));
        return ImagePlan.Create(SampleGeometry(), seed, "/srv/agent/fl-agent", "unit text");
    }

    /// <summary>The real image's layout, from its own partition table.</summary>
    private static ImageGeometry SampleGeometry()
    {
        var mbr = BuildMasterBootRecord(
            (0x0c, 16_384u, 1_048_576u),
            (0x83, 1_064_960u, 4_751_360u));

        Assert.True(ImageGeometry.TryRead(mbr, 2_977_955_840, out var geometry, out _));
        return geometry!;
    }

    private static void AssertDebugfsRequest(IReadOnlyList<ImageStep> plan, string request)
    {
        Assert.Contains(
            plan.OfType<RunToolStep>(),
            step => step.Tool is ImageTool.Debugfs
                && step.Arguments.Contains("-w")
                && step.Arguments.Contains("-R")
                && step.Arguments.Contains(request));
    }

    /// <summary>Builds a 512-byte MBR with the given primary partitions.</summary>
    private static byte[] BuildMasterBootRecord(params (byte Type, uint FirstSector, uint SectorCount)[] partitions)
    {
        var mbr = new byte[ImageGeometry.MasterBootRecordSize];

        for (var index = 0; index < partitions.Length; index++)
        {
            var entry = mbr.AsSpan(446 + (index * 16), 16);
            entry[4] = partitions[index].Type;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..12], partitions[index].FirstSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..16], partitions[index].SectorCount);
        }

        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        return mbr;
    }

    /// <summary>Writes a small partitioned image and returns a pin that matches it exactly.</summary>
    private static BaseImagePin SyntheticPin(TempWorkspace workspace, out string path)
    {
        path = Path.Combine(workspace.Root, "synthetic.img");
        File.WriteAllBytes(path, ImageFixture.SyntheticImageBytes());

        return ImageFixture.PinFor(path, "synthetic.img");
    }
}
