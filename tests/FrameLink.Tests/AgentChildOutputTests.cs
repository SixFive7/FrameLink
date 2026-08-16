using FrameLink.Agent.Supervise;

namespace FrameLink.Tests;

/// <summary>
/// <b>A supervised child cannot rotate this frame's journal away</b>.
/// </summary>
/// <remarks>
/// <para>
/// The defect these pin, measured on the mule 2026-08-16. Immich Kiosk was running with no album
/// scope, could select no asset, and logged
/// <c>SaveOfflineAsset: generateViewData err="selecting asset: no assets found for random"</c>
/// about seven times a second — 1,288 lines in three minutes. Its output reached the journal
/// through the agent's own inherited stdout, under one <c>SyslogIdentifier=fl-agent</c>, so against
/// guide 12's <c>SystemMaxUse=64M</c> it evicted every archived journal file on the frame. The
/// earliest surviving <c>fl-agent</c> entry became the running process's own start line, and the
/// persistent journal that had root-caused two other defects the same night was gone.
/// </para>
/// <para>
/// Fixing the album silences that particular flood. These tests are about the other half: a child
/// that can erase the frame's own memory is a hazard whatever it is shouting about, so the bound
/// has to hold for the next chatty child too — which is why it lives on the pipe every supervised
/// child's output travels down rather than on the one message that happened to cause this.
/// </para>
/// </remarks>
public sealed class AgentChildOutputTests
{
    [Fact]
    public void A_child_within_its_budget_is_carried_through_unchanged()
    {
        var log = new RecordingLog();
        var clock = new ManualClock();
        var budget = new ChildOutputBudget(log, clock, "Immich Kiosk", 5, TimeSpan.FromMinutes(10));

        budget.Write("Serving asset 1");
        budget.Write("Serving asset 2");

        // The property the old inheriting arrangement had, and which this must not lose: the
        // child's lines land beside the agent's under one `journalctl -u fl-agent`, which is what
        // replaces `docker logs immich-kiosk` from guide 9 step 4. What is added is whose they are.
        Assert.Equal(
            ["Info: Immich Kiosk: Serving asset 1", "Info: Immich Kiosk: Serving asset 2"],
            log.Lines);
        Assert.Equal(0, budget.Dropped);
    }

    [Fact]
    public void The_end_of_stream_marker_and_blank_lines_spend_no_budget()
    {
        var log = new RecordingLog();
        var budget = new ChildOutputBudget(log, new ManualClock(), "Immich Kiosk", 2, TimeSpan.FromMinutes(10));

        // null is what Process's line-reading events deliver at end of stream, and a child that
        // ends every burst with a blank line would otherwise spend half its allowance on nothing.
        budget.Write(null);
        budget.Write(string.Empty);
        budget.Write("   ");
        budget.Write("a real line");
        budget.Write("another real line");
        budget.Write("this one is over budget");

        Assert.Equal(1, budget.Dropped);
    }

    [Fact]
    public void A_flood_is_clipped_and_the_clipping_is_announced_twice_and_only_twice()
    {
        var log = new RecordingLog();
        var clock = new ManualClock();
        var budget = new ChildOutputBudget(log, clock, "Immich Kiosk", 3, TimeSpan.FromMinutes(10));

        for (var line = 0; line < 200; line++)
        {
            budget.Write("SaveOfflineAsset: generateViewData err=\"selecting asset: no assets found for random\"");
        }

        // Three lines of the child, and one notice at the moment the budget ran out. The count
        // itself is owed until the window closes, because it is not known before then.
        Assert.Equal(4, log.Lines.Count);
        Assert.Equal(197, budget.Dropped);
        Assert.Contains("used its whole budget of 3 log lines per 10 minutes", log.Transcript, StringComparison.Ordinal);

        // The notice fires once per window, not once per dropped line — a suppression notice that
        // floods is the same defect wearing a different hat.
        Assert.Single(log.Lines, entry => entry.Contains("used its whole budget", StringComparison.Ordinal));

        clock.UtcNow += TimeSpan.FromMinutes(10);
        budget.Write("the window has rolled");

        // Bounded, and visibly truncated rather than silent: the count reaches the journal in the
        // same shape journald uses for its own drops.
        Assert.Contains(
            "Suppressed 197 lines from Immich Kiosk over the last 10 minutes (197 in total since this agent started)",
            log.Transcript,
            StringComparison.Ordinal);
        Assert.Contains("Immich Kiosk: the window has rolled", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_window_restores_the_allowance_so_a_transient_flood_does_not_silence_the_child()
    {
        var log = new RecordingLog();
        var clock = new ManualClock();
        var budget = new ChildOutputBudget(log, clock, "Immich Kiosk", 2, TimeSpan.FromMinutes(10));

        budget.Write("one");
        budget.Write("two");
        budget.Write("three — dropped");

        clock.UtcNow += TimeSpan.FromMinutes(10);

        budget.Write("four");
        budget.Write("five");
        budget.Write("six — dropped");

        Assert.Contains("Immich Kiosk: four", log.Transcript, StringComparison.Ordinal);
        Assert.Contains("Immich Kiosk: five", log.Transcript, StringComparison.Ordinal);
        Assert.Equal(2, budget.Dropped);

        // Cumulative across windows, so the counter answers "has this frame been losing output"
        // rather than only "is it losing output this minute".
        clock.UtcNow += TimeSpan.FromMinutes(10);
        budget.Write("seven");

        Assert.Contains("(2 in total since this agent started)", log.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void A_child_that_floods_and_then_dies_does_not_take_the_size_of_its_flood_with_it()
    {
        var log = new RecordingLog();
        var budget = new ChildOutputBudget(log, new ManualClock(), "Immich Kiosk", 1, TimeSpan.FromMinutes(10));

        budget.Write("first");
        budget.Write("dropped");
        budget.Write("dropped too");

        // Without the flush the count waits on a line that never comes, which is exactly the
        // silence the budget exists to avoid.
        Assert.DoesNotContain("Suppressed", log.Transcript, StringComparison.Ordinal);

        budget.Flush();

        Assert.Contains("Suppressed 2 lines from Immich Kiosk", log.Transcript, StringComparison.Ordinal);

        // And a flush with nothing owed says nothing at all.
        var quiet = log.Lines.Count;
        budget.Flush();
        Assert.Equal(quiet, log.Lines.Count);
    }

    [Fact]
    public void The_shipped_budget_leaves_a_healthy_slideshow_room_and_clips_the_measured_flood()
    {
        // Guide 12 sizes the journal at 64 MB and calls that one to two weeks of this frame's logs.
        // A healthy slideshow changes photo twice a minute; the measured flood ran at about seven
        // lines a second, which is 4,200 in the same ten minutes.
        const int HealthyLinesPerWindow = 20;
        const int MeasuredFloodLinesPerWindow = 4_200;

        Assert.True(ChildOutputBudget.DefaultLinesPerWindow > HealthyLinesPerWindow);
        Assert.True(ChildOutputBudget.DefaultLinesPerWindow < MeasuredFloodLinesPerWindow / 10);
        Assert.Equal(TimeSpan.FromMinutes(10), ChildOutputBudget.DefaultWindow);
    }
}
