using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// The call button — guide 11's daemon, now inside the agent, and the two device resources the
/// catalog keeps from that guide.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of this can be tested on the hardware it is for.</b> The button is not sourced, so what
/// is asserted here is everything up to the wire: the claim's shape, the press-to-toggle path, the
/// retry after a lost claim, and — the one that matters most — that a claim which cannot be made is
/// reported as a failure rather than as a healthy watch. That last is v1's scar: without
/// <c>python3-lgpio</c>, gpiozero silently substituted a mock pin factory, the daemon reported
/// healthy for ever, and the button did nothing.
/// </para>
/// <para>
/// The wire itself stays a human-confirmed checkpoint, exactly as guide 11 step 5 leaves it.
/// </para>
/// </remarks>
public sealed class AgentButtonTests
{
    private static readonly CancellationToken None = TestContext.Current.CancellationToken;

    /// <summary><c>gpioinfo</c> on a Pi, with the agent holding line 17.</summary>
    private const string GpioInfoHeld =
        "gpiochip0 - 54 lines:\n"
        + "\tline   0:\t\"ID_SDA\"\tinput\n"
        + "\tline  16:\t\"GPIO16\"\tinput\n"
        + "\tline  17:\t\"GPIO17\"\tinput\tconsumer=\"fl-agent-button\"\tbias=pull-up\tedges=falling\tdebounce-period=50ms\n"
        + "\tline  18:\t\"GPIO18\"\tinput\n";

    /// <summary>The same frame with nothing holding the line — the wrong-pin signature.</summary>
    private const string GpioInfoUnused =
        "gpiochip0 - 54 lines:\n"
        + "\tline  17:\t\"GPIO17\"\tinput\n";

    /// <summary>And with somebody else holding it.</summary>
    private const string GpioInfoContended =
        "gpiochip0 - 54 lines:\n"
        + "\tline  17:\t\"GPIO17\"\tinput\tconsumer=\"framelink-gpio\"\tbias=pull-up\n";

    /// <summary>The v1 frame's own <c>id</c> line, from the inventory's USERS_GROUPS block.</summary>
    private const string IdOutput =
        "uid=1000(framelink) gid=1000(framelink) groups=1000(framelink),4(adm),20(dialout),24(cdrom),"
        + "27(sudo),29(audio),44(video),46(plugdev),60(games),100(users),102(netdev),985(docker),"
        + "986(gpio),988(i2c),989(spi),992(render),996(input)";

    [Fact]
    public void The_line_request_carries_the_pull_up_the_falling_edge_and_the_fifty_millisecond_debounce()
    {
        var vectors = GpioMonLines.Vectors(
            new GpioLineRequest(ButtonWatch.DefaultChip, ButtonWatch.DefaultPin, ButtonWatch.ConsumerName, ButtonWatch.Debounce));

        Assert.Equal(2, vectors.Count);

        foreach (var vector in vectors)
        {
            // A button to ground with the internal pull-up reads high until it is pressed, so the
            // press is the *falling* edge. Watching both would toggle twice per press — once on
            // the way down and once when the finger comes off.
            Assert.Contains("--bias=pull-up", vector);
            Assert.Contains("--edges=falling", vector);
            Assert.Contains("--debounce-period=50ms", vector);

            // The consumer name is the whole of how gpio.button.line tells "the agent is holding
            // this" from "somebody else is".
            Assert.Contains(ButtonWatch.ConsumerName, vector);
        }

        // Chip and offset first, then the bare line name — the Pi 5's header has been gpiochip0 and
        // gpiochip4 on different kernels, and the name form does not care which.
        Assert.Contains("gpiochip0", vectors[0]);
        Assert.Contains("17", vectors[0]);
        Assert.Contains("GPIO17", vectors[1]);
    }

