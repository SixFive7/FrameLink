using System.Net.Http.Json;
using System.Text.Json;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Link;
using FrameLink.Protocol;

namespace FrameLink.Agent.Update;

/// <summary>Reads the update feed the Fleet Manager publishes.</summary>
/// <remarks>
/// §2.8: the container <i>is</i> the feed — no S3, no CDN, no bucket this project operates in
/// anyone's deployment. The install command, the installer and the binary all come from the same
/// address the agent reports to, so discovery URL and software source are one thing.
/// </remarks>
public interface IReleaseSource
{
    /// <summary>Fetches the served release, or <see langword="null"/> if the server cannot be reached.</summary>
    Task<AgentRelease?> GetReleaseAsync(Uri endpoint, string runtimeIdentifier, CancellationToken cancellationToken);

    /// <summary>Opens the binary for reading, or <see langword="null"/> if it cannot be fetched.</summary>
    Task<Stream?> DownloadAsync(Uri endpoint, AgentRelease release, CancellationToken cancellationToken);
}

/// <summary>Reads the feed over plain, versionless HTTPS (§4.2).</summary>
public sealed class HttpReleaseSource : IReleaseSource
{
    private readonly HttpClient _client;
    private readonly IAgentLog _log;

    /// <summary>Creates a source over <paramref name="client"/>.</summary>
    public HttpReleaseSource(HttpClient client, IAgentLog log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(log);

        _client = client;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<AgentRelease?> GetReleaseAsync(
        Uri endpoint,
        string runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        var url = ControlRoutes.ReleaseFor(endpoint, runtimeIdentifier);

        try
        {
            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"Update check at {url} answered {(int)response.StatusCode}.");
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync(ProtocolJson.Default.AgentRelease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            _log.Warn($"Update check at {url} failed: {exception.Message}");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<Stream?> DownloadAsync(
        Uri endpoint,
        AgentRelease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (!Uri.TryCreate(endpoint, release.Url, out var url))
        {
            _log.Warn($"Served release names an unusable URL '{release.Url}'.");
            return null;
        }

        try
        {
            var response = await _client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"Downloading {url} answered {(int)response.StatusCode}.");
                response.Dispose();
                return null;
            }

            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _log.Warn($"Downloading {url} failed: {exception.Message}");
            return null;
        }
    }
}
