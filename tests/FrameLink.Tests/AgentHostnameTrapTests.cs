using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;

namespace FrameLink.Tests;

/// <summary>
/// <c>identity.hostname</c> — the confirmed "silently reverted" case, and the reason §2.4 exists.
/// </summary>
/// <remarks>
/// <para>
/// Observed on the mule 2026-08-15 and recorded in version2.md Appendix B item 1: the hostname
/// is cloud-init managed on this image, <c>hostnamectl set-hostname</c> appears to succeed, and
/// the value is silently reverted at the next boot. A write-only check would have marked it
/// <c>InSync</c> while it was quietly wrong.
/// </para>
/// <para>
/// These tests model that owner explicitly — <see cref="CloudInit"/> puts the seed's value back
/// during every boot, exactly as the datasource does — so the difference between acting on
/// <c>hostnamectl</c> and acting on the seed is a difference in outcome rather than a claim.
/// </para>
/// </remarks>
public sealed class AgentHostnameTrapTests
{
    private const string Desired = "framelink-douwe";
    private const string Seeded = "raspberrypi";

    private static ReconcileOptions Fast => new() { Countdown = TimeSpan.Zero, AttemptBudget = 3 };

    [Fact]
    public async Task Observe_reads_all_four_owners_and_names_the_ones_that_disagree()
    {
        using var files = new TemporaryFiles();
        SeedStockImage(files);

        var processes = new RecordingProcessRunner();
        processes.Answers["hostnamectl --static"] = new ProcessResult(0, Seeded, string.Empty);

        var observation = await Resource(files, processes)
            .ObserveAsync(TestContext.Current.CancellationToken);

        Assert.False(observation.InSync);
        Assert.Equal(Desired, observation.Expected);
        Assert.Contains("live=" + Seeded, observation.Observed, StringComparison.Ordinal);
        Assert.Contains(HostnameResource.MetaDataPath, observation.Observed, StringComparison.Ordinal);
        Assert.Contains(HostnameResource.HostsPath, observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_act_writes_cloud_inits_seed_and_not_only_hostnamectl()
    {
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        var processes = new RecordingProcessRunner();

        var action = await Resource(files, processes).ActAsync(TestContext.Current.CancellationToken);

        Assert.Contains("local-hostname: " + Desired, files.Read(HostnameResource.MetaDataPath)!, StringComparison.Ordinal);
        Assert.Contains("hostname: " + Desired, files.Read(HostnameResource.UserDataPath)!, StringComparison.Ordinal);
        Assert.Contains("preserve_hostname: false", files.Read(HostnameResource.UserDataPath)!, StringComparison.Ordinal);
        Assert.Contains("127.0.1.1\t" + Desired, files.Read(HostnameResource.HostsPath)!, StringComparison.Ordinal);
        Assert.Contains("hostnamectl set-hostname " + Desired, processes.Commands);
        Assert.Contains(HostnameResource.MetaDataPath, action.Change, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_data_document_keeps_its_cloud_config_header_and_its_other_keys()
    {
        // cloud-init ignores a user-data document that does not open with #cloud-config, and a
        // file that is silently ignored is exactly the failure mode this resource is about.
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        files.Seed(
            HostnameResource.UserDataPath,
            "#cloud-config\ntimezone: Europe/Amsterdam\nusers:\n  - name: framelink\n");

        await Resource(files, new RecordingProcessRunner()).ActAsync(TestContext.Current.CancellationToken);
        var written = files.Read(HostnameResource.UserDataPath)!;

        Assert.StartsWith("#cloud-config", written, StringComparison.Ordinal);
        Assert.Contains("timezone: Europe/Amsterdam", written, StringComparison.Ordinal);
        Assert.Contains("  - name: framelink", written, StringComparison.Ordinal);
        Assert.Contains("hostname: " + Desired, written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_with_no_header_gets_one_rather_than_being_ignored_forever()
    {
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        files.Seed(HostnameResource.UserDataPath, "hostname: raspberrypi\n");

        await Resource(files, new RecordingProcessRunner()).ActAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("#cloud-config", files.Read(HostnameResource.UserDataPath)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acting_only_on_hostnamectl_is_reverted_at_the_next_boot_and_is_caught()
    {
        // The trap itself, reproduced. This resource writes hostnamectl and nothing else — which
        // is what guide 13 step 4 and every naive implementation do — and cloud-init puts it back.
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        var cloudInit = new CloudInit(files);

        using var harness = new ReconcileHarness(Fast, new NaiveHostnameResource(cloudInit, Desired));
        harness.Telemetry.Connected = true;
        harness.Boundary.OnBoot = (_, _) =>
        {
            cloudInit.Boot();
            return Task.CompletedTask;
        };

        var outcome = await harness.ConvergeAsync();
        var status = ReconcileHarness.StatusOf(outcome, "identity.hostname.naive");

        // Never converges, is never believed, and reaches a person instead of being reported green.
        Assert.Equal(ResourceStatusKind.Escalated, status.Kind);
        Assert.Contains(Seeded, status.Delta!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acting_on_the_seed_survives_the_boot_that_reverts_hostnamectl()
    {
        using var files = new TemporaryFiles();
        SeedStockImage(files);
        var cloudInit = new CloudInit(files);

        using var harness = new ReconcileHarness(
            Fast,
            new AdoptionResource(files.Store, () => true),
            Resource(files, cloudInit));

        harness.Boundary.OnBoot = (_, _) =>
        {
            cloudInit.Boot();
            return Task.CompletedTask;
        };

        var outcome = await harness.ConvergeAsync();

        Assert.Equal(PassResult.Converged, outcome.Result);
        Assert.Equal(
            ResourceStatusKind.InSync,
            ReconcileHarness.StatusOf(outcome, HostnameResource.ResourceName).Kind);
        Assert.Equal(Desired, cloudInit.LiveHostname);
    }

    [Fact]
    public async Task A_frame_with_no_name_from_the_fleet_manager_is_left_alone()
    {
        // Inventing a name would be worse than leaving it, and a permanently drifted field the
        // operator simply left blank means a permanently not-green frame (§2.6).
        using var files = new TemporaryFiles();
        SeedStockImage(files);

        var resource = new HostnameResource(files.Files, new RecordingProcessRunner(), FleetValues.None);
        var observation = await resource.ObserveAsync(TestContext.Current.CancellationToken);

        Assert.True(observation.InSync);
        Assert.Contains("no name set", observation.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_yaml_reader_handles_what_imager_actually_writes()
    {
        Assert.Equal("pi", HostnameResource.ReadYamlScalar("local-hostname: pi\n", "local-hostname"));
        Assert.Equal("pi", HostnameResource.ReadYamlScalar("local-hostname: \"pi\"\n", "local-hostname"));
        Assert.Equal("pi", HostnameResource.ReadYamlScalar("local-hostname: 'pi'\n", "local-hostname"));
        Assert.Null(HostnameResource.ReadYamlScalar("# local-hostname: pi\n", "local-hostname"));
        Assert.Null(HostnameResource.ReadYamlScalar("  local-hostname: pi\n", "local-hostname"));
        Assert.Null(HostnameResource.ReadYamlScalar("instance-id: rpi\n", "local-hostname"));
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
        // The catalog warns that raspi-config rewrites /etc/hostname and /etc/hosts together, so
        // a hostname resource that owns only one of them half-applies.
        var updated = HostnameResource.WriteHostsEntry("127.0.0.1\tlocalhost\n", "framelink-douwe");

        Assert.Contains("127.0.1.1\tframelink-douwe", updated, StringComparison.Ordinal);
    }

    private static HostnameResource Resource(TemporaryFiles files, IProcessRunner processes) =>
        new(
            files.Files,
            processes,
            FleetValues.From(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HostnameResource.SettingKey] = Desired,
            }));

    /// <summary>What Raspberry Pi Imager leaves on the boot partition of a stock card.</summary>
    private static void SeedStockImage(TemporaryFiles files)
    {
        files.Seed(HostnameResource.MetaDataPath, $"instance-id: rpi-imager-1776005232619\nlocal-hostname: {Seeded}\n");
        files.Seed(HostnameResource.UserDataPath, $"#cloud-config\nhostname: {Seeded}\n");
        files.Seed(HostnameResource.HostsPath, $"127.0.0.1\tlocalhost\n127.0.1.1\t{Seeded}\n");
    }

    /// <summary>
    /// The NoCloud datasource, modelled as what it is: a second owner that runs at every boot.
    /// </summary>
    /// <remarks>
    /// A real <c>hostnamectl</c> in the two respects that matter — <c>set-hostname</c> takes
    /// effect immediately and lasts for the rest of the session, and <see cref="Boot"/> discards
    /// it in favour of the seed. Those two facts together are the entire trap: any check that
    /// runs before a boot sees success.
    /// </remarks>
    private sealed class CloudInit(TemporaryFiles files) : IProcessRunner
    {
        public string LiveHostname { get; private set; } = Seeded;

        public List<string> Commands { get; } = [];

        /// <summary>Applies the seed, as cloud-init does on every boot.</summary>
        public void Boot() =>
            LiveHostname = HostnameResource.ReadYamlScalar(
                files.Read(HostnameResource.MetaDataPath),
                "local-hostname") ?? LiveHostname;

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Commands.Add(executable + " " + string.Join(' ', arguments));

            if (arguments is ["--static"])
            {
                return Task.FromResult(new ProcessResult(0, LiveHostname, string.Empty));
            }

            if (arguments is ["set-hostname", var name])
            {
                LiveHostname = name;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    /// <summary>
    /// The implementation the guides describe, and the one the trap defeats.
    /// </summary>
    /// <remarks>
    /// Kept as a test fixture rather than as production code, because its whole value is showing
    /// what the shipping resource is not. Guide 13 step 4's <c>raspi-config nonint do_hostname</c>
    /// is subject to the same revert and cannot be transcribed as the Act.
    /// </remarks>
    private sealed class NaiveHostnameResource(IProcessRunner processes, string desired) : IResource
    {
        public string Name => "identity.hostname.naive";

        public string Detected => "This frame is not using the name it was given.";

        public string WhyItMatters => "The name is how this frame is found on your network.";

        public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            var result = await processes.RunAsync("hostnamectl", ["--static"], cancellationToken);
            var live = result.StandardOutput.Trim();

            return new ResourceObservation(string.Equals(live, desired, StringComparison.Ordinal), desired, live);
        }

        public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
        {
            await processes.RunAsync("hostnamectl", ["set-hostname", desired], cancellationToken);
            return new ResourceAction($"hostnamectl set-hostname {desired}", "Renaming the frame.");
        }
    }
}
