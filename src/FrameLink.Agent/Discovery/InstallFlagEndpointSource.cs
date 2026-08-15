namespace FrameLink.Agent.Discovery;

/// <summary>
/// §4.3's first candidate: the URL the install command was given.
/// </summary>
/// <remarks>
/// It leads the search because it is the most authoritative thing available — §2.8 notes that
/// the install command, the installer and the binary all come from the same address the agent
/// will report to, so an operator who ran the Fleet Manager's own install line has already said
/// exactly where this frame belongs.
/// </remarks>
public sealed class InstallFlagEndpointSource : IEndpointSource
{
    /// <summary>Command-line flag carrying the public control URL.</summary>
    public const string ControlUrlFlag = "--control-url";

    /// <summary>Command-line flag carrying the optional LAN address.</summary>
    public const string LanUrlFlag = "--lan-url";

    /// <summary>Environment variable equivalent of <see cref="ControlUrlFlag"/>.</summary>
    public const string ControlUrlVariable = "FL_CONTROL_URL";

    /// <summary>Environment variable equivalent of <see cref="LanUrlFlag"/>.</summary>
    public const string LanUrlVariable = "FL_LAN_URL";

    private readonly IReadOnlyList<Uri> _endpoints;

    /// <summary>Reads the flags out of <paramref name="arguments"/> and the environment.</summary>
    public InstallFlagEndpointSource(
        IReadOnlyList<string> arguments,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var lookup = environment ?? Environment.GetEnvironmentVariable;
        var control = ReadFlag(arguments, ControlUrlFlag) ?? lookup(ControlUrlVariable);
        var lan = ReadFlag(arguments, LanUrlFlag) ?? lookup(LanUrlVariable);

        _endpoints = EndpointParsing.Parse(control, lan);
    }

    /// <inheritdoc/>
    public string Name => "install-flag";

    /// <inheritdoc/>
    public Task<IReadOnlyList<Uri>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_endpoints);
    }

    private static string? ReadFlag(IReadOnlyList<string> arguments, string flag)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, flag, StringComparison.Ordinal))
            {
                return index + 1 < arguments.Count ? arguments[index + 1] : null;
            }

            if (argument.StartsWith(flag + "=", StringComparison.Ordinal))
            {
                return argument[(flag.Length + 1)..];
            }
        }

        return null;
    }
}

/// <summary>Turns operator-supplied strings into the ordered endpoint list of §4.3.</summary>
public static class EndpointParsing
{
    /// <summary>
    /// Builds the list, public URL first, dropping anything unparseable or duplicated.
    /// </summary>
    /// <remarks>
    /// Unparseable input is dropped rather than throwing: a typo in a boot-partition file must
    /// leave the frame narrating "no Fleet Manager configured" on its own screen, not crash the
    /// agent into a restart loop before anything can be read.
    /// </remarks>
    public static IReadOnlyList<Uri> Parse(params string?[] candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var results = new List<Uri>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            {
                continue;
            }

            if (!results.Contains(uri))
            {
                results.Add(uri);
            }
        }

        return results;
    }
}
