using System.Security.Cryptography;
using FrameLink.Agent.Firmware;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// The firmware the agent carries <b>inside itself</b>, and the offline install that needs it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first test here is the one that matters, and it is the only check in the whole chain that
/// is not a re-reading of something already read.</b> Everything downstream — the digest on the way
/// to the card, the re-hash on every observe, the re-hash in the instant before <c>dfu-util</c>
/// starts — asks whether a file on an SD card is still the pinned bytes. None of them can catch the
/// failure that actually threatens an embedded image: the <i>wrong bytes being embedded in the
/// first place</i>, by a glob that widened, a vendored file somebody replaced, or a pin that moved
/// without the vendored copy moving with it. On a frame that failure looks exactly like a healthy
/// frame right up until the array is written. It is catchable only here, at build time, against the
/// pin in source — so this file, not the flash, is where that guarantee lives.
/// </para>
/// <para>
/// <b>These tests read the real 933 KB image out of the real assembly.</b> Deliberately, and
/// against the house habit of synthetic fixtures: a synthetic image would assert the plumbing while
/// leaving the one claim that cannot be re-derived on a frame — that this binary contains the file
/// the pin names — untested. The cost is under a second and a megabyte of temp.
/// </para>
/// <para>
/// <b>They also happen to be the round-trip test for the compression.</b> The resource is stored
/// gzipped and the vendored file is not, so hashing what comes out of
/// <see cref="XvfVendoredFirmware.Open"/> against the pin asserts the build-time compressor and the
/// run-time decompressor agree — a thing no amount of on-frame re-hashing could ever discover,
/// because on a frame both sides are already this same binary.
/// </para>
/// </remarks>
public sealed class AgentVendoredFirmwareTests
{
    [Fact]
    public void The_image_this_binary_carries_is_the_image_the_pin_names()
    {
        var target = XvfFirmwarePin.Current.Target;

        using var embedded = XvfVendoredFirmware.Open(target);
        Assert.NotNull(embedded);

        using var buffer = new MemoryStream();
        embedded.CopyTo(buffer);
        var bytes = buffer.ToArray();

        Assert.Equal(target.SizeBytes, bytes.LongLength);
        Assert.Equal(target.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Every_pinned_image_travels_inside_the_binary()
    {
        var pin = XvfFirmwarePin.Current;

        // This test used to assert the opposite of half the pin: a v2.0.6 fallback and
        // 4mb_all_ff.bin were pinned and deliberately NOT vendored, and it recorded that open
        // question rather than approving of it. The question was answered by removing the two
        // images (XvfFirmwarePin carries the account), so what it now pins is the property that
        // makes the vendoring worth anything — a frame with no route to anywhere has every byte
        // this build would ever write.
        Assert.All(pin.Images, image => Assert.True(XvfVendoredFirmware.Carries(image)));
        Assert.Equal([pin.Target.Name], XvfVendoredFirmware.Names);
    }

    [Fact]
    public void The_resource_name_cannot_depend_on_which_host_built_the_binary()
    {
        // The whole reason the csproj carries a LogicalName transform. A build on the workstation
        // and a build in the arm64 container must embed one name, because a mismatch would be
        // invisible until a frame tried to flash. Asserting on the manifest name itself rather than
        // on the lookup, since the lookup normalises and would hide exactly this.
        var names = typeof(XvfVendoredFirmware).Assembly
            .GetManifestResourceNames()
            .Where(name => name.Replace('\\', '/').StartsWith(XvfVendoredFirmware.Prefix, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.DoesNotContain('\\', name));
        Assert.Contains(
            XvfVendoredFirmware.Prefix + XvfFirmwarePin.Current.Target.Name + XvfVendoredFirmware.CompressedSuffix,
            names);
    }

    [Fact]
    public void The_stored_bytes_are_smaller_than_the_image_and_are_not_the_image()
    {
        // Both halves matter. "Smaller" is the whole reason the compression is there — a regression
        // that silently embedded the raw file would still pass every other test in this class, and
        // would quietly put the better part of a megabyte back onto every agent download in the
        // fleet. "Not the image" is what stops the resource name lying: a resource called .bin.gz
        // holding a plain .bin would decode into nothing and be discovered on a frame instead.
        var target = XvfFirmwarePin.Current.Target;

        using var stored = typeof(XvfVendoredFirmware).Assembly.GetManifestResourceStream(
            XvfVendoredFirmware.Prefix + target.Name + XvfVendoredFirmware.CompressedSuffix);

        Assert.NotNull(stored);
        Assert.True(
            stored.Length < target.SizeBytes / 2,
            $"the stored resource is {stored.Length} bytes against an image of {target.SizeBytes}");

        // The gzip magic number. Two bytes, and they are the difference between a name that
        // describes its contents and one that does not.
        Assert.Equal(0x1F, stored.ReadByte());
        Assert.Equal(0x8B, stored.ReadByte());
    }

    [Fact]
    public async Task A_frame_with_no_network_at_all_still_gets_the_target_image()
    {
        using var files = new TemporaryFiles();
        var log = new RecordingLog();
        var installer = new XvfFirmwareInstaller(files.Files, UnreachableXvfHostDownload.Instance, log);
        var target = XvfFirmwarePin.Current.Target;

        var result = await installer.InstallAsync(TestContext.Current.CancellationToken);

        // The target is on the card and hashes to the pin, with nothing having been reachable.
        Assert.True(await installer.VerifyAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(
            target.SizeBytes,
            new FileInfo(files.Files.Resolve(XvfFirmwareInstaller.PathOf(target))).Length);

        // And the install reports Installed, which is now the honest answer and was not before: it
        // used to report Unreachable, because two more images were pinned, were not vendored, and
        // had to be fetched — so an offline frame could hold the target and still not flash it. The
        // recovery kit went with the pre-flight that required it, so a frame with no route to
        // anywhere now genuinely has everything this build would ever write.
        Assert.Equal(XvfFirmwareInstallResult.Installed, result);
        Assert.Contains("from this agent's own binary", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_embedded_image_is_placed_before_anything_that_needs_a_network()
    {
        // The pinned image behind two the binary does not carry. The guarantee under test is that
        // an offline frame still receives everything the binary carries — not that the embedded one
        // happens to sit first in the pin, which it does today and might not tomorrow. The two
        // stand-ins are fabricated here rather than taken from the pin, because the pin now names
        // one image and the property being tested is about ordering rather than about them.
        var current = XvfFirmwarePin.Current;
        var reordered = current with
        {
            Images =
            [
                current.Target with { Name = "a-fetched-image.bin", Sha256 = new string('a', 64) },
                current.Target with { Name = "another-fetched-image.bin", Sha256 = new string('b', 64) },
                current.Target,
            ],
        };

        using var files = new TemporaryFiles();
        var installer = new XvfFirmwareInstaller(
            files.Files,
            UnreachableXvfHostDownload.Instance,
            new RecordingLog(),
            reordered);

        await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.True(await installer.VerifyAsync(current.Target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_carried_copy_that_is_not_the_pinned_bytes_is_refused_and_fetched_instead()
    {
        // The only thing a wrong digest on a carried image can mean is that this executable's own
        // resource region is damaged — the first test in this file rules out a wrong file having
        // shipped. A frame that does have a network should then heal rather than stop, and the
        // refusal should be in the journal either way.
        var current = XvfFirmwarePin.Current;
        var substitute = new byte[current.Target.SizeBytes];
        Random.Shared.NextBytes(substitute);

        var claimed = current.Target with
        {
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(substitute)),
        };

        var pin = current with { Images = [claimed] };

        var download = new StubXvfHostDownload();
        download.Payloads[claimed.Name] = substitute;

        using var files = new TemporaryFiles();
        var log = new RecordingLog();
        var installer = new XvfFirmwareInstaller(files.Files, download, log, pin);

        await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.True(await installer.VerifyAsync(claimed, TestContext.Current.CancellationToken));
        Assert.Contains(
            "this agent's own binary does not match the pinned digest",
            log.Transcript,
            StringComparison.Ordinal);
        Assert.Contains(download.Opened, url => url.ToString().EndsWith(claimed.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void What_the_frame_reports_says_how_much_of_the_pin_travels_inside_the_binary()
    {
        using var files = new TemporaryFiles();
        var installer = new XvfFirmwareInstaller(
            files.Files,
            UnreachableXvfHostDownload.Instance,
            new RecordingLog());

        Assert.Contains("all 1 inside this binary", installer.Describe(), StringComparison.Ordinal);
    }
}
