using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace FrameLink.Control.LiveKit;

/// <summary>
/// What this Fleet Manager can honestly say about the WebRTC media path (§3.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read this before reading anything below: a green result here does not mean media works.</b>
/// §3.7 splits the network exposure in two — signalling can ride Traefik as a WebSocket over TLS,
/// media cannot and is published directly as a TCP port and a UDP range — and the gap this class
/// exists for is that <i>the signalling half proves nothing about the media half</i>. A frame
/// whose WebSocket connects, whose token verifies and whose room is created has demonstrated
/// exactly one thing: that port 7880 is reachable. Nothing anywhere in the product has ever
/// checked the other half, so a household where UDP never arrives looks like a call bug.
/// </para>
/// <para>
/// <b>What this closes, and what it does not.</b> A real end-to-end media test needs two
/// participants exchanging RTP through the SFU and then agreeing that they did; one process
/// looking at its own sockets is not that and cannot be made into that. What is checkable from
/// here is the server's own end of the path — whether the media ports exist, whether the range
/// has anything left in it, and whether the address the fleet is told to dial is one this server
/// will put in an ICE candidate at all. Everything past the container's network namespace is invisible:
/// published ports, host firewall rules, the household router, the frame's own stack. So this
/// answers "is anything obviously wrong on this side", never "can a frame reach media", and its
/// findings are worded as observations for that reason.
/// </para>
/// <para>
/// <b>Nothing here binds, connects or sends.</b> Every fact is read from the operating system's
/// own tables — the listening-socket lists and this host's addresses — so the check has no side
/// effects, cannot race <c>livekit-server</c> for a port it is about to allocate, and costs a
/// couple of <c>/proc</c> reads. An earlier shape of this bound sample ports to see whether they
/// were free, which would have been a probe that occasionally caused the fault it was looking for.
/// </para>
/// </remarks>
public static class LiveKitMediaProbe
{
    /// <summary>Reads the media path facts for <paramref name="options"/>.</summary>
    /// <param name="options">The deployment being checked.</param>
    /// <param name="serverRunning">
    /// Whether a <c>livekit-server</c> child is alive right now. Half the checks below are only
    /// meaningful against a running server: a media port that is not listening because nothing is
    /// running is not a media fault, it is the process fault that is already reported.
    /// </param>
    /// <param name="host">Where this host's socket and address tables are read from.</param>
    public static LiveKitMediaCheck Inspect(
        LiveKitOptions options,
        bool serverRunning,
        ILiveKitHostSockets host)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);

        if (options.Mode is not LiveKitMode.Bundled)
        {
            // An operator's own LiveKit is on a host this process cannot see the sockets of, and
            // a disabled deployment has no media path to check. Saying nothing is the honest
            // answer; inventing one from this container's tables would describe the wrong machine.
            return LiveKitMediaCheck.NotChecked;
        }

        var findings = new List<string>();

        var listening = host.ListeningTcpPorts();
        var signalListening = listening.Contains(options.SignalPort);
        var tcpMediaListening = listening.Contains(options.TcpMediaPort);

        var rangeSize = Math.Max(0, options.UdpPortEnd - options.UdpPortStart + 1);
        var inUse = 0;
        foreach (var port in host.BoundUdpPorts())
        {
            if (port >= options.UdpPortStart && port <= options.UdpPortEnd)
            {
                inUse++;
            }
        }

        var free = Math.Max(0, rangeSize - inUse);

        // The contrast is the whole point, so it is asserted as a contrast. Signalling up and the
        // TCP media fallback down is the shape of the fault this class was written for: every
        // surface that watches the WebSocket says the call server is healthy, and the only path
        // media has when UDP is blocked is not there.
        if (serverRunning && signalListening && !tcpMediaListening)
        {
            findings.Add(
                $"The call server is answering on the signalling port ({Port(options.SignalPort)}) but "
                + $"nothing is listening on the TCP media fallback port ({Port(options.TcpMediaPort)}). "
                + "Signalling working says nothing about media: a call will connect and then carry no "
                + "picture or sound on any network where UDP does not get through.");
        }

        // Not a warning threshold, a wall. LiveKit takes a port from this range per participant
        // connection, so an exhausted range does not fail a call outright — it fails whichever
        // participants arrive after the last port went, which LiveKitOptions already names as the
        // least diagnosable failure in the whole call path.
        if (serverRunning && rangeSize > 0 && free == 0)
        {
            findings.Add(
                $"Every UDP port in the media range ({Port(options.UdpPortStart)}-{Port(options.UdpPortEnd)}) "
                + "is already in use, so the call server has none left to allocate for the next "
                + "participant. Calls will connect for some people in the room and not others.");
        }

        // Two ways for this comparison to be impossible, and both have to mean "unchecked" rather
        // than "not offered". A host that would not enumerate its own interfaces returns nothing,
        // and an empty set trivially does not contain the dialled address — so reading the absence
        // as a mismatch would turn a failed read into a confident accusation about a deployment
        // that may be perfectly correct.
        var local = host.LocalAddresses();
        var dialed = local.Count > 0 ? options.DialedAddress : null;
        var addressChecked = dialed is not null;

        // Two ways for the address frames dial to end up in an ICE candidate, and either will do:
        // this server holds it, or the generated configuration names it as the address to
        // advertise. The second is what LiveKitConfigFile writes as node_ip, and it is what makes
        // a container publishing its ports one-to-one onto a LAN host correct rather than merely
        // reachable for signalling.
        var addressIsOffered = dialed is null
            || local.Contains(dialed)
            || dialed.Equals(options.MediaAddress);

        // The one finding here that is an inference rather than a fact, and it is worth the
        // inference because it is the failure this deployment is shaped to produce. §3.7 sets
        // use_external_ip: false, which has LiveKit advertise the addresses it is locally bound
        // to; §3.8 puts this container on its own /24 bridge with a pinned private IPv4. So
        // signalling arrives on the published LAN address and works, while the ICE candidates
        // carry a bridge address no frame can route to — unless something names the dialled
        // address, which is what MediaAddress is. What is left here is the case where nothing
        // does: a public URL whose address cannot be handed to the call server, which reads as
        // configured and is not.
        if (addressChecked && !addressIsOffered)
        {
            findings.Add(
                $"Frames are told to dial {options.PublicUrl}, but {dialed} is neither an address this "
                + "server is on nor one it is configured to advertise. Signalling still reaches it "
                + "through whatever publishes that port, while media candidates carry this container's "
                + "own addresses — which a frame on another network cannot reach. Set "
                + $"{LiveKitOptions.PublicUrlVariable} to the address frames actually reach this host "
                + "on, and publish the media ports there one-to-one.");
        }

        return new LiveKitMediaCheck
        {
            Checked = true,
            SignalPortListening = signalListening,
            TcpMediaPortListening = tcpMediaListening,
            UdpRangeSize = rangeSize,
            UdpRangeFree = free,
            DialedAddressChecked = addressChecked,
            DialedAddressIsOffered = addressIsOffered,
            Findings = findings,
        };
    }

    private static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What <see cref="LiveKitMediaProbe"/> found, and how far it looked.</summary>
