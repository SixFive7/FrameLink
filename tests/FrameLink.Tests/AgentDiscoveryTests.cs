using System.Globalization;
using System.Text;
using FrameLink.Agent.Discovery;
using FrameLink.Agent.Hosting;

namespace FrameLink.Tests;

/// <summary>
/// Endpoint discovery — version2.md §4.3's one code path: find a candidate → enroll → persist →
/// <b>never rediscover</b>.
/// </summary>
public sealed class AgentDiscoveryTests
{
    [Fact]
    public async Task The_install_flag_wins_over_the_boot_file_and_mdns()
    {
        var flag = new StubEndpointSource("install-flag", "https://flag.example.org/");
        var boot = new StubEndpointSource("boot-file", "https://boot.example.org/");
        var mdns = new StubEndpointSource("mdns", "http://192.168.1.9:8080/");
        using var temporary = new TemporaryStore();

        var resolved = await Resolver(temporary, flag, boot, mdns).ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("install-flag", resolved!.DiscoveredBy);
        Assert.Equal(new Uri("https://flag.example.org/"), resolved.Endpoints[0]);

        // The later candidates are not merely outranked, they are never consulted.
        Assert.Equal(0, boot.Calls);
        Assert.Equal(0, mdns.Calls);
    }

    [Fact]
    public async Task The_boot_file_wins_over_mdns()
    {
        var flag = new StubEndpointSource("install-flag");
        var boot = new StubEndpointSource("boot-file", "https://boot.example.org/");
        var mdns = new StubEndpointSource("mdns", "http://192.168.1.9:8080/");
        using var temporary = new TemporaryStore();

        var resolved = await Resolver(temporary, flag, boot, mdns).ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("boot-file", resolved!.DiscoveredBy);
        Assert.Equal(0, mdns.Calls);
    }

    [Fact]
    public async Task A_frame_that_has_been_told_where_it_belongs_never_asks_again()
    {
        // §4.3's "never rediscover", enforced structurally. The mDNS candidate makes the local
        // network a voice in this decision; a frame that re-ran discovery every boot could be
        // moved to another Fleet Manager by anything on the LAN willing to answer first.
        var flag = new StubEndpointSource("install-flag", "https://first.example.org/");
        using var temporary = new TemporaryStore();
        await Resolver(temporary, flag).ResolveAsync(TestContext.Current.CancellationToken);

        var shouter = new StubEndpointSource("mdns", "http://192.168.1.200:8080/");
        var again = await Resolver(temporary, shouter).ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://first.example.org/"), again!.Endpoints[0]);
        Assert.Equal(0, shouter.Calls);
    }

    [Fact]
    public async Task The_persisted_list_keeps_the_public_url_first_and_the_lan_address_second()
    {
        // The whole reason §4.3 stores a list: a frame built on the operator's bench survives being
        // shipped to another household, and hairpin-NAT setups still work from inside.
        var source = new StubEndpointSource("install-flag", "https://framelink.example.org/", "http://192.168.1.9:8080/");
        using var temporary = new TemporaryStore();

        await Resolver(temporary, source).ResolveAsync(TestContext.Current.CancellationToken);
        var reloaded = Resolver(temporary).Persisted();

        Assert.Equal(2, reloaded!.Endpoints.Count);
        Assert.Equal(new Uri("https://framelink.example.org/"), reloaded.Endpoints[0]);
        Assert.Equal(new Uri("http://192.168.1.9:8080/"), reloaded.Endpoints[1]);
    }

    [Fact]
    public async Task A_frame_nobody_configured_persists_nothing_and_says_so()
    {
        using var temporary = new TemporaryStore();

        var resolved = await Resolver(temporary, new StubEndpointSource("install-flag"))
            .ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Null(resolved);
        Assert.False(temporary.Store.Exists(EndpointResolver.FileName));
    }

    [Fact]
    public async Task A_corrupt_persisted_list_is_rediscovered_rather_than_bricking_the_frame()
    {
        using var temporary = new TemporaryStore();
        temporary.Store.WriteText(EndpointResolver.FileName, "{ this is not json");
        var source = new StubEndpointSource("install-flag", "https://recovered.example.org/");

        var resolved = await Resolver(temporary, source).ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://recovered.example.org/"), resolved!.Endpoints[0]);
    }

