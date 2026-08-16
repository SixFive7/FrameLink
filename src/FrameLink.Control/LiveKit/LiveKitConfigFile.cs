using System.Globalization;
using System.Text;
using FrameLink.Control.Storage;

namespace FrameLink.Control.LiveKit;

/// <summary>
/// The <c>livekit.yaml</c> the Fleet Manager generates and owns (§3.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Generated on every start, never edited by hand.</b> Guide 7 had an operator write this
/// file with a guarded <c>printf</c> whose whole job was to avoid minting a second secret over
/// the first; here the secret lives in the database and the file is a rendering of it, so
/// regenerating is free and the guard has nothing to protect. The write is still conditional —
/// an identical file is left alone — because a rewrite is a change to a file inside the
/// operator's volume and §0.1 asks for commands that are safe to run twice, not merely
/// harmless.
/// </para>
/// <para>
/// <b>Every key below is a real key, checked rather than assumed.</b> LiveKit parses its
/// configuration with unknown fields treated as errors — a file carrying one extra key is
/// refused with <c>field … not found in type config.Config</c> and the server does not start.
/// This exact document was fed to <c>livekit-server 1.13.5</c>, which accepted it and answered
/// <c>ports</c> with precisely the ports it asks for: <c>7880 - HTTP service</c>,
/// <c>7881 - ICE/TCP</c>, <c>50000-50059 - ICE/UDP range</c>. A typo here is therefore a dead
/// call server rather than a silently ignored line, which is why it is a rendered constant
/// rather than an operator-supplied template. <b>Re-checked with <c>node_ip</c> in it</b>, against
/// the same pinned binary and both ways round: the document below answers <c>ports</c> with that
/// same table, and the one-letter misspelling <c>node_ipp</c> is refused with
/// <c>line 9: field node_ipp not found in type config.RTCConfig</c> — which is what makes the
/// acceptance evidence rather than a formality.
/// </para>
/// <para>
/// <b>The exposure §3.7 says splits in two, splits here.</b> <c>port</c> carries signalling and
/// is the one that can ride Traefik as a WebSocket over TLS; <c>rtc.tcp_port</c> and the UDP
/// range carry WebRTC media, which cannot, so they are published directly by the stack.
/// TURN/TLS for frames in other households is deferred within v2, and there is deliberately
/// nothing here that half-implements it.
/// </para>
/// <para>
/// <b><c>use_external_ip: false</c> and <c>node_ip</c> are one decision, and the order between
/// them is the version's own.</b> External-IP discovery asks a STUN server on the internet what
/// this host's public address is, which on a home LAN is both the wrong answer — frames are on
/// the same network and need the LAN address — and a dependency on a third party for a call that
/// never leaves the house. So it stays off, and §3.7's LAN setting is unchanged. What it does
/// <i>not</i> do is say where media should come from, and a container publishing its ports
/// one-to-one onto a LAN host is on an address no frame can route to. <c>node_ip</c> is the key
/// that says it, and in <c>livekit-server 1.13.5</c> the two interact in exactly one direction:
/// the pinned <c>rtcconfig</c> re-determines the node address whenever external-IP discovery is
/// on, so a configured <c>node_ip</c> only survives while <c>use_external_ip</c> is false — which
/// is why turning the second on to "also" advertise an address would silently discard the first.
/// With it off and the address set, the server rewrites its host ICE candidates to that address,
/// and stops adding public STUN servers to the ICE configuration it would otherwise reach for.
/// The value comes from <see cref="LiveKitOptions.MediaAddress"/>, which is the address frames
/// are already told to dial; when the public URL names a host rather than an address the line is
/// absent entirely and the server advertises what it is locally on, as before.
/// </para>
/// <para>
/// <b>No telemetry section, which is how "no telemetry unless configured" is kept true.</b>
/// LiveKit reports nothing outward without an explicitly configured endpoint, and the way to
/// guarantee that is a generated file that has no such section for anyone to fill in.
/// </para>
/// </remarks>
public static class LiveKitConfigFile
{
    /// <summary>How long an empty room lingers before LiveKit tears it down, in seconds.</summary>
    /// <remarks>
    /// Five minutes. Calling is one-button and auto-answer (§3.4), so a room is created by
    /// whoever calls first and has to survive the seconds before anyone else arrives; five
    /// minutes also means a call that drops for a network blip rejoins the room it left rather
    /// than a new one with the same name.
    /// </remarks>
    public const int EmptyRoomTimeoutSeconds = 300;

