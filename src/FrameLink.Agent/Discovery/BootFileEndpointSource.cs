using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Discovery;

/// <summary>
/// §4.3's second candidate: a plain <c>key=value</c> file on the boot partition.
/// </summary>
/// <remarks>
/// The boot partition is FAT and is the one filesystem on the card that can be edited from any
/// laptop without a Linux machine. That is what makes this the right second candidate: it lets
/// a frame be pointed at a Fleet Manager by someone who has a card reader and nothing else, and
/// it is the mechanism v3's pre-seeded SD image generation (§8) will write into.
/// </remarks>
public sealed class BootFileEndpointSource : IEndpointSource
{
    /// <summary>Key holding the public control URL.</summary>
    public const string ControlUrlKey = "control-url";

    /// <summary>Key holding the optional LAN address.</summary>
    public const string LanUrlKey = "control-lan-url";

    /// <summary>Where Raspberry Pi OS mounts the boot partition on Trixie, then the legacy path.</summary>
    public static IReadOnlyList<string> DefaultPaths { get; } =
    [
        "/boot/firmware/framelink.conf",
        "/boot/framelink.conf",
    ];

    private readonly ITextFileReader _reader;
    private readonly IReadOnlyList<string> _paths;

    /// <summary>Creates a source reading through <paramref name="reader"/>.</summary>
    public BootFileEndpointSource(ITextFileReader reader, IReadOnlyList<string>? paths = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
        _paths = paths ?? DefaultPaths;
    }

    /// <inheritdoc/>
    public string Name => "boot-file";

    /// <inheritdoc/>
    public Task<IReadOnlyList<Uri>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var path in _paths)
        {
            var content = _reader.ReadAllTextOrNull(path);
            if (content is null)
            {
                continue;
            }

            var settings = Parse(content);
            settings.TryGetValue(ControlUrlKey, out var control);
            settings.TryGetValue(LanUrlKey, out var lan);

            var endpoints = EndpointParsing.Parse(control, lan);
            if (endpoints.Count > 0)
            {
                return Task.FromResult(endpoints);
            }
        }

        return Task.FromResult<IReadOnlyList<Uri>>([]);
    }

    /// <summary>Parses the <c>key=value</c> body, ignoring blanks and <c>#</c> comments.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            settings[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return settings;
    }
}
