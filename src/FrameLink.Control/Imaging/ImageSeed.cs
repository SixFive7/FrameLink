using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace FrameLink.Control.Imaging;

/// <summary>
/// Everything a generated image is allowed to carry: where to find this Fleet Manager, and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decision 17 — "generic image, no secrets" — is enforced by this type's shape, not by
/// anyone remembering it.</b> There are two fields and both are URLs. There is nowhere to put a
/// device token, an adoption credential, a keypair or a LiveKit secret, so the tempting
/// "helpfully pre-seed the identity so the frame arrives already adopted" change cannot be made
/// by adding a line somewhere — it has to widen this record, which is a review a person
/// performs. Identity is the keypair the agent generates on its first boot (§3.3), and adoption
/// is a human pressing <b>Adopt</b>. An image that arrived pre-adopted would destroy both.
/// </para>
/// <para>
/// One image therefore serves an entire fleet, which is also what keeps the storage budget
/// (§3.1) to a single artifact rather than one per frame.
/// </para>
/// <para>
/// <b>Wi-Fi is deliberately absent.</b> Decision 32's wording included it, and it is not here,
/// for two measured reasons rather than for scope. The vendor's supported seeding channel is
/// <c>/boot/firmware/custom.toml</c>, which also governs first-boot user creation, hostname and
/// SSH — writing a partial one changes first-boot behaviour in ways nothing short of flashing a
/// card can confirm. And on Bookworm and later the WLAN interface stays rfkill-soft-blocked
/// until a wireless regulatory country is set, so a NetworkManager keyfile on its own is a seed
/// that looks correct and never associates. Adding a <c>[wlan]</c> section to a
/// <c>custom.toml</c> written by <see cref="ImagePlan"/> is the shape that will work; it needs a
/// card and a boot to prove, which is why it is named as an open item rather than shipped
/// unproven.
/// </para>
/// </remarks>
public sealed record ImageSeed
{
    /// <summary>
    /// The <c>key=value</c> key the agent reads the public control URL from.
    /// </summary>
    /// <remarks>
    /// <b>One name, two programs.</b> The reader is
    /// <c>FrameLink.Agent.Discovery.BootFileEndpointSource.ControlUrlKey</c>, and the Fleet
    /// Manager cannot reference the agent — they are two separately published binaries that meet
    /// only at <c>FrameLink.Protocol</c>, and this is a file format rather than a wire type, so
    /// it does not belong there either. The two constants are held equal by test, exactly as the
    /// two copies of <c>fl-agent.service</c> are. Edit one, edit both.
    /// </remarks>
    public const string ControlUrlKey = "control-url";

    /// <summary>The key the agent reads the optional LAN address from. See <see cref="ControlUrlKey"/>.</summary>
    public const string LanUrlKey = "control-lan-url";

    /// <summary>Name of the seed file on the boot partition (§4.3's second discovery candidate).</summary>
    public const string BootFileName = "framelink.conf";

    private ImageSeed(Uri controlUrl, Uri? lanUrl)
    {
        ControlUrl = controlUrl;
        LanUrl = lanUrl;
    }

    /// <summary>The public URL every frame built from this image will dial (§4.3).</summary>
    public Uri ControlUrl { get; }

    /// <summary>An optional LAN address tried after the public one, or null.</summary>
    public Uri? LanUrl { get; }

    /// <summary>
    /// Validates an operator's request, or explains what is wrong with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three refusals, and each one is a way something that must not reach a card reaches a card.
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>User information in the URL.</b> <c>https://token@framelink.example/</c> is a perfectly
    /// well-formed absolute URI and it is a credential. It is the one place a secret can ride
    /// into a type that has no field for a secret, so the type refuses it.
    /// </description></item>
    /// <item><description>
    /// <b>A query or a fragment.</b> Same argument one step along: <c>?adopt=…</c> is where an
    /// enrollment token would be smuggled. The agent reads the URL as an endpoint and never as a
    /// carrier of parameters, so nothing is lost by refusing them.
    /// </description></item>
    /// <item><description>
    /// <b>Control characters.</b> The seed is rendered as a <c>key=value</c> file, so a newline
    /// inside a value does not corrupt the file — it <i>appends a line to it</i>, which is an
    /// injection of arbitrary further keys into a file the agent trusts on first boot.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="controlUrl">The public URL, as the operator typed it.</param>
    /// <param name="lanUrl">The optional LAN address, or null/blank.</param>
    /// <param name="seed">The validated seed, when this returns true.</param>
    /// <param name="problem">A sentence fit to show an operator, when this returns false.</param>
    public static bool TryCreate(
        string? controlUrl,
        string? lanUrl,
        [NotNullWhen(true)] out ImageSeed? seed,
        [NotNullWhen(false)] out string? problem)
    {
        seed = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(controlUrl))
        {
            problem = "A control URL is required — it is the whole point of the image.";
            return false;
        }

        if (!TryEndpoint(controlUrl, "control URL", out var control, out problem))
        {
            return false;
        }

        // The LAN address is genuinely optional: §4.3 keeps it second in the endpoint list so a
        // frame built on a bench survives being shipped, and a fleet that never leaves one house
        // has no use for it.
        Uri? lan = null;
        if (!string.IsNullOrWhiteSpace(lanUrl) && !TryEndpoint(lanUrl, "LAN address", out lan, out problem))
        {
            return false;
        }

        seed = new ImageSeed(control, lan);
        return true;
    }

    /// <summary>
    /// Renders the boot-partition seed file exactly as the agent's boot-file source parses it.
    /// </summary>
    /// <remarks>
    /// Comments are <c>#</c>-prefixed and blank lines are ignored by that parser, so the header
    /// costs nothing and answers the question somebody holding a card in a reader will actually
    /// have: what wrote this, and what is it for. LF line endings unconditionally — the file is
    /// read on Linux, and this is written by a server that may well be running on Windows in
    /// development.
    /// </remarks>
    public string RenderBootFile()
    {
        var text = new StringBuilder();
        text.Append("# FrameLink — written by the Fleet Manager that generated this image.\n");
        text.Append("# The agent reads this on first boot to find its Fleet Manager (§4.3).\n");
        text.Append("# It carries no credential: identity is the keypair the agent generates on\n");
        text.Append("# first boot, and adoption is a person pressing Adopt (§3.3, decision 17).\n");
        text.Append(ControlUrlKey).Append('=').Append(ControlUrl.AbsoluteUri).Append('\n');

        if (LanUrl is not null)
        {
            text.Append(LanUrlKey).Append('=').Append(LanUrl.AbsoluteUri).Append('\n');
        }

        return text.ToString();
    }

    private static bool TryEndpoint(
        string value,
        string label,
        [NotNullWhen(true)] out Uri? endpoint,
        [NotNullWhen(false)] out string? problem)
    {
        endpoint = null;
        problem = null;

        var trimmed = value.Trim();

        if (trimmed.Any(char.IsControl))
        {
            problem = $"The {label} contains a control character, which would inject extra lines "
                + $"into the seed file the agent trusts on first boot.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            problem = $"The {label} is not an absolute URL.";
            return false;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            problem = $"The {label} must be http or https, not '{parsed.Scheme}'.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            problem = $"The {label} carries user information. A generated image is generic and "
                + $"must not carry a credential (decision 17).";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            problem = $"The {label} carries a query or fragment. The agent reads it as an endpoint, "
                + $"and that is where an adoption token would be smuggled onto a card (decision 17).";
            return false;
        }

        endpoint = parsed;
        return true;
    }
}