    /// <summary>File mode the configuration is written with, on the platforms that have one.</summary>
    /// <remarks>
    /// <c>0600</c>. The file holds the signing secret for every call token in the fleet, which is
    /// the same class of value §2.9 keeps in root-only files on a frame. It is written into the
    /// operator's data volume beside the database, so the mode is what stops another process on
    /// a shared host reading it.
    /// </remarks>
    public const UnixFileMode SecretFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Renders the configuration for one credential and one set of options.</summary>
    public static string Render(LiveKitOptions options, LiveKitCredential credential)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);

        var text = new StringBuilder();

        text.Append("# Generated by the FrameLink Fleet Manager. Edits are overwritten on restart:\n");
        text.Append("# the API key and secret live in the fleet database and this file is a rendering\n");
        text.Append("# of them, so the way to change a value here is to change it there.\n");
        text.Append(CultureInfo.InvariantCulture, $"port: {options.SignalPort}\n");
        text.Append("bind_addresses:\n");
        text.Append("  - 0.0.0.0\n");
        text.Append("rtc:\n");
        text.Append(CultureInfo.InvariantCulture, $"  tcp_port: {options.TcpMediaPort}\n");
        text.Append(CultureInfo.InvariantCulture, $"  port_range_start: {options.UdpPortStart}\n");
        text.Append(CultureInfo.InvariantCulture, $"  port_range_end: {options.UdpPortEnd}\n");
        text.Append("  use_external_ip: false\n");

        if (options.MediaAddress is { } media)
        {
            text.Append(CultureInfo.InvariantCulture, $"  node_ip: {media}\n");
        }

        text.Append("keys:\n");
        text.Append(CultureInfo.InvariantCulture, $"  {credential.Key}: {credential.Secret}\n");
        text.Append("room:\n");
        text.Append("  auto_create: true\n");
        text.Append(CultureInfo.InvariantCulture, $"  empty_timeout: {EmptyRoomTimeoutSeconds}\n");
        text.Append("logging:\n");
        text.Append("  level: info\n");
        text.Append("  json: false\n");

        return text.ToString();
    }

    /// <summary>
    /// Writes the configuration if it is not already exactly this, and says whether it did.
    /// </summary>
    /// <returns>True when the file on disk changed, which is the caller's cue to restart LiveKit.</returns>
    public static bool Write(LiveKitOptions options, LiveKitCredential credential)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rendered = Render(options, credential);

        if (File.Exists(options.ConfigPath))
        {
            string existing;
            try
            {
                existing = File.ReadAllText(options.ConfigPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                existing = string.Empty;
            }

            if (string.Equals(existing, rendered, StringComparison.Ordinal))
            {
                // Still assert the mode. A file with the right bytes and the wrong permissions is
                // the secret readable by anything on the host, and it is invisible from the
                // content alone.
                Restrict(options.ConfigPath);
                return false;
            }
        }

        Directory.CreateDirectory(options.Directory);

        // Created restricted, then written. Creating it world-readable and tightening afterwards
        // leaves a window in which the secret is on disk and readable, which is exactly the kind
        // of window that only ever matters once.
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = SecretFileMode;
        }

        using (var file = new FileStream(options.ConfigPath, streamOptions))
        {
            file.Write(Encoding.UTF8.GetBytes(rendered));
            file.Flush(flushToDisk: true);
        }

        Restrict(options.ConfigPath);
        return true;
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (File.GetUnixFileMode(path) != SecretFileMode)
            {
                File.SetUnixFileMode(path, SecretFileMode);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Better to run with a readable config than to refuse to serve calls over a mode bit;
            // the value it protects is regenerable and rotation is one request away.
        }
    }
}
