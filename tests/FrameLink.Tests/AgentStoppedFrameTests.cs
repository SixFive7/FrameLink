using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Stage;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// <b>What a frame that has given up looks like, and what it offers</b> — version2.md §2.7 items
/// 7, 8 and 9, decisions 70, 71 and 72.
/// </summary>
/// <remarks>
/// <para>
/// The operator watched a frame reboot 41 times and concluded, reasonably, that it was looping
/// for ever. It was not: it had stopped, and the screen was still painting an attempt counter
/// beside a moving bar for a resource nothing was touching. Every test here asserts something a
/// person standing in front of the frame would see — what it says, whether it moves, who it names,
/// and whether the button in front of them does anything.
/// </para>
/// <para>
/// <b>Nothing here has run on hardware.</b> These are assertions about composed text and about a
/// message arriving on a channel; the panel, its touch digitiser and the browser on it are not in
/// this process.
/// </para>
/// </remarks>
public sealed class AgentStoppedFrameTests
{
    private static AgentStatus Stopped => new()
    {
        Condition = DeviceStateLadder.Starting,
        DeviceId = "TEST-DEVI-CEID-0001",
        Resources =
        [
            new ResourceStatus
            {
                Name = "audio.mixer.pcm-volume",
                Kind = ResourceStatusKind.Escalated,
                Delta = "expected '20', observed '0'",
                Attempts = 3,
                AttemptBudget = 3,
                Escalations = 1,
            },
        ],
        Reconcile = new ReconcileNarration
        {
            Resource = "audio.mixer.pcm-volume",
            Attempt = 3,
            AttemptBudget = 3,
            Escalations = 1,
            AdminNotified = true,
        },
    };

    [Fact]
    public void The_stopped_sentence_is_the_operators_wording_and_carries_the_recorded_delta()
    {
        // §2.5 rung 2 already requires the exact expected-versus-observed delta and the attempt
        // count to be recorded, so the screen renders that text rather than composing a second
        // spelling of the same failure (decision 70).
        Assert.Equal(
            "audio.mixer.pcm-volume failed after 3 tries, expected '20', observed '0'",
            ReconcileVoice.StoppedLine(Stopped));
    }

    [Fact]
    public void A_frame_that_is_still_trying_says_the_item_and_the_attempt_and_nothing_about_giving_up()
    {
        var working = Stopped with
        {
            Resources = [],
            Reconcile = new ReconcileNarration
            {
                Resource = "audio.mixer.pcm-volume",
                Attempt = 1,
                AttemptBudget = 3,
            },
        };

        Assert.Equal("audio.mixer.pcm-volume attempt 1 of 3", ReconcileVoice.ProgressLine(working));
        Assert.Null(ReconcileVoice.StoppedLine(working));
        Assert.False(ReconcileVoice.HasStopped(working));
    }

    [Fact]
    public void A_stopped_frame_has_no_progress_line_at_all()
    {
        // The two are mutually exclusive by construction rather than by the caller remembering to
        // check: "attempt 3 of 3" beside "failed after 3 tries" is the contradiction that made a
        // stopped frame read as a working one.
        Assert.Null(ReconcileVoice.ProgressLine(Stopped));
        Assert.True(ReconcileVoice.HasStopped(Stopped));
    }

    [Fact]
    public void A_retry_puts_the_frame_back_to_working_rather_than_leaving_it_stopped()
    {
        // The escalation count survives a retry on purpose (§2.5 rung 3) — it is the attempts that
        // are reset. A screen keyed on the escalation count alone would therefore call a frame
        // stopped for ever after its first escalation, including one somebody has just restarted.
        var retried = Stopped with
        {
            Resources = [],
            Reconcile = new ReconcileNarration
            {
                Resource = "audio.mixer.pcm-volume",
                Attempt = 1,
                AttemptBudget = 3,
                Escalations = 1,
            },
        };

        Assert.False(ReconcileVoice.HasStopped(retried));
        Assert.Equal("audio.mixer.pcm-volume attempt 1 of 3", ReconcileVoice.ProgressLine(retried));
    }

    [Theory]
    [InlineData("Jori", "06 12 34 56 78", "Nothing more will happen until someone helps. Ask Jori — 06 12 34 56 78.")]
    [InlineData("Jori", "", "Nothing more will happen until someone helps. Ask Jori.")]
    [InlineData("", "06 12 34 56 78", "Nothing more will happen until someone helps. Contact 06 12 34 56 78.")]
    [InlineData("", "", ReconcileVoice.UnknownContact)]
    public void Every_shape_of_contact_produces_a_sentence_naming_a_next_step(
        string name,
        string detail,
        string expected)
    {
        // §2.7 item 8. Never silence: a frame that has stopped and cannot name anybody still has
        // to say that somebody is needed, or a household waits for it to fix itself.
        var contact = new OperatorContact
        {
            Name = name,
            Contact = detail,
            UpdatedUtc = DateTimeOffset.UnixEpoch,
        };

        Assert.Equal(expected, ReconcileVoice.ContactLine(contact));
    }