    [Theory]
    [InlineData("--control-url", "https://flag.example.org/")]
    [InlineData("--control-url=https://flag.example.org/", null)]
    public async Task The_install_flag_is_read_from_the_command_line_in_either_form(string first, string? second)
    {
        string[] arguments = second is null ? ["run", first] : ["run", first, second];
        var source = new InstallFlagEndpointSource(arguments, _ => null);

        var found = await source.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://flag.example.org/"), Assert.Single(found));
    }

    [Fact]
    public async Task The_install_flag_falls_back_to_the_environment()
    {
        var source = new InstallFlagEndpointSource(
            [],
            name => name switch
            {
                InstallFlagEndpointSource.ControlUrlVariable => "https://env.example.org/",
                InstallFlagEndpointSource.LanUrlVariable => "http://10.0.0.4:8080/",
                _ => null,
            });

        var found = await source.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, found.Count);
        Assert.Equal(new Uri("https://env.example.org/"), found[0]);
    }

    [Fact]
    public async Task The_boot_file_is_read_from_the_partition_the_card_reader_can_see()
    {
        var files = new MemoryTextFiles();
        files.Files["/boot/firmware/framelink.conf"] =
            "# FrameLink\ncontrol-url=https://framelink.example.org\ncontrol-lan-url = http://192.168.1.9:8080\n";

        var found = await new BootFileEndpointSource(files).DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, found.Count);
        Assert.Equal(new Uri("https://framelink.example.org"), found[0]);
        Assert.Equal(new Uri("http://192.168.1.9:8080"), found[1]);
    }

    [Fact]
    public async Task The_legacy_boot_path_is_still_read()
    {
        var files = new MemoryTextFiles();
        files.Files["/boot/framelink.conf"] = "control-url=https://legacy.example.org\n";

        var found = await new BootFileEndpointSource(files).DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://legacy.example.org"), Assert.Single(found));
    }

    [Fact]
    public async Task A_typo_in_the_boot_file_leaves_the_frame_narrating_rather_than_crashing()
    {
        // The frame's own screen is the diagnostic (§3.2 makes the same argument for the server).
        // A restart loop before anything can be read would take that away.
        var files = new MemoryTextFiles();
        files.Files["/boot/firmware/framelink.conf"] = "control-url=htps:/nonsense\ncontrol-lan-url=\n";

        var found = await new BootFileEndpointSource(files).DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public void Only_http_and_https_endpoints_are_accepted()
    {
        var parsed = EndpointParsing.Parse("file:///etc/passwd", "ws://sneaky.example.org", "https://good.example.org");

        Assert.Equal(new Uri("https://good.example.org"), Assert.Single(parsed));
    }

    [Fact]
    public void A_duplicated_address_is_listed_once()
    {
        var parsed = EndpointParsing.Parse("https://a.example.org/", "https://a.example.org/");

        Assert.Single(parsed);
    }

    [Fact]
    public async Task Mdns_produces_the_public_url_first_and_the_advertised_host_second()
    {
        var query = new StubMulticastQuery();
        query.Responses.Add(MdnsPacket.Advertise(
            instance: "Home._framelink._tcp.local",
            host: "framelink.local",
            address: "192.168.1.9",
            port: 8080,
            text: ["url=https://framelink.example.org/", "path=/"]));

        var found = await new MdnsEndpointSource(query).DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, found.Count);
        Assert.Equal(new Uri("https://framelink.example.org/"), found[0]);
        Assert.Equal(new Uri("http://192.168.1.9:8080/"), found[1]);
    }

    [Fact]
    public async Task Mdns_without_a_published_url_still_yields_the_lan_address()
    {
        var query = new StubMulticastQuery();
        query.Responses.Add(MdnsPacket.Advertise(
            instance: "Home._framelink._tcp.local",
            host: "framelink.local",
            address: "10.1.2.3",
            port: 9000,
            text: []));

        var found = await new MdnsEndpointSource(query).DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("http://10.1.2.3:9000/"), Assert.Single(found));
    }

    [Fact]
    public async Task Mdns_asks_for_the_framelink_service_type()
    {
        var query = new StubMulticastQuery();

        await new MdnsEndpointSource(query).DiscoverAsync(TestContext.Current.CancellationToken);

        var question = Encoding.UTF8.GetString(query.LastQuery!);
        Assert.Contains("_framelink", question, StringComparison.Ordinal);
        Assert.Contains("_tcp", question, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Noise_on_the_multicast_group_produces_nothing_rather_than_an_exception()
    {
        // A multicast socket receives whatever anything on the LAN chooses to send, so a broken
        // packet is ordinary traffic. mDNS is convenience only; it must never be able to fail the
        // agent's startup.
        var query = new StubMulticastQuery();
        query.Responses.Add([0x00, 0x01, 0x02]);
        query.Responses.Add(Encoding.UTF8.GetBytes("not a dns message at all, just some bytes"));

        var found = await new MdnsEndpointSource(query).DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public void A_dns_message_with_compression_pointers_is_decoded_correctly()
    {
        // Real mDNS responders compress aggressively; a parser that cannot follow a 0xC0 pointer
        // reads garbage names and silently finds nothing.
        var packet = MdnsPacket.Advertise("Home._framelink._tcp.local", "framelink.local", "192.168.1.9", 8080, []);

        var records = DnsMessage.ReadRecords(packet);

        Assert.Contains(records, r => r.Type == DnsRecordType.ServiceInstance && r.Target == "Home._framelink._tcp.local");
        Assert.Contains(records, r => r.Type == DnsRecordType.Srv && r.Target == "framelink.local" && r.Port == 8080);
        Assert.Contains(records, r => r.Type == DnsRecordType.A && r.Address == "192.168.1.9");
    }

    [Fact]
    public void A_truncated_message_yields_what_could_be_read_and_stops()
    {
        var packet = MdnsPacket.Advertise("Home._framelink._tcp.local", "framelink.local", "192.168.1.9", 8080, []);

        var records = DnsMessage.ReadRecords(packet.AsSpan(0, packet.Length / 2));

        Assert.True(records.Count < 4);
    }

    private static EndpointResolver Resolver(TemporaryStore temporary, params IEndpointSource[] sources) =>
        new(temporary.Store, sources, new ManualClock(), NullLog.Instance);
}

/// <summary>Builds a realistic DNS-SD response, compression pointers and all.</summary>
internal static class MdnsPacket
{
    public static byte[] Advertise(
        string instance,
        string host,
        string address,
        int port,
        IReadOnlyList<string> text)
    {
        var message = new List<byte>();
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Header: response, four answers, no questions.
        message.AddRange([0, 0, 0x84, 0, 0, 0, 0, 4, 0, 0, 0, 0]);

        AppendRecord(message, offsets, "_framelink._tcp.local", DnsRecordType.ServiceInstance, (data, start) =>
            AppendName(data, offsets, instance, start + data.Count));

        AppendRecord(message, offsets, instance, DnsRecordType.Srv, (data, start) =>
        {
            data.AddRange([0, 0, 0, 0]);
            data.Add((byte)(port >> 8));
            data.Add((byte)(port & 0xFF));
            AppendName(data, offsets, host, start + data.Count);
        });

        AppendRecord(message, offsets, instance, DnsRecordType.Txt, (data, _) =>
        {
            foreach (var entry in text)
            {
                var bytes = Encoding.UTF8.GetBytes(entry);
                data.Add((byte)bytes.Length);
                data.AddRange(bytes);
            }

            if (text.Count == 0)
            {
                data.Add(0);
            }
        });

        AppendRecord(message, offsets, host, DnsRecordType.A, (data, _) =>
        {
            foreach (var octet in address.Split('.'))
            {
                data.Add(byte.Parse(octet, CultureInfo.InvariantCulture));
            }
        });

        return [.. message];
    }

    private static void AppendRecord(
        List<byte> message,
        Dictionary<string, int> offsets,
        string name,
        DnsRecordType type,
        Action<List<byte>, int> writeData)
    {
        AppendName(message, offsets, name, message.Count);
        message.Add((byte)((ushort)type >> 8));
        message.Add((byte)((ushort)type & 0xFF));
        message.AddRange([0x80, 0x01, 0, 0, 0x00, 0x78]);

        // The record's data begins two bytes past here, after the length field, and any name
        // written inside it has to register that real position or every compression pointer
        // aimed at it lands two bytes early.
        var data = new List<byte>();
        writeData(data, message.Count + 2);

        message.Add((byte)(data.Count >> 8));
        message.Add((byte)(data.Count & 0xFF));
        message.AddRange(data);
    }

    private static void AppendName(
        List<byte> target,
        Dictionary<string, int> offsets,
        string name,
        int positionInMessage)
    {
        if (offsets.TryGetValue(name, out var known))
        {
            target.Add((byte)(0xC0 | (known >> 8)));
            target.Add((byte)(known & 0xFF));
            return;
        }

        offsets[name] = positionInMessage;

        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            target.Add((byte)bytes.Length);
            target.AddRange(bytes);
        }

        target.Add(0);
    }
}
