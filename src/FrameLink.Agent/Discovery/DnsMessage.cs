using System.Buffers.Binary;
using System.Text;

namespace FrameLink.Agent.Discovery;

/// <summary>Record types this agent understands.</summary>
public enum DnsRecordType : ushort
{
    /// <summary>IPv4 address.</summary>
    A = 1,

    /// <summary>DNS type 12 (PTR). In DNS-SD it enumerates instances of a service type.</summary>
    ServiceInstance = 12,

    /// <summary>Key/value metadata.</summary>
    Txt = 16,

    /// <summary>IPv6 address.</summary>
    Aaaa = 28,

    /// <summary>Service location: host and port.</summary>
    Srv = 33,

    /// <summary>Wildcard, used in queries.</summary>
    Any = 255,
}

/// <summary>One decoded resource record.</summary>
public sealed record DnsRecord
{
    private static readonly IReadOnlyList<string> NoText = [];

    /// <summary>Owner name.</summary>
    public required string Name { get; init; }

    /// <summary>Record type.</summary>
    public required DnsRecordType Type { get; init; }

    /// <summary>Target name, for <see cref="DnsRecordType.ServiceInstance"/> and <see cref="DnsRecordType.Srv"/>.</summary>
    public string? Target { get; init; }

    /// <summary>Port, for <see cref="DnsRecordType.Srv"/>.</summary>
    public int Port { get; init; }

    /// <summary>Address, for <see cref="DnsRecordType.A"/> and <see cref="DnsRecordType.Aaaa"/>.</summary>
    public string? Address { get; init; }

    /// <summary>Strings, for <see cref="DnsRecordType.Txt"/>.</summary>
    public IReadOnlyList<string> Text { get; init; } = NoText;
}

/// <summary>
/// Just enough DNS to ask one multicast question and read the answers.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than taken from a package, for two reasons that both come from §2.1: the
/// agent is one self-contained AOT binary, and mDNS is <i>convenience only, never a dependency</i>
/// (§4.3). A discovery mechanism the product is explicitly allowed to live without does not
/// justify a third-party dependency in the delivery artifact.
/// </para>
/// <para>
/// Everything here is a pure function over bytes, so the whole of discovery's parsing is
/// testable with no network at all — which matters because the alternative is a test that
/// depends on what happens to be advertising on the developer's LAN.
/// </para>
/// </remarks>
public static class DnsMessage
{
    private const int HeaderLength = 12;
    private const int MaximumPointerHops = 32;
    private const ushort ClassInternet = 1;

    /// <summary>Builds a one-question query.</summary>
    public static byte[] BuildQuery(string name, DnsRecordType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var body = new List<byte>(HeaderLength + name.Length + 8);

        // Header: id 0 (mDNS ignores it), no flags, one question.
        body.AddRange([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]);

        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            if (encoded.Length > 63)
            {
                throw new ArgumentException($"Label '{label}' exceeds 63 bytes.", nameof(name));
            }

            body.Add((byte)encoded.Length);
            body.AddRange(encoded);
        }

        body.Add(0);
        body.Add((byte)((ushort)type >> 8));
        body.Add((byte)((ushort)type & 0xFF));
        body.Add(ClassInternet >> 8);
        body.Add(ClassInternet & 0xFF);

        return [.. body];
    }

    /// <summary>
    /// Reads every answer, authority and additional record out of a response.
    /// </summary>
    /// <remarks>
    /// Malformed input yields the records decoded so far rather than throwing. A multicast
    /// socket receives whatever anything on the LAN chooses to send, so a broken packet is
    /// ordinary traffic, not an exceptional condition.
    /// </remarks>
    public static IReadOnlyList<DnsRecord> ReadRecords(ReadOnlySpan<byte> message)
    {
        var records = new List<DnsRecord>();
        if (message.Length < HeaderLength)
        {
            return records;
        }

        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var authorities = BinaryPrimitives.ReadUInt16BigEndian(message[8..]);
        var additionals = BinaryPrimitives.ReadUInt16BigEndian(message[10..]);
        var total = answers + authorities + additionals;

        var offset = HeaderLength;
        for (var index = 0; index < questions; index++)
        {
            if (!TrySkipName(message, ref offset) || offset + 4 > message.Length)
            {
                return records;
            }

            offset += 4;
        }

        for (var index = 0; index < total; index++)
        {
            if (!TryReadRecord(message, ref offset, out var record))
            {
                break;
            }

            records.Add(record);
        }

        return records;
    }

    private static bool TryReadRecord(ReadOnlySpan<byte> message, ref int offset, out DnsRecord record)
    {
        record = null!;

        if (!TryReadName(message, ref offset, out var name) || offset + 10 > message.Length)
        {
            return false;
        }

        var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
        var length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
        offset += 10;

        if (offset + length > message.Length)
        {
            return false;
        }

        var dataStart = offset;
        var data = message.Slice(dataStart, length);
        offset += length;

        switch (type)
        {
            case DnsRecordType.ServiceInstance:
            {
                var nameOffset = dataStart;
                record = new DnsRecord
                {
                    Name = name,
                    Type = type,
                    Target = TryReadName(message, ref nameOffset, out var target) ? target : null,
                };
                return true;
            }

            case DnsRecordType.Srv:
            {
                if (length < 7)
                {
                    return false;
                }

                var port = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
                var nameOffset = dataStart + 6;
                record = new DnsRecord
                {
                    Name = name,
                    Type = type,
                    Port = port,
                    Target = TryReadName(message, ref nameOffset, out var target) ? target : null,
                };
                return true;
            }

            case DnsRecordType.A when length == 4:
            {
                record = new DnsRecord
                {
                    Name = name,
                    Type = type,
                    Address = $"{data[0]}.{data[1]}.{data[2]}.{data[3]}",
                };
                return true;
            }

            case DnsRecordType.Aaaa when length == 16:
            {
                record = new DnsRecord
                {
                    Name = name,
                    Type = type,
                    Address = new System.Net.IPAddress(data).ToString(),
                };
                return true;
            }

            case DnsRecordType.Txt:
            {
                var strings = new List<string>();
                var position = 0;
                while (position < data.Length)
                {
                    int textLength = data[position];
                    position++;
                    if (position + textLength > data.Length)
                    {
                        break;
                    }

                    strings.Add(Encoding.UTF8.GetString(data.Slice(position, textLength)));
                    position += textLength;
                }

                record = new DnsRecord { Name = name, Type = type, Text = strings };
                return true;
            }

            default:
                record = new DnsRecord { Name = name, Type = type };
                return true;
        }
    }

    private static bool TrySkipName(ReadOnlySpan<byte> message, ref int offset) =>
        TryReadName(message, ref offset, out _);

    private static bool TryReadName(ReadOnlySpan<byte> message, ref int offset, out string name)
    {
        var builder = new StringBuilder();
        var position = offset;
        var hops = 0;
        var jumped = false;

        while (true)
        {
            if (position >= message.Length)
            {
                name = string.Empty;
                return false;
            }

            int length = message[position];

            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length || ++hops > MaximumPointerHops)
                {
                    name = string.Empty;
                    return false;
                }

                var target = ((length & 0x3F) << 8) | message[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }

                position = target;
                continue;
            }

            position++;
            if (position + length > message.Length)
            {
                name = string.Empty;
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.UTF8.GetString(message.Slice(position, length)));
            position += length;
        }

        if (!jumped)
        {
            offset = position;
        }

        name = builder.ToString();
        return true;
    }
}
