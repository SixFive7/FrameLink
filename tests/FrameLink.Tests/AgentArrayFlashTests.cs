using System.Security.Cryptography;
using FrameLink.Agent.Firmware;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.Stage;
using FrameLink.Agent.Telemetry;
using FrameLink.Agent.State;
using FrameLink.Agent.Update;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// <b>Decision 91 — the array firmware flash, and every interlock in front of it.</b>
/// </summary>
/// <remarks>
/// <para>
/// The whole value of this work is that the dangerous path is <i>provably</i> guarded, so every test
/// here is written to fail if its guard is deleted. That means almost all of them assert on
/// <see cref="FlashProcessRunner.Commands"/> being <b>empty</b> — a refusal that still started
/// <c>dfu-util</c> is not a refusal, and a test that only checked a return value would pass whether
/// or not the guard existed.
/// </para>
/// <para>
/// <b>Nothing here has ever run against a real array.</b> The images are synthetic, the bus is a
/// temporary directory, and <c>dfu-util</c> is a recording double. What is exercised is the
/// decision-making: which conditions permit a write, in which order the authorisation is spent, what
/// is on the card while the write is in flight, and what is claimed afterwards.
/// </para>
/// </remarks>
public sealed class AgentArrayFlashTests
{
    private const string BusPath = "1-1";