public sealed record LiveKitMediaCheck
{
    /// <summary>The answer for a deployment whose media path is on somebody else's host.</summary>
    public static LiveKitMediaCheck NotChecked { get; } = new() { Findings = [] };

    /// <summary>Whether the check ran at all. False for external and disabled deployments.</summary>
    public bool Checked { get; init; }

    /// <summary>Whether something is listening on the signalling port.</summary>
    public bool SignalPortListening { get; init; }

    /// <summary>Whether something is listening on the TCP media fallback port.</summary>
    public bool TcpMediaPortListening { get; init; }

    /// <summary>How many UDP ports the configured media range holds.</summary>
    public int UdpRangeSize { get; init; }

    /// <summary>
    /// How many of them are not currently bound.
    /// </summary>
    /// <remarks>
    /// Deliberately <i>free</i> rather than <i>held by something else</i>. The listener tables say
    /// a port is bound, not who bound it, and during a call the call server itself is holding
    /// ports out of this range — which is the range working, not the range being stolen. Zero is
    /// the only value that means something on its own.
    /// </remarks>
    public int UdpRangeFree { get; init; }

    /// <summary>Whether the address frames dial is a literal this check could compare at all.</summary>
    public bool DialedAddressChecked { get; init; }

    /// <summary>
    /// Whether the call server will put that address in its ICE candidates. True when unchecked.
    /// </summary>
    /// <remarks>
    /// True by either route, because either one works: an address this process is on is advertised
    /// because it is held, and an address the generated configuration names is advertised because
    /// <c>node_ip</c> rewrites the host candidates to it. It says nothing about whether media then
    /// arrives — the ports still have to be published one-to-one on that address, which is on the
    /// far side of this container's network namespace and invisible from here.
    /// </remarks>
    public bool DialedAddressIsOffered { get; init; } = true;

