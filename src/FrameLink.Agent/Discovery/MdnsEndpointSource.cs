using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Discovery;

/// <summary>Sends one multicast question and collects whatever answers arrive.</summary>
public interface IMulticastQuery
{
    /// <summary>Broadcasts <paramref name="query"/> and gathers replies for <paramref name="window"/>.</summary>
    Task<IReadOnlyList<byte[]>> AskAsync(byte[] query, TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>
/// §4.3's third and last candidate: DNS-SD over multicast.
/// </summary>
/// <remarks>
/// <para>
/// Explicitly <b>convenience only, never a dependency</b>. It is last because it is the only
/// candidate nobody deliberately configured, and because it is the only one an unrelated device
/// on the LAN can answer. Everything it finds is written once and then never consulted again
/// (§4.3's "never rediscover"), which bounds the blast radius of a wrong answer to the moment
/// of first boot.
/// </para>
/// <para>
/// A <c>url=</c> entry in the service's TXT record takes precedence over the address the A
/// record gives, and both are kept. That is what produces §4.3's ordering directly out of
/// discovery: the Fleet Manager advertises its public URL in TXT, the A record supplies the LAN
/// address, and the frame ends up with public-first, LAN-second without anyone typing either.
/// </para>
/// </remarks>
public sealed class MdnsEndpointSource : IEndpointSource
{
    /// <summary>The DNS-SD service type the Fleet Manager advertises.</summary>
    public const string ServiceType = "_framelink._tcp.local";

    /// <summary>TXT key carrying the public URL.</summary>
    public const string PublicUrlKey = "url=";

    /// <summary>TXT key carrying the path the agent socket lives on.</summary>
    public const string PathKey = "path=";

    private readonly IMulticastQuery _query;
    private readonly TimeSpan _window;

    /// <summary>Creates a source over <paramref name="query"/>.</summary>
    public MdnsEndpointSource(IMulticastQuery query, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        _query = query;
        _window = window ?? TimeSpan.FromSeconds(2);
    }

    /// <inheritdoc/>
    public string Name => "mdns";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Uri>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var question = DnsMessage.BuildQuery(ServiceType, DnsRecordType.ServiceInstance);
        var responses = await _query.AskAsync(question, _window, cancellationToken).ConfigureAwait(false);

        var records = new List<DnsRecord>();
        foreach (var response in responses)
        {
            records.AddRange(DnsMessage.ReadRecords(response));
        }

        return Assemble(records);
    }

    /// <summary>Turns loose records into the ordered endpoint list of §4.3.</summary>
    public static IReadOnlyList<Uri> Assemble(IReadOnlyList<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        foreach (var pointer in records.Where(r => r.Type == DnsRecordType.ServiceInstance && r.Target is not null))
        {
            var service = records.FirstOrDefault(r =>
                r.Type == DnsRecordType.Srv
                && string.Equals(r.Name, pointer.Target, StringComparison.OrdinalIgnoreCase));

            if (service?.Target is null || service.Port <= 0)
            {
                continue;
            }

            var text = records.FirstOrDefault(r =>
                r.Type == DnsRecordType.Txt
                && string.Equals(r.Name, pointer.Target, StringComparison.OrdinalIgnoreCase));

            var address = records.FirstOrDefault(r =>
                r.Type == DnsRecordType.A
                && string.Equals(r.Name, service.Target, StringComparison.OrdinalIgnoreCase));

            var path = ReadValue(text, PathKey) ?? "/";
            var lan = address?.Address is null
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"http://{address.Address}:{service.Port}{path}");

            var endpoints = EndpointParsing.Parse(ReadValue(text, PublicUrlKey), lan);
            if (endpoints.Count > 0)
            {
                return endpoints;
            }
        }

        return [];
    }

    private static string? ReadValue(DnsRecord? text, string key)
    {
        if (text is null)
        {
            return null;
        }

        foreach (var entry in text.Text)
        {
            if (entry.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return entry[key.Length..];
            }
        }

        return null;
    }
}

/// <summary>Asks on 224.0.0.251:5353 and listens for the configured window.</summary>
public sealed class UdpMulticastQuery : IMulticastQuery
{
    private static readonly IPAddress GroupAddress = IPAddress.Parse("224.0.0.251");
    private const int MulticastPort = 5353;
    private const int MaximumResponses = 32;

    private readonly IAgentLog _log;

    /// <summary>Creates a query that reports trouble to <paramref name="log"/>.</summary>
    public UdpMulticastQuery(IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<byte[]>> AskAsync(
        byte[] query,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var responses = new List<byte[]>();

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(GroupAddress, IPAddress.Any));

            var destination = new IPEndPoint(GroupAddress, MulticastPort);
            await socket.SendToAsync(query, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(window);

            var buffer = new byte[4096];
            while (responses.Count < MaximumResponses && !deadline.IsCancellationRequested)
            {
                var received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, destination, deadline.Token)
                    .ConfigureAwait(false);

                if (received.ReceivedBytes > 0)
                {
                    responses.Add(buffer[..received.ReceivedBytes]);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The listening window closed; whatever arrived is the answer.
        }
        catch (SocketException exception)
        {
            _log.Warn($"mDNS discovery could not run ({exception.SocketErrorCode}); this is optional, continuing.");
        }

        return responses;
    }
}
