using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// <c>identity.hostname</c> — the name, and that the name leads back to this frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests replaced a set that asserted a trap which does not exist.</b> The previous file
/// modelled cloud-init re-applying its seed at every boot and proved that acting on the seed beat
/// acting on <c>hostnamectl</c>. It was green against its own fixture and wrong about the world:
/// measured on the mule 2026-08-15, <c>hostnamectl set-hostname</c> survives a real reboot
/// (<c>boot_id</c> moved, the name held), cloud-init logged nothing about hostnames, and the
/// shipped seed carries no hostname to re-apply — <c>/boot/firmware/user-data</c> has
/// <c>#hostname:</c> commented out and <c>/boot/firmware/meta-data</c> has no
/// <c>local-hostname</c> at all.
/// </para>
/// <para>
/// What was measured, and what is asserted here instead, is the fault the trap was hiding:
/// <c>hostnamectl</c> does not maintain <c>/etc/hosts</c>, so after the rename the frame resolved
/// <b>its own name</b> through the DNS search domain to <c>217.61.253.65</c> — a public internet
/// address. <see cref="NameServices"/> models exactly that coupling, so the dangerous
/// half-applied state is an outcome the suite can produce rather than a claim in a comment.
/// </para>
/// </remarks>
public sealed class AgentHostnameTests
{
    private const string Desired = "framelink-mule";
    private const string Seeded = "raspberrypi";

    /// <summary>What the search domain answered for the frame's own name, verbatim.</summary>
    private const string PublicAddress = "217.61.253.65";

    private static ReconcileOptions Fast => new() { Countdown = TimeSpan.Zero, AttemptBudget = 3 };

