using System.Net;
using FrameLink.Control.LiveKit;

namespace FrameLink.Tests;

/// <summary>
/// The media half of §3.7's split exposure, and the honest edge of what can be checked.
/// </summary>
/// <remarks>
/// <para>
/// §3.7 says the network exposure "splits in two, unavoidably": signalling can ride Traefik as a
/// WebSocket over TLS, WebRTC media cannot and is published directly as a TCP fallback port and a
/// UDP range. Every existing surface watches the signalling half — the socket, the token, the
/// room — and none of them touches the media half, so a deployment where UDP never arrives
/// presents as a call bug on the frames.
/// </para>
/// <para>
/// <b>What is asserted here is deliberately smaller than "media works".</b> Proving that needs
/// two participants exchanging RTP through the SFU and agreeing that they did, which no unit test
/// and no single process can stand in for. These assert the server's own end: that the media
/// ports exist while signalling is up, that an exhausted range is named, and that an address
/// frames are told to dial which this server will neither hold nor advertise is called out rather
/// than left silent. Everything past the container — published ports, host firewall, the
/// household router — is invisible to the check and is invisible here too.
/// </para>
/// </remarks>
public sealed class LiveKitMediaProbeTests
{
    private static readonly IPAddress Bridge = IPAddress.Parse("172.16.14.3");
    private static readonly IPAddress Lan = IPAddress.Parse("10.20.30.250");

    private static LiveKitOptions Bundled(string publicUrl = "ws://10.20.30.250:7880") =>
        new()
        {
            Directory = "/data/livekit",
            Mode = LiveKitMode.Bundled,
            PublicUrl = publicUrl,
        };

    [Fact]
    public void A_healthy_bundled_server_produces_no_findings()
    {
        var host = new FakeHostSockets
        {
            Tcp = { 7880, 7881 },
            Addresses = { Lan },
        };

        var check = LiveKitMediaProbe.Inspect(Bundled(), serverRunning: true, host);

        Assert.True(check.Checked);
        Assert.True(check.SignalPortListening);
        Assert.True(check.TcpMediaPortListening);
        Assert.Equal(60, check.UdpRangeSize);
        Assert.Equal(60, check.UdpRangeFree);
        Assert.Empty(check.Findings);
    }