    [Fact]
    public async Task A_press_publishes_the_toggle_the_v1_daemon_used_to_send_over_its_own_port()
    {
        var channel = new LocalChannel();
        var frames = new List<StageMessage>();
        using var attached = channel.Attach((message, _) =>
        {
            frames.Add(message);
            return Task.CompletedTask;
        });

        var lines = new ScriptedGpioLines();
        var button = Watch(channel, lines, productRuns: true);

        using var running = CancellationTokenSource.CreateLinkedTokenSource(None);
        var loop = button.RunAsync(running.Token);

        await lines.PressAsync();
        await running.CancelAsync();
        await loop;

        var toggle = Assert.Single(frames);

        Assert.Equal(ButtonWatch.ToggleCommand, toggle.Command);
        Assert.Equal(1, button.Presses);

        // It rides a complete, current stage frame rather than a bare command, so a page that does
        // not understand commands still renders the truth rather than a default condition.
        Assert.True(toggle.ProductRuns);
        Assert.Equal("InSync", toggle.Condition);
    }

    [Fact]
    public async Task A_simulated_press_takes_exactly_the_same_path_as_the_wire()
    {
        var channel = new LocalChannel();
        var frames = new List<StageMessage>();
        using var attached = channel.Attach((message, _) =>
        {
            frames.Add(message);
            return Task.CompletedTask;
        });

        var button = Watch(channel, new ScriptedGpioLines(), productRuns: true);

        // Guide 11 step 4's SIGUSR1, without a signal: there is no separate process to signal any
        // more, and the point of it was always that the handler ran the *same* broadcast a press
        // does — so the simulation exercises everything except the wire.
        await button.SimulateAsync(None);

        Assert.Equal(ButtonWatch.ToggleCommand, Assert.Single(frames).Command);
        Assert.Equal(1, button.Presses);
    }

