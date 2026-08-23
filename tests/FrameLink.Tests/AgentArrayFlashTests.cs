using System.Security.Cryptography;
using FrameLink.Agent.Firmware;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
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

        var markerDuringWrite = false;
        fixture.Processes.Before = _ => markerDuringWrite = fixture.Files.Store.Exists(ArrayFlashWindow.MarkerFileName);

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
        fixture.Processes.Before = _ => throw new OperationCanceledException("the agent is restarting");

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

        var command = Assert.Single(fixture.Processes.Commands);
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
        Assert.Single(fixture.Processes.Commands);
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

    /// <summary>Whether the array comes back on the bus reporting the target after a write.</summary>
    public bool ReEnumerate { get; set; } = true;

    /// <summary>The authorisation string this fixture last wrote.</summary>
    public string Authorisation { get; private set; } = string.Empty;

    /// <summary>A frame that could flash if somebody authorised one: images in place, array on 2.0.6.</summary>
    public async Task ReadyToFlashAsync()
    {
        Seed(AlsaCards.CardsPath, CardsWithArray);
        Seed(ArrayFirmwareFlash.DfuUtilPath, "#!/bin/false\n");
        ServeEverything();

        var installed = await Images.InstallAsync(TestContext.Current.CancellationToken);
        Assert.Equal(XvfFirmwareInstallResult.Installed, installed);

        Attach(BusPath, "0206", "…030");
    }

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
    public void Authorise(string ticket = "bench-2026-08-23")
    {
        Authorisation = Pin.Target.Sha256 + ":" + ticket;
        Settings[ArrayFirmwareFlash.AuthorisationKey] = Authorisation;
    }

    public void ClearMarker() => Files.Store.Delete(ArrayFlashWindow.MarkerFileName);

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

        return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
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
