using System.Net;
using System.Security.Cryptography;
using FrameLink.Control;
using FrameLink.Control.Updates;
using FrameLink.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrameLink.Tests;

/// <summary>
/// The versionless update feed of §2.8 and §4.2.
/// </summary>
/// <remarks>
/// This is §1.2 principle 4 made observable: the Fleet Manager <i>is</i> the feed, so agent
/// version is a function of server version and the wire protocol always matches.
/// </remarks>
public sealed class ControlReleaseFeedTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_metadata_describes_the_bytes_on_disk()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.WriteAgentBinary("linux-arm64", "the agent binary");
        var catalog = NewCatalog(workspace);

        var release = catalog.TryGet("linux-arm64");

        Assert.NotNull(release);
        Assert.Equal("linux-arm64", release.RuntimeIdentifier);
        Assert.Equal(new FileInfo(path).Length, release.SizeBytes);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
            release.Sha256);
        Assert.Equal("/agent/binary/linux-arm64", release.Url);
    }

    [Fact]
    public void A_declared_version_wins_over_the_content_hash()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteAgentBinary("linux-arm64", "the agent binary", version: "0.3.1+a1b2c3d");

        Assert.Equal("0.3.1+a1b2c3d", NewCatalog(workspace).TryGet("linux-arm64")!.Version);
    }

    [Fact]
    public void Without_a_declared_version_the_served_version_follows_the_served_bytes()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteAgentBinary("linux-arm64", "build one");
        var catalog = NewCatalog(workspace);
        var first = catalog.TryGet("linux-arm64")!.Version;

        // Deliberately a different length as well as different content, so the cache's
        // (length, write time) validation is guaranteed to notice on any filesystem.
        workspace.WriteAgentBinary("linux-arm64", "build two, longer than the first");
        var second = catalog.TryGet("linux-arm64")!.Version;

        // §2.8: the agent matches the served version rather than taking the greater of the
        // two. Deriving it from content means new bytes always are a new version, which is
        // the only property that rule needs.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Nothing_is_served_for_a_runtime_identifier_with_no_build()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteAgentBinary("linux-arm64", "the agent binary");

        Assert.Null(NewCatalog(workspace).TryGet("linux-x64"));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..")]
    [InlineData("linux/arm64")]
    [InlineData("LINUX-ARM64")]
    [InlineData("")]
    public void A_runtime_identifier_can_never_escape_the_release_directory(string runtimeIdentifier)
    {
        using var workspace = new TempWorkspace();
        var catalog = NewCatalog(workspace);

        // The value arrives in a URL on an unauthenticated route and is concatenated into a
        // filesystem path, so this is the test that keeps it a 404 rather than a disclosure.
        Assert.Null(catalog.TryGet(runtimeIdentifier));
        Assert.Null(catalog.ResolveBinaryPath(runtimeIdentifier));
    }

    [Fact]
    public void A_flat_layout_is_served_as_readily_as_a_nested_one()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(Path.Combine(workspace.ReleaseDirectory, "fl-agent-linux-x64"), "flat build");

        Assert.NotNull(NewCatalog(workspace).TryGet("linux-x64"));
    }

    [Fact]
    public async Task The_feed_answers_without_any_authentication()
    {
        await using var server = await ControlServer.StartAsync("a-long-operator-passphrase-for-the-fleet");
        server.Workspace.WriteAgentBinary("linux-arm64", "the agent binary", version: "0.3.1+a1b2c3d");

        var metadata = await server.Client.GetAsync("/agent/release/linux-arm64", Token);
        var release = await metadata.ReadAsync(ProtocolJson.Default.AgentRelease);
        var binary = await server.Client.GetAsync(release.Url, Token);
        var bytes = await binary.Content.ReadAsByteArrayAsync(Token);

        // The one route an agent too old to speak the protocol must still be able to use to
        // repair itself, so it cannot depend on adoption, on a session, or on a version.
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal("0.3.1+a1b2c3d", release.Version);
        Assert.Equal(HttpStatusCode.OK, binary.StatusCode);
        Assert.Equal("the agent binary", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), release.Sha256);
    }

    [Fact]
    public async Task An_unknown_runtime_identifier_is_a_readable_404()
    {
        await using var server = await ControlServer.StartAsync("a-long-operator-passphrase-for-the-fleet");

        var response = await server.Client.GetAsync("/agent/release/solaris-sparc", Token);
        var error = await response.ReadAsync(ControlJson.Default.ApiError);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-release", error.Error);
    }

    [Fact]
    public async Task An_unconfigured_server_still_serves_the_update_feed()
    {
        await using var server = await ControlServer.StartAsync(operatorPassword: null);
        server.Workspace.WriteAgentBinary("linux-arm64", "the agent binary");

        var response = await server.Client.GetAsync("/agent/release/linux-arm64", Token);

        // Updates are capability only (§2.8) and the feed is out of band. An operator who has
        // not set a password yet must not also have a fleet that cannot update itself.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static AgentReleaseCatalog NewCatalog(TempWorkspace workspace) =>
        new(
            new ControlOptions { ReleaseDirectory = workspace.ReleaseDirectory },
            NullLogger<AgentReleaseCatalog>.Instance);
}