    /// <summary>What an operator should look at, in plain sentences. Empty means nothing seen.</summary>
    public required IReadOnlyList<string> Findings { get; init; }
}

/// <summary>The host tables <see cref="LiveKitMediaProbe"/> reads, as a seam.</summary>
/// <remarks>
/// A test cannot arrange for a port to be missing on the machine running it, and a check that can
/// only be exercised by the condition it detects is a check nobody verifies. So the three reads
/// are an interface with one real implementation.
/// </remarks>
public interface ILiveKitHostSockets
{
    /// <summary>Every TCP port something is listening on, in this network namespace.</summary>
    IReadOnlyCollection<int> ListeningTcpPorts();

    /// <summary>Every UDP port something is bound to, in this network namespace.</summary>
    IReadOnlyCollection<int> BoundUdpPorts();

    /// <summary>Every unicast address this host holds.</summary>
    IReadOnlyCollection<IPAddress> LocalAddresses();
}

/// <summary>The real one, reading the operating system's tables.</summary>
public sealed class SystemHostSockets : ILiveKitHostSockets
{
    /// <summary>The shared instance. It holds no state.</summary>
    public static SystemHostSockets Instance { get; } = new();

    /// <inheritdoc/>
    public IReadOnlyCollection<int> ListeningTcpPorts() =>
        Read(static properties => properties.GetActiveTcpListeners());

    /// <inheritdoc/>
    public IReadOnlyCollection<int> BoundUdpPorts() =>
        Read(static properties => properties.GetActiveUdpListeners());

    /// <inheritdoc/>
    public IReadOnlyCollection<IPAddress> LocalAddresses()
    {
        var addresses = new HashSet<IPAddress>();

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    addresses.Add(unicast.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // A host that will not describe its own interfaces is reported as having none, which
            // makes the advertised-address comparison unchecked rather than falsely failed.
            return [];
        }

        return addresses;
    }

    private static HashSet<int> Read(Func<IPGlobalProperties, IPEndPoint[]> endpoints)
    {
        try
        {
            var ports = new HashSet<int>();
            foreach (var endpoint in endpoints(IPGlobalProperties.GetIPGlobalProperties()))
            {
                ports.Add(endpoint.Port);
            }

            return ports;
        }
        catch (Exception exception)
            when (exception is NetworkInformationException or PlatformNotSupportedException or IOException)
        {
            // No tables to read is not a media fault. An empty set makes every check that needs
            // them inconclusive, and an inconclusive check reports nothing — which is the whole
            // discipline of this file: say what is known, never fill a gap with a guess.
            return [];
        }
    }
}