    [Fact]
    public void A_frame_that_has_never_been_told_says_so_rather_than_inventing_an_address()
    {
        Assert.Equal(ReconcileVoice.UnknownContact, ReconcileVoice.ContactLine(null));
    }

    [Fact]
    public void The_browser_stage_sends_the_page_the_same_verdict_the_console_paints()
    {
        // Two surfaces, one composition (decision 70). The page is told the frame has stopped by
        // the presence of the stopped line and by CanRetry, so a page cannot render a stopped
        // frame as a working one without ignoring both.
        var message = BrowserStage.Compose(
            Stopped with { Contact = new OperatorContact { Name = "Jori", Contact = "06", UpdatedUtc = DateTimeOffset.UnixEpoch } },
            DateTimeOffset.UnixEpoch);

        Assert.Equal(ReconcileVoice.StoppedLine(Stopped), message.StoppedLine);
        Assert.Null(message.ProgressLine);
        Assert.True(message.CanRetry);
        Assert.Equal("Nothing more will happen until someone helps. Ask Jori — 06.", message.ContactLine);
        Assert.Equal("Your Fleet Manager has been told and is waiting for you.", message.EscalationLine);
    }

    [Fact]
    public void A_working_frame_is_offered_no_retry_button()
    {
        // §2.7 item 9: the button appears only when there is a budget to reset. Offering it while
        // the frame is mid-attempt would reset nothing and teach the person that it does nothing,
        // which is the same harm as a button that is not wired up.
        var message = BrowserStage.Compose(
            Stopped with
            {
                Resources = [],
                Reconcile = new ReconcileNarration { Resource = "audio.mixer.pcm-volume", Attempt = 1, AttemptBudget = 3 },
            },
            DateTimeOffset.UnixEpoch);

        Assert.False(message.CanRetry);
        Assert.Null(message.StoppedLine);
        Assert.Null(message.ContactLine);
        Assert.Equal("audio.mixer.pcm-volume attempt 1 of 3", message.ProgressLine);
    }

    [Fact]
    public void A_press_at_the_frame_asks_for_a_retry_over_the_channel_that_is_already_open()
    {
        // §2.5 rung 5. The press arrives as an ordinary page message on the local channel — the
        // same one "Reboot now" uses — and carries no arguments, because the person pressing it
        // has not chosen a resource and should not have to.
        var channel = new LocalChannel();
        var retries = 0;
        var reboots = 0;
        channel.RetryRequested += () => retries++;
        channel.RebootRequested += () => reboots++;

        channel.Receive(new PageMessage { Kind = PageMessage.KindRetry }, DateTimeOffset.UnixEpoch);

        Assert.Equal(1, retries);
        Assert.Equal(0, reboots);
    }

    [Fact]
    public void An_ordinary_check_in_is_not_a_retry()
    {
        var channel = new LocalChannel();
        var retries = 0;
        channel.RetryRequested += () => retries++;

        channel.Receive(new PageMessage { Kind = PageMessage.KindHello }, DateTimeOffset.UnixEpoch);
        channel.Receive(new PageMessage { Kind = PageMessage.KindAlive }, DateTimeOffset.UnixEpoch);
        channel.Receive(new PageMessage { Kind = PageMessage.KindRebootNow }, DateTimeOffset.UnixEpoch);

        Assert.Equal(0, retries);
    }

    [Fact]
    public void The_console_stage_names_the_fleet_manager_when_this_frame_has_no_touchscreen()
    {
        // Decision 72's honest half, kept and now true of the frame that prints it. The sentence it
        // used to print — "This screen has no buttons" — was a claim about the hardware, made when
        // nothing in this repository had ever captured an input device from a frame. It has since
        // been measured and the panel does expose one, so the claim is now conditional on what the
        // agent found rather than on what was assumed (decision 77). This status carries the
        // default, which is a frame with no touchscreen: every workstation, and every frame whose
        // panel overlay has not been applied yet.
        var frame = StageRenderer.Render(Stopped, DateTimeOffset.UnixEpoch, tick: 0, 160, 30, colour: false);

        Assert.Contains("the Try again button is in the Fleet Manager", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("hold for", frame, StringComparison.Ordinal);
    }
}