    [Fact]
    public async Task The_name_being_right_is_not_enough_when_it_resolves_off_the_frame()
    {
        // The dangerous half-applied state, and the whole reason Observe asks two questions. The
        // frame is called what it should be called; asking where that name lives answers with a
        // machine on the internet.
        using var files = new TemporaryFiles();
        SeedStockImage(files);

        var names = new NameServices(files) { LiveHostname = Desired };
        var observation = await Resource(files, names).ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.DoesNotContain("live=", observation.Observed, StringComparison.Ordinal);
        Assert.Contains($"{Desired} resolves to {PublicAddress}", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frame_is_in_sync_only_when_the_name_is_right_and_it_answers_loopback()
    {
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        files.Seed(HostnameResource.HostsPath, $"127.0.0.1\tlocalhost\n127.0.1.1\t{Desired}\n");

        var names = new NameServices(files) { LiveHostname = Desired };
        var observation = await Resource(files, names).ObserveAsync(TestContext.Current.CancellationToken);

        Assert.True(observation.InSync);
        Assert.Equal(Desired, observation.Expected);
    }

    [Fact]
    public async Task A_wrong_name_and_a_wrong_resolution_are_both_named_in_the_delta()
    {
        using var files = new TemporaryFiles();
        SeedStockImage(files);

        var names = new NameServices(files);
        var observation = await Resource(files, names).ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains("live=" + Seeded, observation.Observed, StringComparison.Ordinal);
        Assert.Contains($"{Desired} resolves to {PublicAddress}", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_that_resolves_nowhere_at_all_is_drift_too()
    {
        // No mapping and no search domain behind it: getent exits non-zero. That is a different
        // symptom from the measured one and the same fault, so it must not read as success.
        using var files = new TemporaryFiles();
        SeedStockImage(files);

        var names = new NameServices(files) { LiveHostname = Desired, SearchDomainAddress = null };
        var observation = await Resource(files, names).ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Contains($"{Desired} resolves to nothing", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_act_writes_etc_hosts_and_renames_and_touches_nothing_else()
    {
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        var names = new NameServices(files);

        var action = await Resource(files, names).ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains($"127.0.1.1\t{Desired}", files.Read(HostnameResource.HostsPath)!, StringComparison.Ordinal);
        Assert.Contains($"hostnamectl set-hostname {Desired}", names.Commands);
        Assert.Contains(HostnameResource.HostsPath, action.Change, StringComparison.Ordinal);
        Assert.DoesNotContain("/boot/firmware", action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_under_boot_firmware_is_written_because_there_is_nothing_there_to_fix()
    {
        // The shipped seed supplies no hostname, so there was never anything to re-apply — and
        // writing it was the only reason this resource touched the boot partition at all. The
        // catalog now calls it not brick-adjacent, and this is what makes that true.
        using var files = new TemporaryFiles();
        SeedStockImage(files);

        var before = (
            Meta: files.Read(StockMetaDataPath),
            User: files.Read(StockUserDataPath));

        var names = new NameServices(files);
        using var harness = new ReconcileHarness(
            Fast,
            new AdoptionResource(files.Store, () => ServerAnswer.Adopted),
            Resource(files, names));

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(before.Meta, files.Read(StockMetaDataPath));
        Assert.Equal(before.User, files.Read(StockUserDataPath));
    }

    [Fact]
    public async Task One_apply_is_enough_because_nothing_puts_the_name_back()
    {
        // The disproof, as an outcome. If cloud-init re-applied a seeded hostname at every boot
        // this resource would never converge; measured on the mule it converges on the first
        // apply and holds across the reboot that proves it.
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        var names = new NameServices(files);

        using var harness = new ReconcileHarness(
            Fast,
            new AdoptionResource(files.Store, () => ServerAnswer.Adopted),
            Resource(files, names));

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(
            ResourceStatusKind.InSync,
            ReconcileHarness.StatusOf(outcome, HostnameResource.ResourceName).Kind);
        Assert.Equal(Desired, names.LiveHostname);
        Assert.Equal(1, names.Commands.Count(command => command.StartsWith("hostnamectl set-hostname", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task After_the_repair_the_frame_resolves_its_own_name_to_itself()
    {
        // The acceptance criterion in the terms the fault was found in: `getent hosts` answers a
        // loopback address rather than a machine in a data centre.
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        var names = new NameServices(files);

        using var harness = new ReconcileHarness(
            Fast,
            new AdoptionResource(files.Store, () => ServerAnswer.Adopted),
            Resource(files, names));

        await harness.ConvergeAsync();
        var resolved = await names.RunAsync("getent", ["hosts", Desired], TestContext.Current.CancellationToken);

        Assert.StartsWith("127.0.1.1", resolved.StandardOutput, StringComparison.Ordinal);
        Assert.True(HostnameResource.IsLoopback(resolved.StandardOutput.Split('\t', ' ')[0]));
    }

    [Fact]
    public async Task Renaming_without_writing_etc_hosts_is_what_pointed_the_frame_at_the_internet()
    {
        // The measured sequence, reproduced: rename only, and the frame's own name now answers
        // with a public address. Kept as a test rather than as a comment because it is the thing
        // the shipping Observe has to catch, and a fixture that cannot produce the fault cannot
        // prove the check works.
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        var names = new NameServices(files);

        await names.RunAsync("hostnamectl", ["set-hostname", Desired], TestContext.Current.CancellationToken);
        var resolved = await names.RunAsync("getent", ["hosts", Desired], TestContext.Current.CancellationToken);

        Assert.StartsWith(PublicAddress, resolved.StandardOutput, StringComparison.Ordinal);
        Assert.False(await InSyncAsync(files, names));
    }

    [Fact]
    public async Task A_fleet_with_no_hostname_setting_keeps_the_frames_own_name_and_still_fixes_the_mapping()
    {
        // The /etc/hosts half is worth enforcing whatever the frame is called, and on this fleet
        // nobody has set a name. The frame is not renamed; it stops resolving itself off-box.
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        var names = new NameServices(files);

        var resource = new HostnameResource(files.Files, names, FleetValues.None);
        await resource.ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains($"127.0.1.1\t{Seeded}", files.Read(HostnameResource.HostsPath)!, StringComparison.Ordinal);
        Assert.True((await resource.ObserveAsync(TestContext.Current.CancellationToken)).InSync);
        Assert.Equal(Seeded, names.LiveHostname);
    }

    [Fact]
    public async Task A_frame_with_no_name_at_all_is_left_alone()
    {
        // Nothing set by the fleet and nothing set locally. Inventing a name would be worse than
        // leaving it, and a permanently drifted field means a permanently not-green frame (§2.6).
        using var files = new TemporaryFiles();
        var names = new NameServices(files) { LiveHostname = string.Empty };

        var observation = await new HostnameResource(files.Files, names, FleetValues.None)
            .ObserveAsync(TestContext.Current.CancellationToken);

        Assert.True(observation.InSync);
        Assert.Contains("no name set", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hosts_editor_replaces_the_loopback_line_and_leaves_the_rest()
    {
        const string Original = "127.0.0.1\tlocalhost\n127.0.1.1\traspberrypi\n::1\tip6-localhost\n";
        var updated = HostnameResource.WriteHostsEntry(Original, "framelink-douwe");

        Assert.Contains("127.0.0.1\tlocalhost", updated, StringComparison.Ordinal);
        Assert.Contains("127.0.1.1\tframelink-douwe", updated, StringComparison.Ordinal);
        Assert.Contains("::1\tip6-localhost", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("raspberrypi", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hosts_file_with_no_loopback_mapping_gains_one()
    {
        // The measured state: 127.0.1.1 named the old host and the new name had no entry at all.
        var updated = HostnameResource.WriteHostsEntry("127.0.0.1\tlocalhost\n", "framelink-douwe");

        Assert.Contains("127.0.1.1\tframelink-douwe", updated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("0:0:0:0:0:0:0:1", true)]
    [InlineData("217.61.253.65", false)]
    [InlineData("10.20.30.250", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_address_that_comes_back_here_counts_as_loopback(string? address, bool loopback) =>
        Assert.Equal(loopback, HostnameResource.IsLoopback(address));

    private static async Task<bool> InSyncAsync(TemporaryFiles files, IProcessRunner processes) =>
        (await Resource(files, processes).ObserveAsync(TestContext.Current.CancellationToken)).InSync;

    private static HostnameResource Resource(TemporaryFiles files, IProcessRunner processes) =>
        new(
            files.Files,
            processes,
            FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HostnameResource.SettingKey] = Desired,
            }));

    /// <summary>The NoCloud seed as it actually ships, plus the stock <c>/etc/hosts</c>.</summary>
    /// <remarks>
    /// Read verbatim off the mule 2026-08-15. The hostname in <c>user-data</c> is a <b>comment</b>
    /// and <c>meta-data</c> has no <c>local-hostname</c> key — which is why there was never
    /// anything for cloud-init to put back, and why nothing here writes to either file.
    /// </remarks>
    private static void SeedStockImage(TemporaryFiles files)
    {
        files.Seed(StockMetaDataPath, "instance_id: rpios-image\ndsmode: local\n");
        files.Seed(StockUserDataPath, "#cloud-config\n#hostname: raspberrypi\n");
        files.Seed(HostnameResource.HostsPath, $"127.0.0.1\tlocalhost\n127.0.1.1\t{Seeded}\n");
    }

    private const string StockMetaDataPath = "/boot/firmware/meta-data";
    private const string StockUserDataPath = "/boot/firmware/user-data";

    /// <summary>
    /// This frame's name services: <c>hostnamectl</c>, <c>/etc/hosts</c>, and a DNS search domain
    /// behind them.
    /// </summary>
    /// <remarks>
    /// The coupling is the point. <c>getent</c> answers from <c>/etc/hosts</c> when the name is
    /// there and falls through to the search domain when it is not — which is exactly how a frame
    /// that had been renamed but not remapped came to answer <c>217.61.253.65</c> for its own
    /// name. A double that answered from a field the test sets could not produce that fault at
    /// all, and a check against it would prove nothing.
    /// </remarks>
    private sealed class NameServices(TemporaryFiles files) : IProcessRunner
    {
        public string LiveHostname { get; set; } = Seeded;

        /// <summary>What the search domain answers for a name <c>/etc/hosts</c> does not carry.</summary>
        public string? SearchDomainAddress { get; set; } = PublicAddress;

        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Commands.Add(executable + " " + string.Join(' ', arguments));

            switch (arguments)
            {
                case ["--static"]:
                    return Answer(LiveHostname);

                case ["set-hostname", var name]:
                    LiveHostname = name;
                    return Answer(string.Empty);

                case ["hosts", var name] when Mapped(name) is { } address:
                    return Answer($"{address}\t{name}");

                case ["hosts", var name] when SearchDomainAddress is { } address:
                    return Answer($"{address}\t{name}.huisman.io");

                case ["hosts", _]:
                    // getent exits 2 when the key is not found in any source.
                    return Task.FromResult(new ProcessResult(2, string.Empty, string.Empty));

                default:
                    return Answer(string.Empty);
            }
        }

        private static Task<ProcessResult> Answer(string output) =>
            Task.FromResult(new ProcessResult(0, output, string.Empty));

        private string? Mapped(string name)
        {
            foreach (var raw in (files.Read(HostnameResource.HostsPath) ?? string.Empty).Split('\n'))
            {
                var fields = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length >= 2
                    && fields[0].Length > 0
                    && fields[0][0] != '#'
                    && fields.Skip(1).Contains(name, StringComparer.Ordinal))
                {
                    return fields[0];
                }
            }

            return null;
        }
    }
}