    // ---------------------------------------------------------------------------------------
    // The pin: which image, and how it is addressed
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_pinned_target_is_the_unsuffixed_two_channel_16_kHz_build()
    {
        // The crux of the whole exercise. Upstream published three v2.1.0 images on one day and only
        // one of them keeps a FrameLink frame's audio topology: 16 kHz, two channels, square array —
        // the `ua-io16-sqr` profile Frame #1's array reports. Flashing `_16k6ch` would give the frame
        // six capture channels and `_48k2ch` would change its sample rate, underneath every mixer
        // resource in the catalog. A future bump that reached for a suffixed file would be a silent
        // hardware change, so the shape of the name is asserted rather than trusted.
        var target = XvfFirmwarePin.Current.Target;

        Assert.Equal("ua-io16-sqr", XvfFirmwarePin.Profile);
        Assert.EndsWith("_v2.1.0.bin", target.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("6ch", target.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("6chl", target.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("48k", target.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("16k", target.Name, StringComparison.Ordinal);
        Assert.Equal("2 1 0", target.Version);

        // And nothing else in the pin is a firmware the fleet could be pointed at by accident: the
        // other two exist to undo a write, never to make one.
        Assert.Equal(XvfFirmwareRole.Target, target.Role);
        Assert.Single(XvfFirmwarePin.Current.Images, image => image.Role == XvfFirmwareRole.Target);
    }

    [Fact]
    public void Every_pinned_image_is_addressed_by_its_own_commit_and_locked_by_a_digest()
    {
        // Two locks, and the first one is what makes the second meaningful. `v2.0.10` was published
        // twice under one filename with different bytes, so a pin that named a branch or a directory
        // would be a pin on whatever happened to be there. A raw URL carrying a full commit SHA is
        // content-addressed; the digest catches everything else.
        var pin = XvfFirmwarePin.Current;

        Assert.Equal(3, pin.Images.Count);

        foreach (var image in pin.Images)
        {
            Assert.Equal(40, image.Commit.Length);
            Assert.Equal(64, image.Sha256.Length);
            Assert.True(image.SizeBytes > 0);

            var url = pin.UrlOf(image).ToString();
            Assert.StartsWith("https://raw.githubusercontent.com/", url, StringComparison.Ordinal);
            Assert.Contains(image.Commit, url, StringComparison.Ordinal);
            Assert.EndsWith(image.PathInRepository, url, StringComparison.Ordinal);

            // The probe watches the file, never the directory. A directory probe would report
            // "moved" for every unrelated firmware upstream adds — three times in 2026 — and a gate
            // that always says moved is a gate nobody reads.
            var probe = pin.CommitsUrlOf(image);
            Assert.Contains("path=" + image.PathInRepository, probe, StringComparison.Ordinal);
            Assert.DoesNotContain("path=xmos_firmwares/usb&", probe, StringComparison.Ordinal);
        }

        // Recovery is a pair and both halves ship: the blank image erases a half-written partition
        // and the fallback firmware goes back on afterwards. One without the other is a route with a
        // hole in it.
        Assert.Equal("4mb_all_ff.bin", pin.Recovery.Name);
        Assert.Equal(4_194_304, pin.Recovery.SizeBytes);
        Assert.Equal("2 0 6", pin.Fallback.Version);
    }

    // ---------------------------------------------------------------------------------------
    // The images resource: nothing unverified ever reaches the card
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_corrupted_download_installs_nothing_at_all()
    {
        using var fixture = new FlashFixture();
        fixture.ServeEverything();
        fixture.Corrupt(fixture.Pin.Target.Name);

        var result = await fixture.Images.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(XvfFirmwareInstallResult.ChecksumMismatch, result);
        Assert.False(fixture.Exists(fixture.Pin.Target));
        Assert.False(fixture.Exists(fixture.Pin.Target, XvfFirmwareInstaller.StagingSuffix));
    }

    [Fact]
    public async Task A_short_download_installs_nothing_at_all()
    {
        using var fixture = new FlashFixture();
        fixture.ServeEverything();
        fixture.Truncate(fixture.Pin.Target.Name);

        var result = await fixture.Images.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(XvfFirmwareInstallResult.SizeMismatch, result);
        Assert.False(fixture.Exists(fixture.Pin.Target));
    }

    [Fact]
    public async Task The_image_resource_re_hashes_the_card_on_every_pass_rather_than_remembering()
    {
        // §2.4 refuses to claim "applied" from a successful write, and a note saying the install
        // succeeded would outlive the bytes it describes. So a file damaged after a clean install is
        // drift on the very next Observe, with no process restart in between.
        using var fixture = new FlashFixture();
        fixture.Seed(AlsaCards.CardsPath, FlashFixture.CardsWithArray);
        fixture.ServeEverything();

        var resource = new XvfFirmwareImageResource(fixture.Files.Files, fixture.Images);
        await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);

        fixture.Damage(fixture.Pin.Recovery);
        var after = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(after.InSync);
        Assert.Contains("4mb_all_ff.bin", after.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_with_no_sound_hardware_holds_no_images_and_says_so()
    {
        using var fixture = new FlashFixture();
        var resource = new XvfFirmwareImageResource(fixture.Files.Files, fixture.Images);

        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.True(observation.InSync);
        Assert.Equal("no sound hardware on this machine", observation.Observed);
    }

    // ---------------------------------------------------------------------------------------
    // Authorisation: single-use, digest-named, spent before anything starts
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unauthorised_frame_starts_no_process()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.NotAuthorised, outcome.Refusal);
        Assert.Empty(fixture.Processes.Commands);
    }

    [Fact]
    public async Task One_authorisation_produces_exactly_one_write_however_many_passes_run()
    {
        // The failure the old `audio.firmwareFlashAuthorised` had by construction: it was a
        // persistent per-device string, so once set it re-authorised a flash on every pass for ever.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();

        var flash = fixture.Flash();
        var first = await flash.TickAsync(TestContext.Current.CancellationToken);
        var second = await flash.TickAsync(TestContext.Current.CancellationToken);
        var third = await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(first.Flashed);
        Assert.False(second.Flashed);
        Assert.False(third.Flashed);
        Assert.Equal(ArrayFlashRefusal.AlreadyConsumed, second.Refusal);
        Assert.Single(fixture.Processes.Commands, command => command.StartsWith("dfu-util ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_spent_authorisation_survives_the_process_that_spent_it()
    {
        // The record is on the card and fsynced, so a crash between the consume and the write cannot
        // re-authorise. Asserted by building a *new* flash over the same store, which is what a
        // restart leaves behind.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);
        fixture.ClearMarker();

        var afterRestart = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AlreadyConsumed, afterRestart.Refusal);
        Assert.Single(fixture.Processes.Commands, command => command.StartsWith("dfu-util ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Re_authorising_means_writing_a_different_value()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(ticket: "first");

        var flash = fixture.Flash();
        await flash.TickAsync(TestContext.Current.CancellationToken);

        // Same digest, new ticket: a deliberate second act by a person, and the only way to get one.
        fixture.Roll("2 0 6");
        fixture.Authorise(ticket: "second");
        var again = await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(again.Flashed);
        Assert.Equal(2, fixture.Processes.Commands.Count(command => command.StartsWith("dfu-util ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_authorisation_is_recorded_before_dfu_util_is_started_and_never_after()
    {
        // Ordering, asserted from inside the process runner. If the consume happened after the write
        // — or in a `finally` — a power cut in the middle would leave a frame armed to write again
        // onto an array whose partition is half-written.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();

        string? consumedDuringWrite = null;
        fixture.Processes.Before = _ => consumedDuringWrite =
            fixture.Files.Store.ReadText(ArrayFirmwareFlash.ConsumedFileName)?.Trim();

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(fixture.Authorisation, consumedDuringWrite);
    }

    [Fact]
    public async Task An_authorisation_naming_anything_but_the_pinned_image_is_refused()
    {
        // The version string is not the identity: the same version has shipped twice with different
        // bytes. So the authorisation names the digest, and a digest that is not the pinned one
        // authorises nothing — including the digest of the *fallback* image, which is a real,
        // verified, present file that this path must still never write.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();

        foreach (var wrong in new[] { fixture.Pin.Fallback.Sha256, fixture.Pin.Recovery.Sha256, "2.1.0", new string('0', 64) })
        {
            fixture.Settings[ArrayFirmwareFlash.AuthorisationKey] = wrong;
            var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ArrayFlashRefusal.NotThePinnedImage, outcome.Refusal);
        }

        Assert.Empty(fixture.Processes.Commands);
    }

    // ---------------------------------------------------------------------------------------
    // Pre-flight: never write without a verified image and a verified way back
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_target_image_that_does_not_match_the_pin_is_never_written()
    {
        // The single most important guard in this file. `Authorise()` used to check only that the
        // file existed, and a DFU write of an unverified 933 KB file is strictly worse than no flash
        // at all — a truncated download would be pushed onto the array with nothing complaining.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Damage(fixture.Pin.Target);
        fixture.Authorise();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.ImageNotVerified, outcome.Refusal);
        Assert.Empty(fixture.Processes.Commands);
        Assert.Null(fixture.Files.Store.ReadText(ArrayFirmwareFlash.ConsumedFileName));
    }

    [Fact]
    public async Task A_missing_way_back_stops_the_write_before_it_starts()
    {
        // Recovery must not depend on a network at the moment it is needed. A frame that cannot
        // prove it holds the erase image and the known-good fallback has no rehearsed route back,
        // so it does not take the route forward either.
        foreach (var absent in new[] { XvfFirmwareRole.Recovery, XvfFirmwareRole.Fallback })
        {
            using var fixture = new FlashFixture();
            await fixture.ReadyToFlashAsync();
            fixture.Remove(fixture.Pin.Of(absent));
            fixture.Authorise();

            var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ArrayFlashRefusal.RecoveryNotVerified, outcome.Refusal);
            Assert.Empty(fixture.Processes.Commands);
        }
    }

    [Fact]
    public async Task No_dfu_util_on_the_frame_is_a_refusal_rather_than_a_failed_process()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Files.Files.DeleteFile(ArrayFirmwareFlash.DfuUtilPath);
        fixture.Authorise();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.DfuUtilMissing, outcome.Refusal);
        Assert.Empty(fixture.Processes.Commands);
    }

    [Fact]
    public async Task An_array_that_is_absent_or_doubled_is_never_written_to()
    {
        // `dfu-util` picks whichever DFU device it enumerates first and `xvf_host` has no device
        // selector either, so with two attached nothing here can say which unit it would write.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.DetachArrays();
        fixture.Authorise();

        var none = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ArrayFlashRefusal.NoArrayAttached, none.Refusal);

        fixture.Attach(BusPath, "0206", "…030");
        fixture.Attach("1-2", "020a", "…069");
        var two = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.MoreThanOneArray, two.Refusal);
        Assert.Empty(fixture.Processes.Commands);
    }

    [Fact]
    public async Task An_array_already_running_the_target_is_left_alone_and_the_authorisation_is_spent()
    {
        // §0.1's idempotency, and the reason the authorisation is consumed anyway: leaving it armed
        // would let a later array swap be written by nobody's decision.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Roll("2 1 0");
        fixture.Authorise();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AlreadyAtTarget, outcome.Refusal);
        Assert.Empty(fixture.Processes.Commands);
        Assert.Equal(fixture.Authorisation, fixture.Files.Store.ReadText(ArrayFirmwareFlash.ConsumedFileName)?.Trim());
    }

    // ---------------------------------------------------------------------------------------
    // The window: what is on the card while a write is in flight
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_marker_is_on_the_card_while_dfu_util_runs_and_gone_afterwards()
    {
        // The bench power switch has nothing else to read. `fl.py power` cannot ask a running
        // process whether it is mid-write; it can only look at the card.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();

        // Only the write itself. The hardware gate reads the unit through the control tool before
        // the window opens and the verify reads it again after the window has closed, so a hook that
        // fired on every command would be answering about the wrong instants.
        var markerDuringWrite = false;
        fixture.Processes.Before = command =>
        {
            if (command.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal))
            {
                markerDuringWrite = fixture.Files.Store.Exists(ArrayFlashWindow.MarkerFileName);
            }
        };

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.True(markerDuringWrite);
        Assert.False(fixture.Files.Store.Exists(ArrayFlashWindow.MarkerFileName));
    }

    [Fact]
    public async Task A_write_that_was_cut_short_leaves_the_marker_behind()
    {
        // The one path the marker exists for, and the one a plain `using` would have cleared. When
        // the agent's token is cancelled mid-write — which is exactly what a self-update or a
        // `systemctl stop` does — `HostProcessRunner` abandons the child rather than killing it and
        // systemd takes the cgroup down a moment later. Nothing afterwards can tell how far the
        // write got, so the mark has to survive the process that made it.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Before = command =>
        {
            if (command.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal))
            {
                throw new OperationCanceledException("the agent is restarting");
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Flash().TickAsync(TestContext.Current.CancellationToken));

        Assert.True(fixture.Files.Store.Exists(ArrayFlashWindow.MarkerFileName));

        // And the next process refuses on it, with the authorisation already spent either way.
        fixture.Processes.Before = null;
        var afterRestart = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.PreviousFlashUnfinished, afterRestart.Refusal);

        // The cut-short attempt reached the runner, so one command is on the record. What must not
        // happen is a second one.
        Assert.Single(fixture.Processes.Commands, command => command.StartsWith("dfu-util ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_flash_that_never_finished_blocks_every_later_one_until_a_person_clears_it()
    {
        // A cgroup kill, a power cut and a crash all leave the same array behind, and nothing can
        // tell afterwards how far the write got. Retrying a partial write is the documented route
        // from a recoverable board to an unrecoverable one, so this never clears itself.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Files.Store.WriteText(ArrayFlashWindow.MarkerFileName, "2026-08-23T00:00:00Z writing something\n");
        fixture.Authorise();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.PreviousFlashUnfinished, outcome.Refusal);
        Assert.Empty(fixture.Processes.Commands);
        Assert.Null(fixture.Files.Store.ReadText(ArrayFirmwareFlash.ConsumedFileName));

        // And it is latched, so a process that starts, sees it, and keeps running cannot forget.
        fixture.ClearMarker();
        Assert.Equal(
            ArrayFlashRefusal.PreviousFlashUnfinished,
            (await fixture.Flash(reuseWindow: true).TickAsync(TestContext.Current.CancellationToken)).Refusal);
    }

    [Fact]
    public async Task An_interrupted_flash_is_reported_to_the_fleet_rather_than_only_logged()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Files.Store.WriteText(ArrayFlashWindow.MarkerFileName, "2026-08-23T00:00:00Z writing something\n");

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        var published = Assert.Single(fixture.Telemetry.Events);
        Assert.Equal(DeviceEventKinds.ArrayFlash, published.Kind);
        Assert.Contains("never finished", published.Summary, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // The write itself: one attempt, verified by evidence, recorded
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_write_targets_the_upgrade_partition_with_the_verified_image()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        var command = Assert.Single(
            fixture.Processes.Commands,
            line => line.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal));

        Assert.Equal(
            "dfu-util -R -e -a 1 -D " + XvfFirmwareInstaller.PathOf(fixture.Pin.Target),
            command);
    }

    [Fact]
    public async Task A_failed_write_is_never_retried_and_asks_for_a_person()
    {
        // "One attempt, not three." The authorisation is already spent by the time `dfu-util` runs,
        // so this is structural rather than a policy a counter enforces.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Result = new ProcessResult(74, string.Empty, "dfu-util: Cannot open DFU device");
        fixture.ReEnumerate = false;

        var flash = fixture.Flash();
        var first = await flash.TickAsync(TestContext.Current.CancellationToken);
        await flash.TickAsync(TestContext.Current.CancellationToken);
        await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(first.Flashed);
        Assert.False(first.Succeeded);
        Assert.Single(
            fixture.Processes.Commands,
            line => line.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal));
        Assert.Contains("somebody has to look at the unit", first.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_is_the_array_coming_back_and_not_a_timer_expiring()
    {
        // The old Act slept five seconds and claimed victory. This one polls the descriptor until
        // the array re-enumerates reporting the pinned version, and reports honestly when it does
        // not — which is the whole difference between a verify and a wait.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.ReEnumerate = false;

        var silent = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.True(silent.Flashed);
        Assert.False(silent.Succeeded);
        Assert.Contains("2 0 6 before", silent.Summary, StringComparison.Ordinal);

        // Give the same frame a fresh authorisation and an array that does come back.
        using var healthy = new FlashFixture();
        await healthy.ReadyToFlashAsync();
        healthy.Authorise();

        var good = await healthy.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.True(good.Succeeded);
        Assert.Contains("2 1 0 after", good.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_event_trail_carries_the_digest_both_versions_and_the_tool_output()
    {
        // A fleet-wide firmware change has to be answerable months later, and `dfu-util`'s own
        // output is the only record of what the device said while it was being written.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Result = new ProcessResult(0, "Download\t[=========================] 100%\nDone!", string.Empty);

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        var published = Assert.Single(fixture.Telemetry.Events);
        Assert.Equal(DeviceEventKinds.ArrayFlash, published.Kind);
        Assert.Contains(fixture.Pin.Target.Sha256, published.Summary, StringComparison.Ordinal);
        Assert.Contains("2 0 6 before", published.Summary, StringComparison.Ordinal);
        Assert.Contains("2 1 0 after", published.Summary, StringComparison.Ordinal);
        Assert.Contains("Done!", published.Delta!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Deferrals: waiting is not spending
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_call_in_progress_defers_the_write_without_spending_the_authorisation()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.CallActive = true;

        var flash = fixture.Flash();
        var deferred = await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.CallInProgress, deferred.Refusal);
        Assert.Empty(fixture.Processes.Commands);
        Assert.Null(fixture.Files.Store.ReadText(ArrayFirmwareFlash.ConsumedFileName));

        fixture.CallActive = false;
        Assert.True((await flash.TickAsync(TestContext.Current.CancellationToken)).Flashed);
    }

    [Fact]
    public async Task A_pending_agent_restart_defers_the_write_rather_than_racing_it()
    {
        // The other half of the update stand-down, from this side: an agent that has already swapped
        // its binary is seconds away from a restart that would kill the child it was about to start.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.RestartPending = true;

        var flash = fixture.Flash();
        var deferred = await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AgentRestartPending, deferred.Refusal);
        Assert.Empty(fixture.Processes.Commands);

        fixture.RestartPending = false;
        Assert.True((await flash.TickAsync(TestContext.Current.CancellationToken)).Flashed);
    }

    // ---------------------------------------------------------------------------------------
    // The two interlocks that live outside this class
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_update_stands_down_while_a_firmware_write_is_in_flight()
    {
        // The interlock the operator's own list missed and the one most likely to fire: the update
        // service restarts the process, systemd tears down the cgroup with KillMode=control-group,
        // and `dfu-util` dies with it. Asserted on the swap and the restart, not on the outcome
        // enum, because those two are what actually kill the child.
        using var store = new TemporaryStore();
        var window = new ArrayFlashWindow(store.Store, new ManualClock());
        var swap = new RecordingBinarySwap();
        var restart = new RecordingRestart();

        var updates = new UpdateService(
            new StubReleaseSource
            {
                Release = Release(),
                Payload = [1, 2, 3, 4],
            },
            swap,
            new ManualClock(),
            new AgentStatusHub(AgentStatusFactory.Starting()),
            restart,
            new RecordingLog(),
            () => new Uri("https://control.example"),
            "1.0.0",
            "linux-arm64")
        {
            StandDown = () => window.Reason,
        };

        using (window.Open("writing firmware"))
        {
            Assert.Equal(UpdateOutcome.StoodDown, await updates.CheckOnceAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, swap.Applied);
        Assert.Empty(restart.Requests);

        // And it is a deferral, not a disablement: the very next tick converges the frame.
        Assert.Equal(UpdateOutcome.Applied, await updates.CheckOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, swap.Applied);
        Assert.Single(restart.Requests);
    }

    [Fact]
    public async Task An_update_already_downloaded_is_still_not_applied_once_the_window_opens()
    {
        // The download can take minutes on a slow link, so the check that matters is the one on the
        // far side of it. A stand-down asked only at the top would miss exactly the window it exists
        // to see.
        using var store = new TemporaryStore();
        var window = new ArrayFlashWindow(store.Store, new ManualClock());
        var swap = new RecordingBinarySwap();
        var restart = new RecordingRestart();
        var releases = new StubReleaseSource
        {
            Release = Release(),
            Payload = [1, 2, 3, 4],
        };

        IDisposable? held = null;
        var updates = new UpdateService(
            releases,
            swap,
            new ManualClock(),
            new AgentStatusHub(AgentStatusFactory.Starting()),
            restart,
            new RecordingLog(),
            () => new Uri("https://control.example"),
            "1.0.0",
            "linux-arm64")
        {
            // Open on the second ask, which is the one taken after the download.
            StandDown = () =>
            {
                var reason = window.Reason;
                held ??= window.Open("writing firmware");
                return reason;
            },
        };

        Assert.Equal(UpdateOutcome.StoodDown, await updates.CheckOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, releases.DownloadCalls);
        Assert.Equal(0, swap.Applied);
        Assert.Empty(restart.Requests);

        held?.Dispose();
    }

    [Fact]
    public async Task A_reboot_is_refused_while_a_firmware_write_is_in_flight()
    {
        // §2.4 keeps its "every resource reboots, no exceptions" rule untouched: this is a refusal,
        // which the loop already treats as first-class — the change is written, cannot be proven,
        // spends an attempt and reaches a person.
        using var store = new TemporaryStore();
        var window = new ArrayFlashWindow(store.Store, new ManualClock());
        var inner = new InProcessRebootBoundary(new MutableBootIdentity("boot-1"));
        var boundary = new RebootHold(inner, () => window.Reason, new RecordingLog());
        var request = new RebootRequest { Resource = "some.other.resource", Change = "wrote a file", Attempt = 1 };

        using (window.Open("writing firmware"))
        {
            var refused = await boundary.CrossAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(RebootCrossing.Refused, refused.Crossing);
            Assert.Contains("cannot be undone", refused.Detail!, StringComparison.Ordinal);
            Assert.Empty(inner.Crossings);
        }

        var crossed = await boundary.CrossAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(RebootCrossing.Crossed, crossed.Crossing);
        Assert.Single(inner.Crossings);
    }

    [Fact]
    public async Task The_reboot_hold_and_the_reboot_floor_both_apply_and_neither_hides_the_other()
    {
        // Composed as the agent composes them, so a hold cannot silently swallow a floor refusal or
        // spend one of the floor's reboots on a crossing that never happened.
        using var store = new TemporaryStore();
        using var files = new TemporaryFiles();
        var clock = new ManualClock();
        var window = new ArrayFlashWindow(store.Store, clock);
        var journal = new ReconcileJournal(files.Store, new RecordingLog());
        var inner = new InProcessRebootBoundary(new MutableBootIdentity("boot-1"));
        var boundary = new RebootHold(
            new RebootFloor(inner, journal, clock, new RecordingLog(), limit: 2, window: TimeSpan.FromHours(6)),
            () => window.Reason,
            new RecordingLog());
        var request = new RebootRequest { Resource = "some.other.resource", Change = "wrote a file", Attempt = 1 };

        using (window.Open("writing firmware"))
        {
            await boundary.CrossAsync(request, TestContext.Current.CancellationToken);
        }

        // The held crossing spent none of the floor's budget, so both of its reboots are still there.
        Assert.Empty(journal.Read().Reboots);
        Assert.Equal(RebootCrossing.Crossed, (await boundary.CrossAsync(request, TestContext.Current.CancellationToken)).Crossing);
        Assert.Equal(RebootCrossing.Crossed, (await boundary.CrossAsync(request, TestContext.Current.CancellationToken)).Crossing);
        Assert.Equal(RebootCrossing.Refused, (await boundary.CrossAsync(request, TestContext.Current.CancellationToken)).Crossing);
    }


    // ---------------------------------------------------------------------------------------
    // The wiring, which every test above would pass without
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_agent_actually_wires_the_window_to_the_update_service_and_the_reboot_boundary()
    {
        // The gap this closes is real and would otherwise be invisible: every interlock test in this
        // file constructs its own `UpdateService` and its own boundary, so an `AgentHost` that
        // forgot to pass `flashWindow.Reason` to either of them would leave a running frame entirely
        // unguarded while the whole suite stayed green. A text assertion is the honest tool here —
        // `AgentHost.RunAsync` is four hundred lines of composition against real Linux surfaces and
        // has never been constructible in the suite.
        var host = File.ReadAllText(Path.Combine(
            GuiFreshnessTests.RepositoryRoot(), "src", "FrameLink.Agent", "AgentHost.cs"));

        Assert.Contains("new ArrayFlashWindow(store, _clock)", host, StringComparison.Ordinal);
        Assert.Contains("StandDown = () => flashWindow.Reason", host, StringComparison.Ordinal);
        Assert.Contains("new RebootHold(", host, StringComparison.Ordinal);
        Assert.Contains("arrayFlash.RunAsync(shutdown.Token)", host, StringComparison.Ordinal);

        // The window is constructed before the update service, because it latches whether a
        // *previous* process was writing firmware and nothing may have written a marker first.
        Assert.True(
            host.IndexOf("new ArrayFlashWindow(store, _clock)", StringComparison.Ordinal)
            < host.IndexOf("new UpdateService(", StringComparison.Ordinal));
    }

    [Fact]
    public void The_authorisation_key_is_not_offered_by_the_settings_screen_yet()
    {
        // Deliberate, and it is the sequencing in decision 91 expressed where somebody would
        // otherwise reach. The key works — settings are a generic store — but suggesting it in the
        // interface would invite a first flash before the Safe Mode recovery route has ever been
        // rehearsed on this project's own hardware, which is the one thing that must happen first.
        // Whoever adds the entry has to delete this test, which is the point: it makes that a
        // decision rather than a convenience.
        var catalog = File.ReadAllText(Path.Combine(
            GuiFreshnessTests.RepositoryRoot(),
            "src", "FrameLink.Control", "gui", "src", "lib", "settings-catalog.ts"));

        Assert.DoesNotContain(ArrayFirmwareFlash.AuthorisationKey, catalog, StringComparison.Ordinal);
    }

    /// <summary>A served release the stub feed can hand out.</summary>
    private static AgentRelease Release() => new()
    {
        Version = "9.9.9",
        RuntimeIdentifier = "linux-arm64",
        Sha256 = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3, 4])),
        SizeBytes = 4,
        Url = "/agent/binary",
    };

    // ---------------------------------------------------------------------------------------
    // What the fleet is told, which is what "converge on the latest" is made of
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_frame_reports_whether_it_runs_the_firmware_the_fleet_converges_on()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();

        var behind = await fixture.Reporter().ReadAsync(TestContext.Current.CancellationToken);
        Assert.Contains("is not on it", behind!, StringComparison.Ordinal);
        Assert.Contains(XvfFirmwarePin.Current.Target.Version, behind, StringComparison.Ordinal);

        fixture.Roll("2 1 0");
        var current = await fixture.Reporter().ReadAsync(TestContext.Current.CancellationToken);
        Assert.Contains("That is the firmware this fleet converges on", current!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // The interlock that is a person: nothing is written until somebody at the frame agrees
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_authorised_flash_writes_nothing_until_somebody_at_the_frame_agrees()
    {
        // The whole feature in one assertion. Mains loss during the write is the one hazard no
        // software interlock on this frame can reach — the frame cannot hold its own power on — so
        // the last gate is a human being who has been told that and has said they will not unplug
        // it. Everything else about this frame permits a write: the image is verified, the recovery
        // pair is on the card, the tool is installed, the unit is recognised and the operator has
        // authorised it.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AwaitingLocalApproval, outcome.Refusal);
        Assert.False(outcome.Flashed);
        Assert.DoesNotContain(
            fixture.Processes.Commands,
            line => line.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal));

        // Deferred, never spent. An operator's decision does not expire because the household was
        // out, so the authorisation is still armed and nothing is on the card.
        Assert.Null(fixture.Consumed);
        Assert.Equal(ArrayFlashPhase.Asking, fixture.Screen?.Phase);
    }

    [Fact]
    public async Task A_hold_at_the_frame_is_what_lets_the_write_start()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);

        // The genuine path: the same method the console's completed hold and the browser's button
        // both call, answering whatever the agent currently has on the panel.
        Assert.True(fixture.HoldTheScreen());

        var outcome = await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Flashed);
        Assert.True(outcome.Succeeded);
        Assert.Equal(fixture.Authorisation, fixture.Consumed);
    }

    [Fact]
    public async Task The_approval_screen_says_what_is_happening_and_what_must_not_happen()
    {
        // The wording *is* the feature. It is read by a family member with no computer experience,
        // and it is the only mitigation that exists for the one hazard nothing else can guard —
        // so it has to convey what is about to happen, roughly how long it lasts, that taking the
        // power away can destroy the microphone, and that they should not do that. A sentence they
        // cannot act on is the same as no interlock.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        var prompt = Assert.IsType<ArrayFlashPrompt>(fixture.Screen);
        var text = fixture.ScreenText;

        Assert.Contains("microphone", prompt.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("two minutes", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stay switched on", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broken for good", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("power", text, StringComparison.OrdinalIgnoreCase);

        // And it offers a way to say yes, with the hold the console counts out.
        Assert.Equal("Yes — go ahead", prompt.Affordance);
        Assert.Equal(ArrayFlashApproval.ApprovalHold, prompt.Hold);
        Assert.Contains("5 seconds", ArrayFlashVoice.HoldLine(prompt), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_screen_a_family_member_reads_carries_jargon_or_a_version_number()
    {
        // No version numbers in a headline, no tool names, no digests. A person weighing "should I
        // leave this alone for two minutes" gains nothing from `2 1 0` and loses the sentence that
        // matters to the noise around it.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);
        var asking = fixture.ScreenText;

        fixture.HoldTheScreen();
        await flash.TickAsync(TestContext.Current.CancellationToken);
        var finished = fixture.ScreenText;

        foreach (var text in new[] { asking, finished })
        {
            Assert.DoesNotContain("dfu", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sha256", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bcdDevice", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("USB", text, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Pin.Target.Version, text, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Pin.Target.Name, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_approval_covers_the_write_it_was_given_for_and_no_other()
    {
        // Somebody agreed to *this* write. An operator who then points the frame at a different
        // authorisation has changed what was agreed to, and the household has to be asked again —
        // otherwise a yes given once becomes a standing permission nobody granted.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(ticket: "first", approved: false);

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.HoldTheScreen());

        fixture.Authorise(ticket: "second", approved: false);
        var outcome = await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AwaitingLocalApproval, outcome.Refusal);
        Assert.Null(fixture.Consumed);
    }

    [Fact]
    public async Task An_agreement_does_not_outlive_the_process_that_took_it()
    {
        // Deliberately nothing durable. A stored "somebody said yes" would outlive the person who
        // said it, and what the write needs is somebody in the room now — so a restart between the
        // hold and the write loses the approval and asks again, which is the safe way to fail.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        await fixture.Flash(reuseWindow: true).TickAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.HoldTheScreen());

        fixture.RestartApproval();
        var outcome = await fixture.Flash(reuseWindow: true).TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AwaitingLocalApproval, outcome.Refusal);
        Assert.Equal(ArrayFlashPhase.Asking, fixture.Screen?.Phase);
        Assert.Null(fixture.Consumed);
    }

    [Fact]
    public async Task A_household_is_never_asked_about_a_write_that_would_have_been_refused_anyway()
    {
        // The question is the last gate and not the first. Asking somebody to stand by a frame that
        // then refuses for a missing image teaches them the question means nothing, which is exactly
        // how an interlock made of human attention stops working.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Damage(fixture.Pin.Target);
        fixture.Authorise(approved: false);

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.ImageNotVerified, outcome.Refusal);
        Assert.Null(fixture.Screen);
    }

    [Fact]
    public async Task A_call_takes_the_question_off_the_screen()
    {
        // The prompt covers whatever the panel was showing, and what it may not cover is somebody's
        // conversation. The flash already defers on a call; this is the screen half of that, and
        // without it a question would sit on top of a call for as long as the tick interval.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Screen);

        fixture.CallActive = true;
        var outcome = await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.CallInProgress, outcome.Refusal);
        Assert.Null(fixture.Screen);
    }

    [Fact]
    public async Task A_question_on_the_screen_is_not_a_reason_to_re_read_the_device()
    {
        // The screen has to come off quickly when a call starts, and a full tick is not the way to
        // notice that: it re-hashes six megabytes of pinned images and starts three control-tool
        // processes against the device the reconciler is also reading. So the fast cadence runs one
        // cheap check, and the check does exactly one thing.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);

        var afterAsking = fixture.Processes.Commands.Count;

        Assert.False(flash.StandDown());
        Assert.NotNull(fixture.Screen);
        Assert.Equal(afterAsking, fixture.Processes.Commands.Count);

        fixture.CallActive = true;

        Assert.True(flash.StandDown());
        Assert.Null(fixture.Screen);
        Assert.Equal(afterAsking, fixture.Processes.Commands.Count);
    }

    [Fact]
    public async Task A_screen_that_went_up_too_early_stops_being_wrong_about_the_frame()
    {
        // Both facts a screen is composed from can arrive after it goes up. The panel is found by a
        // watch of its own with no ordering against the first look at the authorisation, so a screen
        // composed a moment too early tells a household its screen cannot be touched while they are
        // touching it — and then offers them nothing to press.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.DetachPanel();
        fixture.Authorise(approved: false);

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Contains("cannot be touched", fixture.ScreenText, StringComparison.Ordinal);
        Assert.Null(fixture.Screen?.Affordance);

        fixture.AttachPanel();
        Assert.False(flash.StandDown());

        Assert.DoesNotContain("cannot be touched", fixture.ScreenText, StringComparison.Ordinal);
        Assert.Equal("Yes — go ahead", fixture.Screen?.Affordance);
        Assert.True(fixture.HoldTheScreen());
    }

    [Fact]
    public async Task A_recovery_screen_names_the_operator_once_the_link_has_said_who_that_is()
    {
        // Decision 71's sentence, on the one screen whose last instruction is "tell somebody". The
        // details arrive over the link, so a screen that went up before the first push named nobody
        // — and the frame that needs this sentence most is a frame whose microphone has stopped
        // answering, which is not a frame anybody should have to guess about.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Before = command =>
        {
            if (command.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal))
            {
                throw new OperationCanceledException("the power went off");
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Flash().TickAsync(TestContext.Current.CancellationToken));

        fixture.Processes.Before = null;
        fixture.DetachArrays();

        var flash = fixture.Flash();
        await flash.TickAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Douwe", fixture.ScreenText, StringComparison.Ordinal);

        fixture.Hub.Publish(status => status with
        {
            Contact = new OperatorContact
            {
                Name = "Douwe",
                Contact = "06 12 34 56 78",
                UpdatedUtc = fixture.Clock.UtcNow,
            },
        });

        Assert.False(flash.StandDown());
        Assert.Contains("Douwe", fixture.ScreenText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unchanged_question_is_not_republished_to_the_two_stages()
    {
        // A record's generated equality compares the lines by reference, so two identical screens
        // are unequal whenever they were built by two calls — which is every call. Publishing on
        // that repaints the console and re-sends the page a frame on every tick, for a screen whose
        // content is fixed from the moment it goes up.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        var publishes = 0;
        using var subscription = fixture.Hub.Subscribe(_ => publishes++);

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, publishes);

        await flash.TickAsync(TestContext.Current.CancellationToken);
        await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, publishes);
    }

    [Fact]
    public async Task The_frame_stops_asking_after_a_while_and_asks_again_later()
    {
        // A question nobody answers must not hold a household's photographs for the rest of the
        // week. It is a product bound rather than a safety one: the authorisation stays armed, the
        // refusal keeps reaching the Fleet Manager, and the frame asks again after the rest.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Screen);

        fixture.Clock.UtcNow += ArrayFlashApproval.AskWindow + TimeSpan.FromMinutes(1);
        await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Null(fixture.Screen);
        Assert.Null(fixture.Consumed);

        fixture.Clock.UtcNow += ArrayFlashApproval.RestWindow + TimeSpan.FromMinutes(1);
        var again = await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AwaitingLocalApproval, again.Refusal);
        Assert.Equal(ArrayFlashPhase.Asking, fixture.Screen?.Phase);
    }

    [Fact]
    public async Task A_frame_with_no_touchscreen_writes_nothing_and_names_the_way_round_it()
    {
        // Nobody can agree at a frame with no panel to agree on, so nothing is written — and the
        // refusal has to name the one route that exists rather than leaving an operator to guess
        // why an authorised frame never did anything.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.DetachPanel();
        fixture.Authorise(approved: false);

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AwaitingLocalApproval, outcome.Refusal);
        Assert.Null(fixture.Consumed);
        Assert.Contains(ArrayFirmwareFlash.UnattendedPrefix, outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("TEST-DEVICE", outcome.Summary, StringComparison.Ordinal);

        // The screen says so too, and offers nothing, because there is nothing it could offer.
        Assert.Contains("cannot be touched", fixture.ScreenText, StringComparison.Ordinal);
        Assert.Null(fixture.Screen?.Affordance);
    }

    // ---------------------------------------------------------------------------------------
    // What the panel says while it is writing, and what it says afterwards
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_panel_says_do_not_unplug_for_the_whole_of_the_write()
    {
        // The person who agreed to it two minutes ago may not be the person who walks past now, so
        // the warning is repeated for the length of the write rather than only at the moment it was
        // agreed to — and the screen offers nothing, because this is the one moment when there is
        // nothing a person may usefully do.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();

        ArrayFlashPrompt? during = null;
        fixture.Processes.Before = command =>
        {
            if (command.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal))
            {
                during = fixture.Screen;
            }
        };

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        var prompt = Assert.IsType<ArrayFlashPrompt>(during);
        Assert.Equal(ArrayFlashPhase.Writing, prompt.Phase);
        Assert.Contains("do not unplug", prompt.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("break the microphone for good", string.Join(" ", prompt.Lines), StringComparison.Ordinal);
        Assert.Null(prompt.Affordance);
    }

    [Fact]
    public async Task A_finished_write_says_whether_it_worked_and_that_it_is_safe_to_unplug()
    {
        // The frame asked somebody to stand guard; it owes them the moment they are released. And
        // the operator asked for a way to say "OK, carry on" rather than a silent resumption.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();

        await fixture.Flash(reuseWindow: true).TickAsync(TestContext.Current.CancellationToken);

        var prompt = Assert.IsType<ArrayFlashPrompt>(fixture.Screen);
        Assert.Equal(ArrayFlashPhase.Succeeded, prompt.Phase);
        Assert.Contains("It worked", string.Join(" ", prompt.Lines), StringComparison.Ordinal);
        Assert.Contains("safe to unplug", string.Join(" ", prompt.Lines), StringComparison.Ordinal);
        Assert.Equal("OK", prompt.Affordance);

        // And it stays there until somebody takes it, rather than vanishing before it is read.
        Assert.True(fixture.HoldTheScreen());
        Assert.Null(fixture.Screen);
    }

    [Fact]
    public async Task A_write_that_did_not_work_says_so_plainly_and_still_releases_the_person()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.ReEnumerate = false;
        fixture.Processes.Result = new ProcessResult(74, string.Empty, "dfu-util: Cannot open DFU device");

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        var prompt = Assert.IsType<ArrayFlashPrompt>(fixture.Screen);
        Assert.Equal(ArrayFlashPhase.Failed, prompt.Phase);
        Assert.True(prompt.Alarming);

        var body = string.Join(" ", prompt.Lines);
        Assert.Contains("did not finish", prompt.Headline, StringComparison.Ordinal);
        Assert.Contains("safe to unplug", body, StringComparison.Ordinal);
        Assert.Contains("Nothing you did caused this", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_screen_nobody_answers_goes_away_on_its_own()
    {
        // The completion screen waits for a person, and a frame flashed under the operator bypass
        // has no person to wait for. Without a bound it would hold a household's photographs for
        // ever over a write that had already finished.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Screen);

        fixture.Clock.UtcNow += ArrayFlashApproval.CompletionLinger + TimeSpan.FromMinutes(1);
        await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Null(fixture.Screen);
    }

    // ---------------------------------------------------------------------------------------
    // The operator's bypass: scoped to one attempt on one device, and warned
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_operator_bypass_skips_the_local_step_for_the_device_it_names()
    {
        // A frame may be somewhere nobody can stand, so the local step has to be skippable — with
        // an acknowledgement of the risk carried in the authorisation itself.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.AuthoriseUnattended("TEST-DEVICE");

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Flashed);
        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task A_bypass_naming_another_frame_skips_nothing_here()
    {
        // The property that makes this "one device" rather than "a fleet default". §3.4's settings
        // are fleet defaults with per-device overrides, so a bypass that were merely a word would
        // switch the local approval off across the whole fleet the moment somebody set it at fleet
        // level. Naming the device means a fleet-wide push bypasses on exactly one frame.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.AuthoriseUnattended("SOME-OTHER-FRAME");

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AwaitingLocalApproval, outcome.Refusal);
        Assert.Null(fixture.Consumed);
        Assert.Equal(ArrayFlashPhase.Asking, fixture.Screen?.Phase);
        Assert.DoesNotContain(
            fixture.Processes.Commands,
            line => line.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_bypass_is_spent_by_the_same_write_that_spends_the_authorisation()
    {
        // Single-use, through the mechanism that already achieves it rather than through a second
        // one. The whole authorisation string — bypass included — is written to the card before
        // dfu-util starts, and an authorisation equal to the recorded one is refused for ever, so
        // there is no flag anywhere that can be left switched on.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.AuthoriseUnattended("TEST-DEVICE");

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(fixture.Authorisation, fixture.Consumed);
        Assert.Contains(ArrayFirmwareFlash.UnattendedPrefix, fixture.Consumed!, StringComparison.Ordinal);

        // A second look at the same setting writes nothing, however many times it is taken.
        fixture.Roll("2 0 6");
        var again = await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AlreadyConsumed, again.Refusal);
        Assert.Single(
            fixture.Processes.Commands,
            line => line.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unattended_write_carries_its_warnings_into_the_trail()
    {
        // Six months later, "was anybody standing there?" is the first question anybody asks about
        // a unit that came back wrong. An event that cannot answer it makes the answer unknowable,
        // so the trail records both that nobody was asked and exactly what was accepted instead.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.AuthoriseUnattended("TEST-DEVICE");

        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        var written = Assert.Single(fixture.Telemetry.Events, e => e.Kind == DeviceEventKinds.ArrayFlash);
        Assert.Contains("Nobody at the frame was asked", written.Summary, StringComparison.Ordinal);

        foreach (var warning in ArrayFirmwareFlash.UnattendedWarning)
        {
            Assert.Contains(warning, written.Summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_attended_write_records_that_somebody_agreed_to_it()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        var flash = fixture.Flash(reuseWindow: true);
        await flash.TickAsync(TestContext.Current.CancellationToken);
        fixture.HoldTheScreen();
        await flash.TickAsync(TestContext.Current.CancellationToken);

        var written = Assert.Single(fixture.Telemetry.Events, e =>
            e.Kind == DeviceEventKinds.ArrayFlash && e.Summary.Contains("Wrote", StringComparison.Ordinal));

        Assert.Contains("Somebody standing at the frame agreed to it", written.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Nobody at the frame was asked", written.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bypass_token_is_a_sentence_nobody_types_by_accident_and_needs_a_device()
    {
        // It states what is being accepted rather than abbreviating it, and it is worthless without
        // a device id after it — a bare token that bypassed everywhere is precisely the fleet-wide
        // switch this design exists to make impossible.
        Assert.Contains("unattended", ArrayFirmwareFlash.UnattendedPrefix, StringComparison.Ordinal);
        Assert.Contains("mains", ArrayFirmwareFlash.UnattendedPrefix, StringComparison.Ordinal);
        Assert.EndsWith("=", ArrayFirmwareFlash.UnattendedPrefix, StringComparison.Ordinal);
        Assert.True(ArrayFirmwareFlash.UnattendedPrefix.Length > 40);

        var bare = ArrayFlashAuthorisation.Parse("abc:ticket " + ArrayFirmwareFlash.UnattendedPrefix);
        Assert.Null(bare.UnattendedDeviceId);
        Assert.False(bare.BypassesLocalApproval("TEST-DEVICE"));

        var named = ArrayFlashAuthorisation.Parse(
            "abc:ticket " + ArrayFirmwareFlash.UnattendedPrefix + "TEST-DEVICE");

        Assert.True(named.BypassesLocalApproval("TEST-DEVICE"));
        Assert.False(named.BypassesLocalApproval("ANOTHER-DEVICE"));
        Assert.True(named.BypassNamesAnotherDevice("ANOTHER-DEVICE"));

        // And the warnings it accepts are carried on the frame, beside the code that acts on it.
        Assert.NotEmpty(ArrayFirmwareFlash.UnattendedWarning);
        Assert.Contains(
            ArrayFirmwareFlash.UnattendedWarning,
            warning => warning.Contains("Mains loss", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // A wedged board: what the frame can see, and the way back it puts on its own screen
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_board_that_never_came_back_gets_the_way_back_on_the_frame_s_own_screen()
    {
        // The detectable case, and the reason it has to be a screen: an array that will not
        // enumerate cannot beep, cannot answer the control tool and has no other way to say
        // anything. The evidence is durable — a marker a previous process left, and an empty bus.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Before = command =>
        {
            if (command.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal))
            {
                throw new OperationCanceledException("the power went off");
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Flash().TickAsync(TestContext.Current.CancellationToken));

        fixture.Processes.Before = null;
        fixture.DetachArrays();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);
        var prompt = Assert.IsType<ArrayFlashPrompt>(fixture.Screen);

        Assert.Equal(ArrayFlashRefusal.PreviousFlashUnfinished, outcome.Refusal);
        Assert.Equal(ArrayFlashPhase.Wedged, prompt.Phase);

        // The vendor's own Safe Mode gesture, in the order a pair of hands does it.
        var body = string.Join(" ", prompt.Lines);
        Assert.Contains("Take the power away", body, StringComparison.Ordinal);
        Assert.Contains("Mute button", body, StringComparison.Ordinal);
        Assert.Contains("put the power back on", body, StringComparison.Ordinal);
        Assert.Contains("red light that blinks", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_board_that_is_still_answering_is_not_told_it_is_wedged()
    {
        // The honest half of the boundary. A unit that enumerates has not been proven well — this
        // agent has no reading that separates a good flash from a bad one beyond a version a
        // misbehaving unit can still report correctly — so the screen says a write was interrupted
        // and says the frame cannot tell, rather than claiming either.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();
        fixture.Processes.Before = command =>
        {
            if (command.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal))
            {
                throw new OperationCanceledException("the agent is restarting");
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Flash().TickAsync(TestContext.Current.CancellationToken));

        fixture.Processes.Before = null;
        await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        var prompt = Assert.IsType<ArrayFlashPrompt>(fixture.Screen);
        Assert.Equal(ArrayFlashPhase.Unfinished, prompt.Phase);

        var body = string.Join(" ", prompt.Lines);
        Assert.Contains("cannot tell", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Mute button", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recovery_route_records_the_two_details_that_are_easy_to_miss()
    {
        // Both were established once by a previous workstream and would otherwise be rediscovered
        // by whoever is holding the board. The first reads as a failure and is not; reacting to it
        // by retrying is the documented route from a recoverable board to an unrecoverable one.
        var steps = string.Join(" ", ArrayFlashRecovery.OperatorSteps);

        Assert.Contains("96%", steps, StringComparison.Ordinal);
        Assert.Contains("expected outcome, not a failure", steps, StringComparison.Ordinal);
        Assert.Contains("Power-cycle", steps, StringComparison.Ordinal);
        Assert.Contains("fails at 0%", steps, StringComparison.Ordinal);

        // And the erase comes before the power cycle, which comes before the next write. Out of
        // order, the download fails at 0% and the person doing it has no idea why.
        var erase = ArrayFlashRecovery.OperatorSteps.ToList().FindIndex(
            step => step.Contains("Erase", StringComparison.Ordinal));
        var cycle = ArrayFlashRecovery.OperatorSteps.ToList().FindIndex(
            step => step.Contains("Power-cycle", StringComparison.Ordinal));
        var write = ArrayFlashRecovery.OperatorSteps.ToList().FindIndex(
            step => step.Contains("Write the pinned fallback", StringComparison.Ordinal));

        Assert.True(erase >= 0 && erase < cycle && cycle < write);

        // And the latch a person clears by hand is named, because nothing else lets the frame flash
        // again and a route that ends with a frame nobody can use is not a route.
        Assert.Contains(ArrayFlashWindow.MarkerFileName, steps, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // The hardware gate: refuse loudly rather than proceed hopefully
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_unit_running_firmware_this_build_has_never_seen_is_refused()
    {
        // The closest thing to a hardware gate that exists. A unit on firmware nobody here has ever
        // seen is evidence of a unit outside what this build was written against — and the correct
        // answer to that is a refusal that says so, not a 933 KB write placed on a hope.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Attach("1-1", "0300", "…030");
        fixture.Authorise();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.ArrayNotRecognised, outcome.Refusal);
        Assert.Contains("never been told about", outcome.Summary, StringComparison.Ordinal);
        Assert.Null(fixture.Consumed);
        Assert.DoesNotContain(
            fixture.Processes.Commands,
            line => line.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_unit_built_for_another_audio_topology_is_refused()
    {
        // The strongest real gate in the set. Upstream publishes six-channel and 48 kHz builds under
        // names one character apart, and writing the two-channel image onto a unit configured for
        // six changes the frame's audio topology underneath every mixer setting in the catalog.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.SeedTool(profile: "ua-io16-6ch-sqr");
        fixture.Authorise();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.ArrayNotRecognised, outcome.Refusal);
        Assert.Contains(XvfFirmwarePin.Profile, outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("ua-io16-6ch-sqr", outcome.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Processes.Commands,
            line => line.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_unit_whose_two_readings_disagree_is_refused()
    {
        // The descriptor and the control interface are independent routes to one fact and this
        // build reads both anyway. A unit on which they disagree is a unit nothing here can
        // describe, and the honest answer to "which is true" is that nobody knows.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.SeedTool(version: "2 0 10");
        fixture.Authorise();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.ArrayNotRecognised, outcome.Refusal);
        Assert.Contains("readings disagree", outcome.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Processes.Commands,
            line => line.StartsWith(ArrayFirmwareFlash.DfuUtil + " ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_frame_that_cannot_read_the_unit_refuses_rather_than_hoping()
    {
        // An unreadable identity is a refusal and not a shrug: writing without knowing which build a
        // unit is configured for is exactly the hopeful proceeding the gate exists to stop. It also
        // names something the reconciler can fix, since the tool is an ordinary resource.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.RemoveTool();
        fixture.Authorise();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.ArrayNotRecognised, outcome.Refusal);
        Assert.Contains("control tool is not installed", outcome.Summary, StringComparison.Ordinal);
        Assert.Null(fixture.Consumed);
    }

    [Fact]
    public async Task A_recognised_unit_reaches_the_trail_with_every_field_that_could_be_read()
    {
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise();

        var flash = fixture.Flash();
        await flash.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayGateVerdict.Recognised, flash.Verdict);

        var identity = Assert.IsType<ArrayIdentity>(flash.Identity);
        Assert.Equal(XvfArrayUsb.VendorId, identity.VendorId);
        Assert.Equal(XvfArrayUsb.ProductId, identity.ProductId);
        Assert.Equal("2 0 6", identity.DescriptorVersion);
        Assert.Equal("2 0 6", identity.ControlVersion);
        Assert.Equal(XvfFirmwarePin.Profile, identity.BuildConfiguration);
        Assert.Equal("3f08f630b41b8bce11cb2f45857ba49f22f9d507", identity.BuildRepositoryHash);

        var written = Assert.Single(fixture.Telemetry.Events, e =>
            e.Kind == DeviceEventKinds.ArrayFlash && e.Summary.Contains("Wrote", StringComparison.Ordinal));

        Assert.Contains(identity.Describe(), written.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_never_claims_to_read_a_board_revision()
    {
        // Upstream issue #32 reports the target firmware not booting on a V1.1 board, so a revision
        // gate is the first thing anybody would reach for — and it cannot be written. The revision
        // is not in the USB descriptors and not in any of the 177 commands of the pinned command
        // map, every identity one of which describes the firmware or the unit. It is silkscreen.
        // A field that pretended to carry it would be the most dangerous kind of fiction here.
        var fields = typeof(ArrayIdentity)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain(fields, name => name.Contains("Revision", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Board", StringComparison.OrdinalIgnoreCase));

        // And the refusal says so out loud, so nobody reads a refusal as "the board is wrong".
        var explanation = ArrayHardwareGate.Explain(ArrayGateVerdict.UnknownFirmware, null);
        Assert.Contains("board revision is not readable in software at all", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_known_firmware_set_is_what_this_repository_has_pinned_or_observed()
    {
        // A list a human edits, which is the property that makes it a gate rather than a guess: it
        // can only ever say what has already been established, so a version nobody here has seen
        // refuses by construction rather than by somebody remembering to add a case.
        Assert.Contains(XvfFirmwarePin.Current.Target.Version, ArrayHardwareGate.KnownFirmware);
        Assert.Contains(XvfFirmwarePin.Current.Fallback.Version, ArrayHardwareGate.KnownFirmware);
        Assert.Contains("2 0 10", ArrayHardwareGate.KnownFirmware);
        Assert.DoesNotContain("2 0 9", ArrayHardwareGate.KnownFirmware);
    }

    [Fact]
    public void A_build_configuration_is_read_through_the_padding_the_tool_prints()
    {
        // BLD_MSG, BLD_HOST and BLD_MODIFIED arrive NUL-padded to fixed widths and the tool prints
        // them raw, so they look like trailing spaces and are not. A value carrying its padding
        // compares unequal to the same value read anywhere else, which would make the gate refuse
        // every unit it was ever pointed at.
        const string Reply = "Found device\nBLD_MSG ua-io16-sqr\0\0\0\0\0\0\0\0\0\0\n";

        Assert.Equal(
            "ua-io16-sqr",
            ArrayHardwareGate.Field(Reply, ArrayHardwareGate.BuildConfigurationCommand));

        Assert.Null(ArrayHardwareGate.Field(Reply, ArrayHardwareGate.BuildHashCommand));
    }

    // ---------------------------------------------------------------------------------------
    // Both surfaces, one set of words
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_firmware_screen_is_shown_on_a_frame_with_nothing_wrong_with_it()
    {
        // The one field on the status that outranks the ladder at the surface. Every other narration
        // is hidden on a converged frame, because a converged frame shows photographs — and this
        // screen appears *only* on frames with nothing wrong with them, so a page that consulted
        // ProductRuns first would never draw it at all.
        var status = AgentStatusFactory.Green() with
        {
            ArrayFlash = ArrayFlashVoice.Asking(answerable: true, contact: null),
        };

        var frame = BrowserStage.Compose(status, DateTimeOffset.UnixEpoch);

        Assert.True(frame.ProductRuns);
        Assert.Equal("asking", frame.FlashPhase);
        Assert.Equal(status.ArrayFlash.Headline, frame.FlashHeadline);
        Assert.Equal(status.ArrayFlash.Lines, frame.FlashLines);
        Assert.Equal("Yes — go ahead", frame.FlashAffordance);
    }

    [Fact]
    public void Neither_surface_composes_a_word_of_its_own_about_a_write()
    {
        // Decision 83's rule, applied before there is a second implementation to disagree with: the
        // console and the page render the same record rather than each working out what to say. The
        // failure this prevents is the one nobody notices, because it appears on whichever surface
        // the panel is not currently showing.
        var status = AgentStatusFactory.Green() with
        {
            ArrayFlash = ArrayFlashVoice.Wedged(answerable: true, contact: null),
        };

        var page = BrowserStage.Compose(status, DateTimeOffset.UnixEpoch);
        var console = StageRenderer.Render(status, DateTimeOffset.UnixEpoch, 0, 160, 60, colour: false);

        Assert.Equal(status.ArrayFlash.Headline, page.FlashHeadline);
        Assert.Contains(status.ArrayFlash.Headline, console, StringComparison.Ordinal);
        Assert.Contains("Mute button", console, StringComparison.Ordinal);

        // And the console gives the whole screen over to it rather than putting it below the
        // ordinary narration, which is where a person would read past the part that matters.
        Assert.DoesNotContain(ReconcileVoice.RepairingHeadline, console, StringComparison.Ordinal);
        Assert.DoesNotContain(status.Condition.Headline, console, StringComparison.Ordinal);
    }

    [Fact]
    public void The_accent_reads_as_waiting_on_a_person_rather_than_as_a_healthy_frame()
    {
        // A frame asking somebody not to unplug it is green by the ladder's reckoning, and green is
        // exactly the wrong signal across a room: the whole point of the screen is that something
        // needs a person. Both surfaces take the same composed accent by name (decision 83).
        var green = AgentStatusFactory.Green();

        var asking = green with { ArrayFlash = ArrayFlashVoice.Asking(true, null) };
        var writing = green with { ArrayFlash = ArrayFlashVoice.Writing() };
        var done = green with { ArrayFlash = ArrayFlashVoice.Finished(succeeded: true, true, null) };
        var bad = green with { ArrayFlash = ArrayFlashVoice.Finished(succeeded: false, true, null) };

        Assert.Equal("green", StagePalette.NameOf(StagePalette.For(green)));
        Assert.Equal("blue", StagePalette.NameOf(StagePalette.For(asking)));
        Assert.Equal("blue", StagePalette.NameOf(StagePalette.For(writing)));
        Assert.Equal("green", StagePalette.NameOf(StagePalette.For(done)));
        Assert.Equal("red", StagePalette.NameOf(StagePalette.For(bad)));

        Assert.Equal("blue", BrowserStage.Compose(asking, DateTimeOffset.UnixEpoch).Accent);
    }

    [Fact]
    public void The_console_labels_the_hold_bar_with_what_the_hold_will_actually_do()
    {
        // The bar is drawn from the touch state and labelled from the screen, and a bar labelled
        // "Try again" counting out a firmware approval is decision 77's defect in words instead of
        // in coordinates: an affordance that answers something other than what it appears to.
        var began = DateTimeOffset.UnixEpoch;
        var status = AgentStatusFactory.Green() with
        {
            ArrayFlash = ArrayFlashVoice.Asking(answerable: true, contact: null),
            Touch = new TouchRetryState("/dev/input/event4", ArrayFlashApproval.ApprovalHold, began),
        };

        var console = StageRenderer.Render(
            status, began + TimeSpan.FromSeconds(2), 0, 160, 60, colour: false);

        Assert.Contains("Yes", console, StringComparison.Ordinal);
        Assert.Contains("keep holding", console, StringComparison.Ordinal);
        Assert.DoesNotContain("Try again", console, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_page_draws_the_write_before_it_consults_the_ladder()
    {
        // Asserted against the shipped source because that file is the whole of what reaches the
        // panel. Every other narration on that surface is hidden on a converged frame, and this
        // screen appears only on converged frames — so a page that checked `productRuns` first
        // would compose a perfect message and render none of it, which is the exact shape of the
        // defect that once served a new stage to a browser that never drew it.
        var page = AgentButtonTests.Asset("frame-stage.js");

        var flash = page.IndexOf("stage.flashHeadline", StringComparison.Ordinal);
        var runs = page.IndexOf("if (stage.productRuns)", StringComparison.Ordinal);

        Assert.True(flash >= 0 && runs >= 0);
        Assert.True(flash < runs);

        // It renders the words it is sent rather than any of its own, and its button sends the one
        // kind whose meaning is decided by whatever the agent currently has on the panel.
        Assert.Contains("stage.flashLines", page, StringComparison.Ordinal);
        Assert.Contains("stage.flashAffordance", page, StringComparison.Ordinal);
        Assert.Contains(PageMessage.KindArrayFlash, page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_write_in_progress_offers_nothing_on_either_surface()
    {
        // The one screen with nothing a person may usefully do. A button on it invites exactly the
        // interruption the whole feature exists to prevent.
        var status = AgentStatusFactory.Green() with { ArrayFlash = ArrayFlashVoice.Writing() };

        Assert.Null(BrowserStage.Compose(status, DateTimeOffset.UnixEpoch).FlashAffordance);
        Assert.Equal(string.Empty, ArrayFlashVoice.HoldLine(status.ArrayFlash));
    }

    [Fact]
    public async Task A_hold_and_a_button_press_mean_whatever_the_screen_currently_says()
    {
        // One entry point for both surfaces and both meanings. Which of "yes, go ahead" and "OK, put
        // this away" a press means is decided by what is on the panel rather than by which caller
        // arrived, so a page that had fallen behind cannot approve a write at a screen that had
        // moved on — and a press can never mean something the sentence above it did not say.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Authorise(approved: false);

        var flash = fixture.Flash(reuseWindow: true);

        // Nothing on the screen: a press does nothing at all.
        Assert.False(fixture.Approval.Answer("the browser stage"));

        await flash.TickAsync(TestContext.Current.CancellationToken);
        Assert.True(fixture.Approval.Answer("the browser stage"));

        await flash.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ArrayFlashPhase.Succeeded, fixture.Screen?.Phase);

        // The same press again, under a different screen, now means "put it away".
        Assert.True(fixture.Approval.Answer("a hold on the panel"));
        Assert.Null(fixture.Screen);
    }

    [Fact]
    public async Task A_unit_already_on_the_target_spends_the_authorisation_even_if_it_is_unrecognised()
    {
        // Ordering, and it is deliberate. The gate runs *after* the already-at-target check, so an
        // unrecognised unit that needs no write still spends the authorisation — which is what keeps
        // "a later array swap cannot be flashed by nobody's decision" true. A gate that ran first
        // would leave the authorisation armed on a frame whose array somebody then changed.
        using var fixture = new FlashFixture();
        await fixture.ReadyToFlashAsync();
        fixture.Roll("2 1 0");
        fixture.SeedTool(profile: "ua-io16-6ch-sqr");
        fixture.Authorise();

        var outcome = await fixture.Flash().TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ArrayFlashRefusal.AlreadyAtTarget, outcome.Refusal);
        Assert.Equal(fixture.Authorisation, fixture.Consumed);
    }
}

/// <summary>A frame with a synthetic pin, a synthetic bus and a recording <c>dfu-util</c>.</summary>
/// <remarks>
/// The pin is synthetic for the same reason <c>XvfHostFixture</c>'s is: the real one names three
/// files totalling six megabytes, and a suite that materialised those would be asserting the network
/// rather than the logic. What is <i>not</i> synthetic is the shape — three images, three roles,
/// real SHA-256 over real bytes through the real installer and the real filesystem.
/// </remarks>
internal sealed class FlashFixture : IDisposable
{
    public const string CardsWithArray = " 0 [Array          ]: USB-Audio - reSpeaker XVF3800 4-Mic Array\n";

    private const string BusPath = "1-1";

    private readonly Dictionary<string, byte[]> _payloads = new(StringComparer.Ordinal);
    private readonly FakeUserSession _session = new();
    private ArrayFlashWindow? _window;
    private ArrayFlashApproval? _approval;

    public FlashFixture()
    {
        Files = new TemporaryFiles();

        var images = new List<XvfFirmwareImage>();
        foreach (var (name, directory, role, version) in ((string, string, XvfFirmwareRole, string)[])
        [
            ("respeaker_xvf3800_usb_dfu_firmware_v2.1.0.bin", "usb", XvfFirmwareRole.Target, "2 1 0"),
            ("respeaker_xvf3800_usb_dfu_firmware_v2.0.6.bin", "usb", XvfFirmwareRole.Fallback, "2 0 6"),
            ("4mb_all_ff.bin", "recover", XvfFirmwareRole.Recovery, ""),
        ])
        {
            // Seeded from the name's own bytes, not from its length. The first version of this
            // fixture used the length, and the target and the fallback have names of exactly the
            // same length — so the two images were byte-identical and hashed the same, which made
            // "authorising the fallback's digest" indistinguishable from authorising the target's.
            // The digest test caught it, which is the behaviour under test working on the fixture.
            var seed = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
            var payload = new byte[2048 + name.Length];
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] = (byte)((index + seed[index % seed.Length]) % 251);
            }

            _payloads[name] = payload;
            images.Add(new XvfFirmwareImage(
                name,
                directory,
                new string((char)('a' + images.Count), 40),
                Convert.ToHexStringLower(SHA256.HashData(payload)),
                payload.Length,
                role,
                version,
                "a synthetic image for the suite"));
        }

        Pin = new XvfFirmwarePin
        {
            Owner = "respeaker",
            Repository = "reSpeaker_XVF3800_USB_4MIC_ARRAY",
            Images = images,
            ReviewedUtc = new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero),
        };

        Images = new XvfFirmwareInstaller(Files.Files, Download, new RecordingLog(), Pin);
    }

    public TemporaryFiles Files { get; }

    public XvfFirmwarePin Pin { get; }

    public XvfFirmwareInstaller Images { get; }

    public StubXvfHostDownload Download { get; } = new();

    public FlashProcessRunner Processes { get; } = new();

    public NullReconcileTelemetry Telemetry { get; } = new();

    public ManualClock Clock { get; } = new();

    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    public bool CallActive { get; set; }

    public bool RestartPending { get; set; }

    /// <summary>The frame's own screen, which is where the local approval is taken.</summary>
    public AgentStatusHub Hub { get; } = new(AgentStatusFactory.Green());

    /// <summary>The interlock that is a person.</summary>
    public ArrayFlashApproval Approval => _approval ??= new ArrayFlashApproval(Hub, Clock, new RecordingLog());

    /// <summary>Whether the array comes back on the bus reporting the target after a write.</summary>
    public bool ReEnumerate { get; set; } = true;

    /// <summary>The authorisation string this fixture last wrote.</summary>
    public string Authorisation { get; private set; } = string.Empty;

    /// <summary>A frame that could flash if somebody authorised one: images in place, array on 2.0.6.</summary>
    /// <remarks>
    /// It also seeds the control tool and a working touchscreen, because both are now part of what
    /// "could flash" means: the hardware gate refuses a unit whose build configuration it cannot
    /// read, and the local approval cannot be given on a frame with no panel to give it on. A frame
    /// missing either is a case with its own test rather than the background of every other one.
    /// </remarks>
    public async Task ReadyToFlashAsync()
    {
        Seed(AlsaCards.CardsPath, CardsWithArray);
        Seed(ArrayFirmwareFlash.DfuUtilPath, "#!/bin/false\n");
        ServeEverything();

        var installed = await Images.InstallAsync(TestContext.Current.CancellationToken);
        Assert.Equal(XvfFirmwareInstallResult.Installed, installed);

        Attach(BusPath, "0206", "…030");
        SeedTool();
        AttachPanel();
    }

    /// <summary>Puts the control tool on the frame and scripts what the unit answers through it.</summary>
    /// <param name="profile">What <c>BLD_MSG</c> reports. Defaults to the profile the pin is for.</param>
    /// <param name="version">
    /// What <c>VERSION</c> reports, or null to answer whatever the synthetic bus currently shows —
    /// which is what a real unit does, and what keeps the gate's two-readings-agree check honest
    /// across a test that rolls the array.
    /// </param>
    public void SeedTool(string? profile = XvfFirmwarePin.Profile, string? version = null)
    {
        var directory = XvfHost.ToolDirectory(XvfHost.AgentDirectory);
        Files.Seed(directory + "/" + XvfHost.Binary, "#!/bin/false\n");

        const string Banner = "Device (USB)::device_init() -- Found device VID: 10374 PID: 26 interface: 3\n";
        var prefix = $"env -C {directory} LD_LIBRARY_PATH={directory} {directory}/{XvfHost.Binary} ";

        Processes.Script = line =>
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            var command = line[prefix.Length..].Trim();

            if (string.Equals(command, XvfHost.VersionCommand, StringComparison.Ordinal))
            {
                var reported = version ?? Running() ?? "2 0 6";
                return new ProcessResult(0, Banner + XvfHost.VersionCommand + " " + reported + "\n", string.Empty);
            }

            if (string.Equals(command, ArrayHardwareGate.BuildConfigurationCommand, StringComparison.Ordinal))
            {
                // NUL-padded to a fixed width, exactly as the real tool prints it. The parser has to
                // strip that; a fixture that sent a clean string would never exercise it.
                return profile is null
                    ? new ProcessResult(0, Banner, string.Empty)
                    : new ProcessResult(
                        0,
                        Banner + ArrayHardwareGate.BuildConfigurationCommand + " " + profile + new string('\0', 39) + "\n",
                        string.Empty);
            }

            if (string.Equals(command, ArrayHardwareGate.BuildHashCommand, StringComparison.Ordinal))
            {
                return new ProcessResult(
                    0,
                    Banner + ArrayHardwareGate.BuildHashCommand + " 3f08f630b41b8bce11cb2f45857ba49f22f9d507\n",
                    string.Empty);
            }

            return null;
        };
    }

    /// <summary>Takes the control tool away, so the unit's build configuration cannot be read.</summary>
    public void RemoveTool()
    {
        Files.Files.DeleteFile(XvfHost.ToolDirectory(XvfHost.AgentDirectory) + "/" + XvfHost.Binary);
        Processes.Script = null;
    }

    /// <summary>The firmware version the synthetic bus currently shows.</summary>
    public string? Running() =>
        XvfArrayUsb.Version(Files.Files.ReadText(XvfArrayUsb.DevicesPath + "/" + BusPath + "/bcdDevice"));

    /// <summary>Says this frame has a working touchscreen, which is what makes it answerable.</summary>
    public void AttachPanel() =>
        Hub.Publish(status => status with
        {
            Touch = new TouchRetryState("/dev/input/event4", ArrayFlashApproval.ApprovalHold, null),
        });

    /// <summary>Says this frame has no touchscreen, so nobody at it can agree to anything.</summary>
    public void DetachPanel() => Hub.Publish(status => status with { Touch = TouchRetryState.None });

    /// <summary>Somebody at the frame holds the screen, taking whatever it is offering.</summary>
    /// <remarks>
    /// The genuine path: it goes through the same method the console's completed hold and the
    /// browser's button both call, and it answers whatever the agent currently has on the panel
    /// rather than asserting an approval into place.
    /// </remarks>
    public bool HoldTheScreen() => Approval.Answer("a hold on the panel");

    public void Seed(string path, string content) => Files.Seed(path, content);

    public void ServeEverything()
    {
        foreach (var (name, payload) in _payloads)
        {
            Download.Payloads[name] = payload;
        }
    }

    public void Corrupt(string name)
    {
        var payload = (byte[])_payloads[name].Clone();
        payload[^1] ^= 0xFF;
        Download.Payloads[name] = payload;
    }

    public void Truncate(string name) => Download.Payloads[name] = _payloads[name][..^16];

    public bool Exists(XvfFirmwareImage image, string suffix = "") =>
        Files.Files.FileExists(XvfFirmwareInstaller.PathOf(image) + suffix);

    public void Damage(XvfFirmwareImage image) =>
        Files.Files.WriteText(XvfFirmwareInstaller.PathOf(image), "not the pinned bytes");

    public void Remove(XvfFirmwareImage image) =>
        Files.Files.DeleteFile(XvfFirmwareInstaller.PathOf(image));

    /// <summary>Puts one array on the synthetic USB bus.</summary>
    public void Attach(string path, string bcdDevice, string serial)
    {
        var directory = XvfArrayUsb.DevicesPath + "/" + path;

        Files.Seed(directory + "/idVendor", XvfArrayUsb.VendorId + "\n");
        Files.Seed(directory + "/idProduct", XvfArrayUsb.ProductId + "\n");
        Files.Seed(directory + "/bcdDevice", bcdDevice + "\n");
        Files.Seed(directory + "/serial", serial + "\n");
    }

    public void DetachArrays()
    {
        foreach (var entry in Files.Files.ListDirectories(XvfArrayUsb.DevicesPath))
        {
            foreach (var field in new[] { "idVendor", "idProduct", "bcdDevice", "serial" })
            {
                Files.Files.DeleteFile(entry + "/" + field);
            }
        }
    }

    /// <summary>Sets what the single attached array reports, in <c>xvf_host</c>'s spelling.</summary>
    public void Roll(string version)
    {
        var descriptor = version switch
        {
            "2 1 0" => "0210",
            "2 0 6" => "0206",
            _ => "020a",
        };

        Files.Seed(XvfArrayUsb.DevicesPath + "/" + BusPath + "/bcdDevice", descriptor + "\n");
    }

    /// <summary>Writes an authorisation naming the pinned target's digest.</summary>
    /// <param name="ticket">Whatever the operator wrote after the colon.</param>
    /// <param name="approved">
    /// Whether somebody at the frame has already agreed to it.
    /// </param>
    /// <remarks>
    /// <b>Approved by default, and the default is stated here rather than assumed in twenty tests.</b>
    /// Every test about some other interlock wants a frame where the household has already said yes,
    /// so that what it is asserting is the interlock it names. The tests about the local approval
    /// itself pass <c>approved: false</c> and drive the screen through
    /// <see cref="HoldTheScreen"/> — which is the real path — so removing the approval requirement
    /// breaks those and nothing else silently covers for it.
    /// </remarks>
    public void Authorise(string ticket = "bench-2026-08-23", bool approved = true)
    {
        Authorisation = Pin.Target.Sha256 + ":" + ticket;
        Settings[ArrayFirmwareFlash.AuthorisationKey] = Authorisation;

        if (approved)
        {
            Approval.Approve(Authorisation, "the fixture, standing in for somebody at the frame");
        }
    }

    /// <summary>An authorisation carrying the operator's unattended bypass for one device.</summary>
    public void AuthoriseUnattended(string deviceId, string ticket = "bench-2026-08-23")
    {
        Authorisation = Pin.Target.Sha256 + ":" + ticket + " "
            + ArrayFirmwareFlash.UnattendedPrefix + deviceId;
        Settings[ArrayFirmwareFlash.AuthorisationKey] = Authorisation;
    }

    public void ClearMarker() => Files.Store.Delete(ArrayFlashWindow.MarkerFileName);

    /// <summary>What is on the frame's screen right now, or null.</summary>
    public ArrayFlashPrompt? Screen => Hub.Current.ArrayFlash;

    /// <summary>The whole screen as one string, for asserting on what it actually says.</summary>
    public string ScreenText => Screen is { } prompt
        ? prompt.Headline + "\n" + string.Join("\n", prompt.Lines) + "\n" + (prompt.Affordance ?? string.Empty)
        : string.Empty;

    /// <summary>The authorisation this frame has durably recorded as spent, or null.</summary>
    public string? Consumed => Files.Store.ReadText(ArrayFirmwareFlash.ConsumedFileName)?.Trim();

    /// <summary>
    /// A new agent process, as far as the approval is concerned. The screen is cleared with it.
    /// </summary>
    public void RestartApproval()
    {
        _approval = null;
        Hub.Publish(status => status with { ArrayFlash = null });
    }

    /// <summary>A flash over this frame. A fresh window each time, which is what a restart leaves.</summary>
    public ArrayFirmwareFlash Flash(bool reuseWindow = false)
    {
        if (!reuseWindow)
        {
            _window = new ArrayFlashWindow(Files.Store, Clock);
        }

        Processes.OnWrite = () =>
        {
            if (ReEnumerate)
            {
                Roll(Pin.Target.Version);
            }
        };

        return new ArrayFirmwareFlash(new ArrayFlashServices
        {
            Tool = new XvfHost(Files.Files, Processes, _session),
            Files = Files.Files,
            Processes = Processes,
            Installer = Images,
            Window = _window ??= new ArrayFlashWindow(Files.Store, Clock),
            Approval = Approval,
            Telemetry = Telemetry,
            Store = Files.Store,
            Clock = Clock,
            Log = new RecordingLog(),
            Values = new FleetValues(key => Settings.GetValueOrDefault(key)),
            DeviceId = "TEST-DEVICE",
            CallActive = () => CallActive,
            RestartPending = () => RestartPending,
        });
    }

    /// <summary>The observe-only reporter, over the same frame.</summary>
    public ArrayFirmwareReporter Reporter() => new(
        new XvfHost(Files.Files, Processes, _session),
        Files.Files,
        Telemetry,
        Files.Store,
        Clock,
        new RecordingLog())
    {
        DeviceId = "TEST-DEVICE",
    };

    public void Dispose() => Files.Dispose();
}

/// <summary>Records commands, answers with a script, and can act while a command "runs".</summary>
internal sealed class FlashProcessRunner : IProcessRunner
{
    public List<string> Commands { get; } = [];

    public ProcessResult Result { get; set; } = new(0, "Done!", string.Empty);

    /// <summary>Answers one command line, or null to fall through to the default.</summary>
    /// <remarks>
    /// A function rather than a dictionary because the control tool's answers have to move with the
    /// synthetic bus: the hardware gate reads the firmware version through the descriptor *and*
    /// through the tool and refuses when the two disagree, so a fixture that scripted a fixed
    /// version would make every test that rolls the array read as a disagreeing unit.
    /// </remarks>
    public Func<string, ProcessResult?>? Script { get; set; }

    /// <summary>Runs before the command's result is produced, with the command line.</summary>
    public Action<string>? Before { get; set; }

    /// <summary>Runs after a <c>dfu-util</c> command, modelling the device re-enumerating.</summary>
    public Action? OnWrite { get; set; }

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var line = executable + " " + string.Join(' ', arguments);
        Commands.Add(line);
        Before?.Invoke(line);

        if (string.Equals(executable, ArrayFirmwareFlash.DfuUtil, StringComparison.Ordinal))
        {
            OnWrite?.Invoke();
            return Task.FromResult(Result);
        }

        return Task.FromResult(Script?.Invoke(line) ?? new ProcessResult(0, string.Empty, string.Empty));
    }
}

/// <summary>A binary swap that records instead of replacing anything.</summary>
internal sealed class RecordingBinarySwap : IBinarySwap
{
    public string TargetPath => "/usr/local/bin/fl-agent";

    public int Applied { get; private set; }

    public Task<SwapResult> ApplyAsync(Stream payload, AgentRelease release, CancellationToken cancellationToken)
    {
        Applied++;
        return Task.FromResult(SwapResult.Applied);
    }
}