    [Fact]
    public async Task A_press_while_the_product_is_not_running_is_counted_and_not_passed_on()
    {
        var channel = new LocalChannel();
        var frames = new List<StageMessage>();
        using var attached = channel.Attach((message, _) =>
        {
            frames.Add(message);
            return Task.CompletedTask;
        });

        var button = Watch(channel, new ScriptedGpioLines(), productRuns: false);

        await button.SimulateAsync(None);

        // §2.6 gives the agent the screen whenever the product is not running, and toggling into a
        // call that cannot start is not a repair. The press still counts, so "somebody keeps
        // pressing the button and nothing happens" stays visible.
        Assert.Empty(frames);
        Assert.Equal(1, button.Presses);
        Assert.Contains("the product is not running", button.LastStandDown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_claim_that_cannot_be_made_is_recorded_as_a_failure_and_retried()
    {
        var lines = new ScriptedGpioLines
        {
            Refusal = "gpiomon could not be started (No such file or directory). The gpiod package provides it.",
        };

        var log = new RecordingLog();

        // Held delays, so the retry schedule is stepped rather than raced: the watch stops at each
        // backoff until this test lets it through, which is also what keeps a refusal that returns
        // instantly from becoming a hot loop inside the suite.
        var clock = new ManualClock { Hold = true };
        var button = Watch(new LocalChannel(), lines, productRuns: true, log: log, clock: clock);

        using var running = CancellationTokenSource.CreateLinkedTokenSource(None);
        var loop = button.RunAsync(running.Token);

        for (var retry = 0; retry < 2; retry++)
        {
            await WaitUntil(() => clock.HeldCount > 0);
            Assert.True(clock.ReleaseOne());
        }

        await WaitUntil(() => lines.Attempts >= 3);

        await running.CancelAsync();
        clock.ReleaseOne();
        await loop;

        // Backed off rather than spun: one wait per failed claim, at the interval the watch names.
        Assert.All(clock.Delays, delay => Assert.Equal(ButtonWatch.RetryDelay, delay));

        // Retried rather than given up on, and never once reported as holding.
        Assert.True(lines.Attempts >= 3);

        var state = button.State();
        Assert.False(state.Holding);
        Assert.Contains("gpiod package", state.Failure, StringComparison.Ordinal);
        Assert.Contains("gpiomon could not be started", log.Transcript, StringComparison.Ordinal);

        // The v1 failure this exists to refuse: a backend that is not there reported as a healthy
        // watch. `Describe` is what reaches the delta, so it is what must not lie.
        Assert.Contains("not holding the line", state.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_line_is_in_sync_on_a_frame_whose_button_has_never_been_pressed()
    {
        var processes = new RecordingProcessRunner();
        processes.Answers["gpioinfo "] = new ProcessResult(0, GpioInfoHeld, string.Empty);

        var button = Watch(new LocalChannel(), new ScriptedGpioLines(), productRuns: true);
        var resource = new GpioButtonLineResource(processes, FleetValues.None, button);

        using var running = CancellationTokenSource.CreateLinkedTokenSource(None);
        var loop = button.RunAsync(running.Token);

        var observation = await resource.ObserveAsync(None);

        await running.CancelAsync();
        await loop;

        // The button hardware is not sourced, so this is every frame in existence right now. A
        // frame with no button wired is not drifted: the claim is on the line, the pull-up holds it
        // high, and no edge ever arrives. Reporting drift would walk §2.5's ladder — retry,
        // escalate, and stop the whole frame — over something no software on it can change.
        Assert.True(observation.InSync);
        Assert.Contains("has not seen a press yet", observation.Observed, StringComparison.Ordinal);
        Assert.Contains("pull-up", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_contended_line_and_a_wrong_pin_are_different_deltas()
    {
        var processes = new RecordingProcessRunner();
        var resource = new GpioButtonLineResource(processes, FleetValues.None, button: null);

        processes.Answers["gpioinfo "] = new ProcessResult(0, GpioInfoUnused, string.Empty);
        var unused = await resource.ObserveAsync(None);

        Assert.False(unused.InSync);
        Assert.Contains("line 17 on gpiochip0", unused.Observed, StringComparison.Ordinal);

        processes.Answers["gpioinfo "] = new ProcessResult(0, GpioInfoContended, string.Empty);
        var contended = await resource.ObserveAsync(None);

        Assert.False(contended.InSync);
        Assert.Contains("framelink-gpio", contended.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_with_no_gpio_stands_down_and_a_missing_tool_does_not()
    {
        var processes = new RecordingProcessRunner();
        var resource = new GpioButtonLineResource(processes, FleetValues.None, button: null);

        // No chips at all — a container, or the virtual agent of §5.3. The same shape as the
        // autologin drop-in standing down where there is no tty1.
        processes.Answers["gpioinfo "] = new ProcessResult(0, string.Empty, string.Empty);
        var noHardware = await resource.ObserveAsync(None);

        Assert.True(noHardware.InSync);
        Assert.Contains("no GPIO chip at all", noHardware.Observed, StringComparison.Ordinal);

        // A missing tool is a different thing entirely, and must not be quietly tolerated: the
        // catalog calls gpiod stock, so this is either a cut-down frame or a gap in the catalog,
        // and both need a person. The delta names the package so nobody has to guess.
        processes.Answers["gpioinfo "] = new ProcessResult(-1, string.Empty, "No such file or directory");
        var noTool = await resource.ObserveAsync(None);

        Assert.False(noTool.InSync);
        Assert.Contains("gpioinfo could not be run", noTool.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_watched_line_follows_the_fleet_setting()
    {
        var processes = new RecordingProcessRunner();
        processes.Answers["gpioinfo "] = new ProcessResult(0, GpioInfoHeld, string.Empty);

        var values = FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ButtonWatch.SettingKey] = "22",
        });

        var resource = new GpioButtonLineResource(processes, values, button: null);
        var observation = await resource.ObserveAsync(None);

        // The agent is holding 17 and the fleet says 22. That is a real fault with its own
        // sentence: everything looks healthy from the agent's side while the button nobody is
        // watching is on another pin.
        Assert.Equal(22, resource.Pin);
        Assert.False(observation.InSync);
        Assert.Contains("holding line 17", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_act_drops_the_claim_so_it_is_made_again()
    {
        var lines = new ScriptedGpioLines();
        var clock = new ManualClock { Hold = true };
        var button = Watch(new LocalChannel(), lines, productRuns: true, clock: clock);
        var resource = new GpioButtonLineResource(new RecordingProcessRunner(), FleetValues.None, button);

        using var running = CancellationTokenSource.CreateLinkedTokenSource(None);
        var loop = button.RunAsync(running.Token);

        await lines.HeldAsync();
        var action = await resource.ActAsync(None);

        await WaitUntil(() => lines.Attempts >= 2);

        await running.CancelAsync();
        clock.ReleaseOne();
        await loop;

        Assert.Contains("claim on line 17", action.Change, StringComparison.Ordinal);
        Assert.Contains("50 ms debounce", action.Change, StringComparison.Ordinal);
        Assert.True(lines.Attempts >= 2);

        // An Act does not wait out the retry backoff. It is a repair with a verifying reboot
        // behind it (§2.4), and half a minute of backoff would put that reboot in the middle of
        // the claim it is supposed to be proving.
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public void The_group_set_is_the_v1_parity_set_without_docker()
    {
        var v1 = UserGroupsResource.Membership(IdOutput);

        // Everything the frozen frame had except its own primary group and docker.
        var expected = v1
            .Where(group => !string.Equals(group, "framelink", StringComparison.Ordinal))
            .Where(group => !string.Equals(group, UserGroupsResource.RetiredGroup, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, UserGroupsResource.Groups.Order(StringComparer.Ordinal));

        // Docker leaves the frame with §2.1, so demanding this membership for ever is exactly the
        // naive state-diff the catalog warns about.
        Assert.DoesNotContain(UserGroupsResource.RetiredGroup, UserGroupsResource.Groups);
    }

    [Fact]
    public async Task Group_membership_is_a_superset_check_and_docker_is_reported_rather_than_removed()
    {
        var processes = new RecordingProcessRunner();
        var session = new FakeUserSession();
        var resource = new UserGroupsResource(processes, session);

        processes.Answers["id framelink"] = new ProcessResult(0, IdOutput, string.Empty);

        var observation = await resource.ObserveAsync(None);

        Assert.True(observation.InSync);
        Assert.Contains("docker", observation.Observed, StringComparison.Ordinal);

        var action = await resource.ActAsync(None);
        Assert.Contains("already holds every group", action.Change, StringComparison.Ordinal);
        Assert.DoesNotContain(processes.Commands, command => command.StartsWith("usermod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_groups_are_appended_and_only_the_missing_ones()
    {
        var processes = new RecordingProcessRunner();
        var resource = new UserGroupsResource(processes, new FakeUserSession());

        processes.Answers["id framelink"] = new ProcessResult(
            0,
            "uid=1000(framelink) gid=1000(framelink) groups=1000(framelink),4(adm),20(dialout),24(cdrom),27(sudo),"
            + "29(audio),44(video),46(plugdev),60(games),100(users),102(netdev),992(render),996(input)",
            string.Empty);

        var observation = await resource.ObserveAsync(None);

        Assert.False(observation.InSync);
        Assert.Contains("spi, i2c, gpio", observation.Observed, StringComparison.Ordinal);

        await resource.ActAsync(None);

        // `-a` is the whole difference between appending and replacing: `usermod -G` without it
        // drops every group not listed, which on this account includes sudo.
        Assert.Contains("usermod -a -G spi,i2c,gpio framelink", processes.Commands);
    }

    [Fact]
    public void The_button_block_depends_on_what_the_catalog_document_says_it_does()
    {
        using var files = new TemporaryFiles();
        var graph = DeviceCatalog.BuildGraph(AgentResourceGraphTests.Context(files));
        var order = graph.Ordered.Select(resource => resource.Name).ToList();

        // `agent.version` is the catalog's "—" and is not a resource, so the only dependency left
        // is the membership that reaches the hardware.
        Assert.Equal(
            [UserGroupsResource.ResourceName],
            graph.Find(GpioButtonLineResource.ResourceName)!.DependsOn);

        Assert.Empty(graph.Find(UserGroupsResource.ResourceName)!.DependsOn);

        // Group membership only takes effect in a new login session, so it has to be settled before
        // the drop-in that creates one.
        Assert.True(order.IndexOf(UserGroupsResource.ResourceName) < order.IndexOf(ConsoleAutologinResource.ResourceName));
    }

    [Fact]
    public void The_app_inside_the_binary_hears_the_button_on_the_one_local_origin()
    {
        var stage = Asset("frame-stage.js");
        var app = Asset("frame-app.js");

        // The page half of the same path. `frame-stage.js` is the file that must keep working when
        // the rest of the app does not, so the command lands there and is re-broadcast rather than
        // being routed through anything the app owns.
        Assert.Contains(ButtonWatch.ToggleCommand, app, StringComparison.Ordinal);
        Assert.Contains("framelink-command", stage, StringComparison.Ordinal);
        Assert.Contains("framelink-command", app, StringComparison.Ordinal);

        // The port is gone, and with it the client that used to connect to it — "an internal detail
        // of the v1 split between daemon and SPA; with both inside one binary there is no port".
        // A frame still carrying that client would retry a dead socket every five seconds for ever.
        Assert.DoesNotContain("control.js", EmbeddedApp.Paths);
        Assert.DoesNotContain("ws://127.0.0.1:8889", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ws://127.0.0.1:8889", stage, StringComparison.Ordinal);
    }

    internal static string Asset(string path) =>
        System.Text.Encoding.UTF8.GetString(
            EmbeddedApp.Find(path) ?? throw new FileNotFoundException($"{path} is not embedded in the agent."));

    private static ButtonWatch Watch(
        LocalChannel channel,
        IGpioLines lines,
        bool productRuns,
        RecordingLog? log = null,
        FleetValues? values = null,
        ManualClock? clock = null) =>
        new(new ButtonWatchServices
        {
            Channel = channel,
            Lines = lines,
            Stage = () => new StageMessage { Condition = "InSync", ProductRuns = productRuns },

            // A manual clock, so the retry schedule costs no wall-clock time. Tests that exercise
            // the schedule itself pass one with `Hold` set and step it.
            Clock = clock ?? new ManualClock(),
            Log = log ?? new RecordingLog(),
            Values = values ?? FleetValues.None,
        });

    /// <summary>Waits for a background loop to reach <paramref name="condition"/>.</summary>
    /// <remarks>
    /// The watch runs as one of the agent's loops, so its progress is observed rather than stepped.
    /// The bound is generous and the poll is short: a passing run leaves here in milliseconds, and
    /// a broken one fails with the assertion below rather than hanging the suite.
    /// </remarks>
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10, None);
        }

        Assert.True(condition(), "the button watch never reached the state this test is about");
    }
}

/// <summary>A GPIO line that does exactly what a test tells it to.</summary>
internal sealed class ScriptedGpioLines : IGpioLines
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _press;

    /// <summary>When set, every claim is refused with this reason instead of being held.</summary>
    public string? Refusal { get; init; }

    /// <summary>How many times a claim has been attempted.</summary>
    public int Attempts { get; private set; }

    /// <summary>The request the last attempt carried.</summary>
    public GpioLineRequest LastRequest { get; private set; }

    /// <inheritdoc/>
    public async Task<string> WatchAsync(GpioLineRequest request, Action onPress, CancellationToken cancellationToken)
    {
        Attempts++;
        LastRequest = request;
        _press = onPress;

        if (Refusal is { } refused)
        {
            return refused;
        }

        _held.TrySetResult();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "the agent is shutting down";
        }

        return "the claim ended";
    }

    /// <summary>Waits until the claim is held.</summary>
    public Task HeldAsync() => _held.Task;

    /// <summary>Fires one debounced press, as the wire would.</summary>
    public async Task PressAsync()
    {
        await HeldAsync().ConfigureAwait(false);
        _press?.Invoke();
        await Task.Yield();
    }
}