    [Fact]
    public void Signalling_up_with_the_media_fallback_down_is_the_fault_this_exists_for()
    {
        // The whole point of the check, stated as a contrast. Everything that watches the
        // WebSocket says this server is healthy; the only path media has when a household blocks
        // UDP is not there. A call connects and carries nothing.
        var host = new FakeHostSockets
        {
            Tcp = { 7880 },
            Addresses = { Lan },
        };

        var check = LiveKitMediaProbe.Inspect(Bundled(), serverRunning: true, host);

        Assert.False(check.TcpMediaPortListening);
        var finding = Assert.Single(check.Findings);
        Assert.Contains("7881", finding, StringComparison.Ordinal);
        Assert.Contains("Signalling working says nothing about media", finding, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_concluded_while_the_server_is_not_running()
    {
        // A media port that is not listening because nothing is running is not a media fault. It
        // is the process fault the supervisor already reports, and saying it twice in different
        // words sends an operator looking for a second problem that does not exist.
        var host = new FakeHostSockets { Addresses = { Lan } };

        Assert.Empty(LiveKitMediaProbe.Inspect(Bundled(), serverRunning: false, host).Findings);
    }

    [Fact]
    public void An_exhausted_udp_range_is_named_before_it_becomes_a_call_that_half_works()
    {
        // LiveKit takes a port per participant connection, so a full range does not fail a call
        // outright — it fails whoever arrives after the last port went. LiveKitOptions calls that
        // "the least diagnosable failure in the whole call path", which is exactly why it is worth
        // a sentence here rather than an inference from six people's experience of one evening.
        var host = new FakeHostSockets
        {
            Tcp = { 7880, 7881 },
            Addresses = { Lan },
        };

        for (var port = 50_000; port <= 50_059; port++)
        {
            host.Udp.Add(port);
        }

        var check = LiveKitMediaProbe.Inspect(Bundled(), serverRunning: true, host);

        Assert.Equal(0, check.UdpRangeFree);
        Assert.Contains(check.Findings, finding => finding.Contains("none left to allocate", StringComparison.Ordinal));
    }

    [Fact]
    public void Ports_in_use_below_the_ceiling_are_the_range_working_and_say_nothing()
    {
        // The listener tables say a port is bound, never who bound it — and during a call the call
        // server itself is holding ports out of this range. Reporting that as contention would
        // make every live call look like a fault.
        var host = new FakeHostSockets
        {
            Tcp = { 7880, 7881 },
            Udp = { 50_000, 50_001, 50_002 },
            Addresses = { Lan },
        };

        var check = LiveKitMediaProbe.Inspect(Bundled(), serverRunning: true, host);

        Assert.Equal(57, check.UdpRangeFree);
        Assert.Empty(check.Findings);
    }

    [Fact]
    public void The_container_on_its_own_bridge_is_answered_by_the_address_it_is_told_to_advertise()
    {
        // §3.8 puts this container on its own /24 bridge with a pinned private IPv4, and §3.7 sets
        // use_external_ip: false — so on its own, LiveKit would advertise the address it is locally
        // on and the ICE candidates would name 172.16.14.3, which no frame on the house network can
        // route to. That is the deployment's shape, not a hypothetical, and what closes it is that
        // the generated configuration names the dialled address as node_ip. The check has to see
        // that too, or it reports a fault that has been fixed.
        var host = new FakeHostSockets
        {
            Tcp = { 7880, 7881 },
            Addresses = { Bridge },
        };

        var options = Bundled();
        var check = LiveKitMediaProbe.Inspect(options, serverRunning: true, host);

        Assert.Equal(Lan, options.MediaAddress);
        Assert.True(check.DialedAddressChecked);
        Assert.True(check.DialedAddressIsOffered);
        Assert.Empty(check.Findings);
    }

    [Fact]
    public void An_address_nothing_can_advertise_is_still_the_call_that_connects_and_carries_nothing()
    {
        // The unspecified address parses, is not loopback, and is a plausible thing to write into a
        // public URL — and it is the one literal that cannot be handed to the call server as the
        // address to advertise. So it is neither held here nor named in the configuration, which is
        // the whole condition this finding exists for: signalling reaches whatever publishes the
        // port, and the candidates carry a bridge address instead.
        var host = new FakeHostSockets
        {
            Tcp = { 7880, 7881 },
            Addresses = { Bridge },
        };

        var options = Bundled("ws://0.0.0.0:7880");
        var check = LiveKitMediaProbe.Inspect(options, serverRunning: true, host);

        Assert.Null(options.MediaAddress);
        Assert.True(check.DialedAddressChecked);
        Assert.False(check.DialedAddressIsOffered);
        Assert.Contains(
            check.Findings,
            finding => finding.Contains(
                "neither an address this server is on nor one it is configured to advertise",
                StringComparison.Ordinal));
    }

    [Fact]
    public void A_server_standing_on_the_address_frames_dial_needs_nothing_advertised()
    {
        // The other correct deployment: the Fleet Manager on the LAN host itself rather than behind
        // a published port. LiveKit is already on 10.20.30.250, so the candidates already carry it
        // and node_ip changes nothing — the check must be silent here for the same reason it is
        // silent above, not for a different one.
        var host = new FakeHostSockets
        {
            Tcp = { 7880, 7881 },
            Addresses = { Lan },
        };

        var check = LiveKitMediaProbe.Inspect(Bundled(), serverRunning: true, host);

        Assert.True(check.DialedAddressChecked);
        Assert.True(check.DialedAddressIsOffered);
        Assert.Empty(check.Findings);
    }

    [Theory]
    [InlineData("wss://framelink.huisman.io")]
    [InlineData("")]
    [InlineData("not a url")]
    public void A_named_host_is_reported_unchecked_rather_than_resolved(string publicUrl)
    {
        // Resolving it would be a network call on a status read that can hang, can answer
        // differently than the frame's resolver does, and can produce a finding about a name this
        // container merely sees differently. Unchecked is the honest form.
        var host = new FakeHostSockets
        {
            Tcp = { 7880, 7881 },
            Addresses = { Bridge },
        };

        var options = Bundled(publicUrl);
        var check = LiveKitMediaProbe.Inspect(options, serverRunning: true, host);

        // And nothing is written into the configuration for it either, which is the same refusal
        // in the other half: an address the call server would advertise cannot be invented from a
        // name this container would have to resolve to read.
        Assert.Null(options.MediaAddress);
        Assert.False(check.DialedAddressChecked);
        Assert.True(check.DialedAddressIsOffered);
        Assert.Empty(check.Findings);
    }

    [Theory]
    [InlineData(LiveKitMode.External)]
    [InlineData(LiveKitMode.Disabled)]
    public void Somebody_elses_livekit_is_on_a_host_whose_sockets_this_process_cannot_see(LiveKitMode mode)
    {
        // The escape hatch of §3.7 puts the media path on a machine this container has no tables
        // for. Describing this container's sockets as though they were that machine's would be a
        // confident answer about the wrong host.
        var options = new LiveKitOptions
        {
            Directory = "/data/livekit",
            Mode = mode,
            ExternalUrl = "ws://192.0.2.10:7880",
            ExternalKey = "key",
            ExternalSecret = "secret",
        };

        var check = LiveKitMediaProbe.Inspect(options, serverRunning: true, new FakeHostSockets());

        Assert.False(check.Checked);
        Assert.Empty(check.Findings);
    }

    [Fact]
    public void A_host_that_will_not_describe_itself_produces_no_findings_rather_than_false_ones()
    {
        // Empty tables mean the check could not look, and a check that could not look reports
        // nothing. The port findings get this by construction — each needs a positive observation
        // to fire — but the address comparison does not: an empty address set trivially fails to
        // contain the advertised one, so an unenumerable host would have been accused of the
        // bridge misconfiguration on no evidence at all. It is unchecked instead.
        var check = LiveKitMediaProbe.Inspect(Bundled(), serverRunning: true, new FakeHostSockets());

        Assert.True(check.Checked);
        Assert.False(check.SignalPortListening);
        Assert.False(check.DialedAddressChecked);
        Assert.True(check.DialedAddressIsOffered);
        Assert.Empty(check.Findings);
    }

    private sealed class FakeHostSockets : ILiveKitHostSockets
    {
        public HashSet<int> Tcp { get; } = [];

        public HashSet<int> Udp { get; } = [];

        public HashSet<IPAddress> Addresses { get; } = [];

        public IReadOnlyCollection<int> ListeningTcpPorts() => Tcp;

        public IReadOnlyCollection<int> BoundUdpPorts() => Udp;

        public IReadOnlyCollection<IPAddress> LocalAddresses() => Addresses;
    }
}
