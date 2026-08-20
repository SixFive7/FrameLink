using FrameLink.Control.Storage;

namespace FrameLink.Control.LiveKit;

/// <summary>What happened when a frame's call credentials were reviewed.</summary>
public enum CallIssueOutcome
{
    /// <summary>A token was minted and written.</summary>
    Issued,

    /// <summary>The frame's token is current; nothing was written.</summary>
    AlreadyCurrent,

    /// <summary>Calling is switched off, so there is nothing to issue.</summary>
    NotConfigured,

    /// <summary>The device is not adopted, and §3.3 gives a pending device nothing.</summary>
    NotAdopted,
}

/// <summary>The result of reviewing one frame's call credentials.</summary>
/// <param name="Outcome">What was done.</param>
/// <param name="Identity">The participant identity the frame is issued.</param>
/// <param name="Room">The room the token is good for.</param>
/// <param name="ExpiresUtc">When the token the frame now holds stops working.</param>
/// <param name="Reason">Why a token was minted, or why one was not.</param>
public sealed record CallIssueResult(
    CallIssueOutcome Outcome,
    string? Identity,
    string? Room,
    DateTimeOffset? ExpiresUtc,
    string Reason);

/// <summary>
/// The call credentials §3.3 issues at adoption and §3.7 rotates at will.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four settings, written as per-device overrides, and that choice is the enforcement.</b>
/// §3.3 forbids giving a pending device a token, and <c>SqliteSettingsStore</c> carries an
/// <c>EXISTS (… state = 'adopted')</c> guard on every per-device write — so a token cannot reach
/// an unadopted frame even if some future call site forgets to check, and unblocking a device
/// (which deletes its overrides) revokes its token as a side effect of the reverse operation
/// rather than as a step somebody has to remember.
/// </para>
/// <para>
/// <b>Why the token travels as a setting rather than as a new protocol message.</b> The
/// mechanism already exists and already has exactly the right properties: settings are pushed on
/// change and re-sent in full on every reconnect (<c>SettingsPublisher</c>), so correctness never
/// depends on a push landing; the frame's <c>app.config.livekit-token</c> resource already
/// consumes <c>call.token</c>, already marks it secret, already writes it root-only and already
/// reports an expired one as drift. A dedicated message would duplicate every one of those and
/// would mean touching the wire — and <c>WireEnvelope</c>, <c>Handshake*</c>, <c>AgentRelease</c>
/// and <c>DeviceIdentity</c> are frozen.
/// </para>
/// <para>
/// <b>Identity is the device id, not a name.</b> A LiveKit participant identity must be unique
/// within a room and a collision is, as <c>DeviceNameResource</c> puts it, "a fleet-wide fault
/// with no local symptom" — two frames sharing one identity are treated by the server as one
/// participant reconnecting, so each kicks the other out of the call. The public-key fingerprint
/// is unique by construction and immutable for the life of the device, which is exactly what the
/// claim needs; the operator's display name travels in the token's <c>name</c> claim instead, so
/// what the family sees on screen is still "Douwe" and what LiveKit keys on cannot collide.
/// An identity an operator has already set by hand is left alone, unless it sits in the
/// <see cref="GuestIdentityPrefix"/> namespace — see that constant for why one string is
/// reserved and what it is reserved against.
/// </para>
/// <para>
/// <b>Renewal is what actually retires the failure class.</b> Every review re-mints when the
/// token is inside its last third, when it names the wrong room or identity, or when it cannot
/// be read at all. Reviews happen at adoption, on every reconnect, after any fleet settings
/// change, and on demand — so the July-23 failure, where a token aged out with nothing watching,
/// now requires the fleet to be out of contact for eight months before it can even begin.
/// </para>
/// </remarks>
public sealed class CallProvisioning(
    LiveKitDeployment deployment,
    LiveKitOptions options,
    ISettingsStore settings,
    IDeviceStore devices,
    TimeProvider clock,
    ILogger<CallProvisioning> logger)
{
    /// <summary>Fleet setting carrying the LiveKit participant identity.</summary>
    public const string IdentityKey = "call.identity";

    /// <summary>Fleet setting carrying the room every frame with that value joins.</summary>
    public const string RoomKey = "call.room";

    /// <summary>Fleet setting carrying the signalling URL.</summary>
    public const string UrlKey = "call.livekitUrl";

    /// <summary>Fleet setting carrying the token itself.</summary>
    public const string TokenKey = "call.token";

    /// <summary>
    /// The room a frame joins when nobody has said otherwise.
    /// </summary>
    /// <remarks>
    /// <c>family</c>, which is not chosen here — it is <c>AppConfigCatalog</c>'s fallback for
    /// <c>call.room</c> on the agent side, and the two must agree exactly or the frame joins a
    /// room its token is not valid for. Repeated rather than shared because the agent and the
    /// Fleet Manager are separate programs that must not take a reference on each other; the
    /// suite asserts the two constants are equal.
    /// </remarks>
    public const string DefaultRoom = "family";

    /// <summary>
    /// The namespace every identity minted for a person lives in, and no frame's ever may.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A colon, which is what makes this a partition rather than a convention. A device's
    /// identity is either its device id — sixteen Crockford Base32 characters and three hyphens,
    /// an alphabet with no colon in it — or a string an operator typed into
    /// <see cref="IdentityKey"/>, and the second case is the one that could collide. So the
    /// prefix is reserved on both sides: nothing outside <c>guest:</c> is minted for a person,
    /// and <see cref="ReviewAsync"/> refuses to hand a frame an identity inside it however the
    /// setting got there. The two halves together are why the collision is impossible rather
    /// than merely unlikely, and why there is no runtime uniqueness check to go stale.
    /// </para>
    /// <para>
    /// The hazard being partitioned is the one <see cref="CallProvisioning"/> already names: two
    /// participants sharing one identity are treated by LiveKit as one participant reconnecting,
    /// so each kicks the other out. A person minting a token for themselves and picking, by
    /// accident, the string a frame is using would take that frame off its own call — silently,
    /// and in front of a family.
    /// </para>
    /// </remarks>
    public const string GuestIdentityPrefix = "guest:";

    /// <summary>
    /// How long a token minted for a person is good for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four hours, against a frame's year, and the gap is the whole of the argument in
    /// <see cref="LiveKitOptions.TokenLifetime"/> run backwards. A frame's token is long because
    /// renewal is free: the Fleet Manager holds the secret, knows every adopted device, and
    /// re-mints on contact. A person's token has none of that — this route writes nothing down,
    /// so there is no record for renewal machinery to find and nothing that would notice the
    /// expiry — which puts the lifetime back to being the entire policy rather than a safety
    /// margin on top of one.
    /// </para>
    /// <para>
    /// It is also the only bound on a leaked one. The single revocation this project has is
    /// rotating the API secret, which invalidates <i>every frame's</i> token as collateral and
    /// costs the whole fleet a re-mint, so nobody will reach for it to retire one person's
    /// credential. Four hours covers a sitting — a call, or an evening spent proving media
    /// flows — and is gone by the next one; a session that outruns it costs one more
    /// authenticated request, which is a far better trade than a token nobody can take back.
    /// </para>
    /// <para>
    /// Not a parameter, deliberately. A caller-supplied lifetime is a way to ask for a year, and
    /// a year on a credential nothing renews and nothing revokes is exactly the failure class
    /// §3.7 was written to retire.
    /// </para>
    /// </remarks>
    public static TimeSpan GuestLifetime => TimeSpan.FromHours(4);

    /// <summary>Whether an identity sits in the namespace reserved for people.</summary>
    public static bool IsGuestIdentity(string? identity) =>
        identity is not null && identity.StartsWith(GuestIdentityPrefix, StringComparison.Ordinal);

    /// <summary>Reviews one frame's credentials and issues a token if it needs one.</summary>
    /// <param name="deviceId">The frame.</param>
    /// <param name="force">Mint unconditionally — what the operator's rotate button does.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<CallIssueResult> ReviewAsync(
        string deviceId,
        bool force,
        CancellationToken cancellationToken)
    {
        if (!options.IsCallingConfigured)
        {
            return new CallIssueResult(
                CallIssueOutcome.NotConfigured,
                null,
                null,
                null,
                "Calling is switched off on this Fleet Manager.");
        }

        var device = await devices.FindAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null || device.State is not DeviceState.Adopted)
        {
            return new CallIssueResult(
                CallIssueOutcome.NotAdopted,
                null,
                null,
                null,
                "A device receives nothing until it is adopted.");
        }

        var credential = await deployment.CredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return new CallIssueResult(
                CallIssueOutcome.NotConfigured,
                null,
                null,
                null,
                "This Fleet Manager has no LiveKit key and secret to sign a token with.");
        }

        var effective = await settings.ResolveAsync(deviceId, cancellationToken).ConfigureAwait(false);

        // The reserved half of GuestIdentityPrefix. An operator's hand-set identity is honoured,
        // except inside the namespace people are minted into — there it is dropped for the device
        // id, which is unique by construction. Dropped rather than refused because a settings
        // write is generic (§3.4) and has no idea what it is writing: the check belongs at the one
        // place a setting becomes a participant identity, which is here. The next mint writes the
        // corrected value back to `call.identity`, so the setting heals rather than staying wrong.
        var configured = Value(effective, IdentityKey);
        if (IsGuestIdentity(configured))
        {
            logger.CallIdentityReserved(deviceId, configured!);
            configured = null;
        }

        var identity = configured ?? deviceId;
        var room = Value(effective, RoomKey) ?? DefaultRoom;
        var url = options.EffectiveUrl;
        var existing = Value(effective, TokenKey);

        var now = clock.GetUtcNow();
        var reason = WhyMint(existing, identity, room, device.DisplayName, credential.Key, now);

        // The URL is checked separately from the token, because it is not in the token. That
        // sounds like a detail and is the difference between working and not: an operator who
        // starts a Fleet Manager without FRAMELINK_LIVEKIT_PUBLIC_URL, adopts frames, then sets
        // the variable and restarts, has every frame reconnect with a token whose claims are all
        // still perfect — so a review that only asked the token whether it was happy would never
        // write the address, and the fleet would hold valid credentials for a server it had never
        // been told the location of.
        //
        // An address that has gone *away* is left alone rather than cleared. A frame holding the
        // last known URL can still call; a frame holding an empty one cannot, and "the operator
        // removed a variable" is not a reason to take a working household off the air.
        var urlDrift = url.Length > 0
            && !string.Equals(Value(effective, UrlKey), url, StringComparison.Ordinal);

        if (!force && reason is null && !urlDrift)
        {
            return new CallIssueResult(
                CallIssueOutcome.AlreadyCurrent,
                identity,
                room,
                LiveKitToken.ExpiryOf(existing),
                "The frame's token is current.");
        }

        if (urlDrift)
        {
            await settings.SetDeviceOverrideAsync(deviceId, UrlKey, url, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!force && reason is null)
        {
            // Only the address moved. Re-minting would be churn: the token names an identity, a
            // room and an expiry, and none of them changed.
            return new CallIssueResult(
                CallIssueOutcome.Issued,
                identity,
                room,
                LiveKitToken.ExpiryOf(existing),
                "The frame was told where the call server is.");
        }

        var token = LiveKitToken.Mint(
            credential,
            identity,
            room,
            device.DisplayName,
            now,
            options.TokenLifetime);

        // Identity before the token. Ordering matters only for a frame that happens to reconcile
        // between the two writes: the agent's dependency graph puts app.config.livekit-token
        // behind identity, room and URL, so a frame that sees the token has already seen
        // everything the token is bound to.
        await settings.SetDeviceOverrideAsync(deviceId, IdentityKey, identity, cancellationToken)
            .ConfigureAwait(false);

        await settings.SetDeviceOverrideAsync(deviceId, TokenKey, token, cancellationToken)
            .ConfigureAwait(false);

        var expires = now + options.TokenLifetime;
        logger.CallTokenIssued(deviceId, identity, room, expires, force ? "the operator asked for it" : reason!);

        return new CallIssueResult(
            CallIssueOutcome.Issued,
            identity,
            room,
            expires,
            force ? "The operator asked for a new token." : reason!);
    }

    /// <summary>
    /// Reviews every adopted frame, and says how many were issued a new token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run after a rotation and after any fleet-settings write. The second is deliberately
    /// unconditional rather than keyed on <c>call.room</c>: §3.4 makes settings "not a fixed list
    /// but a generic mechanism", and a route that special-cased one key would be exactly the
    /// hard-coding that rules out. Reviewing every device is a token decode and three string
    /// comparisons each, so the generic version is also the cheap one.
    /// </para>
    /// <para>
    /// A frame that is offline is reviewed all the same, because the write goes to the database
    /// rather than to the socket — it collects the new token in the settings frame it receives on
    /// its next connect, which is the same path everything else in §3.4 takes.
    /// </para>
    /// </remarks>
    public async Task<int> ReviewFleetAsync(bool force, CancellationToken cancellationToken)
    {
        if (!options.IsCallingConfigured)
        {
            return 0;
        }

        var records = await devices.ListAsync(includeBlocked: false, cancellationToken).ConfigureAwait(false);
        var issued = 0;

        foreach (var record in records.Where(record => record.State is DeviceState.Adopted))
        {
            var result = await ReviewAsync(record.DeviceId, force, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is CallIssueOutcome.Issued)
            {
                issued++;
            }
        }

        return issued;
    }

    /// <summary>
    /// Every room the fleet actually resolves to right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generated <c>livekit.yaml</c> sets <c>room.auto_create: true</c>, which is what lets a
    /// frame be the first one into <c>family</c> — and is also why a room name cannot be checked
    /// by asking the call server. Every name is a room there, the moment somebody joins it: a
    /// mistyped one mints a perfectly valid token, creates an empty room nobody else is in, and
    /// presents as a participant sitting alone with no error on any side. So the set of rooms
    /// that exist is the set this fleet <i>puts frames in</i>, and it is read from the settings
    /// the frames were actually issued.
    /// </para>
    /// <para>
    /// The fleet default is always a member, even with no adopted frame to hold it. That is the
    /// case where somebody is proving the call server works before there is anything to call, and
    /// refusing it would make the room check hardest exactly when the fleet is emptiest.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlySet<string>> RoomsAsync(CancellationToken cancellationToken)
    {
        var rooms = new HashSet<string>(StringComparer.Ordinal)
        {
            await FleetRoomAsync(cancellationToken).ConfigureAwait(false),
        };

        // Adopted only. A pending or blocked device holds no settings at all — SqliteSettingsStore
        // resolves it to nothing — so it is in no room, and counting one would invent a room from
        // a device that has never been given a token.
        var records = await devices.ListAsync(includeBlocked: false, cancellationToken).ConfigureAwait(false);

        foreach (var record in records.Where(record => record.State is DeviceState.Adopted))
        {
            var effective = await settings.ResolveAsync(record.DeviceId, cancellationToken).ConfigureAwait(false);
            rooms.Add(Value(effective, RoomKey) ?? DefaultRoom);
        }

        return rooms;
    }

    /// <summary>
    /// The room a frame with no override of its own is put in.
    /// </summary>
    /// <remarks>
    /// The fleet default if there is one, and <see cref="DefaultRoom"/> otherwise — the same two
    /// steps <see cref="ReviewAsync"/> takes, which is the point of it being one method. A caller
    /// that reached for <see cref="DefaultRoom"/> directly would be right on every fleet that has
    /// never set <c>call.room</c> and wrong on every fleet that has, minting into <c>family</c>
    /// while every frame sat somewhere else.
    /// </remarks>
    public async Task<string> FleetRoomAsync(CancellationToken cancellationToken)
    {
        var fleet = await settings.GetFleetDefaultsAsync(cancellationToken).ConfigureAwait(false);

        return fleet.TryGetValue(RoomKey, out var room) && room.Length > 0 ? room : DefaultRoom;
    }

    /// <summary>
    /// Why this frame needs a new token, or null when it does not.
    /// </summary>
    /// <remarks>
    /// Six reasons. Five are ways a frame ends up holding a credential that will be refused, and
    /// every one of them is silent until the moment somebody presses the call button — which is
    /// why they are checked here rather than left for the frame to discover in front of a family.
    /// The sixth, a changed display name, is refused by nothing and is re-minted anyway: the name
    /// is what the rest of the household sees on screen, and it costs one signature for one frame.
    /// </remarks>
    public string? WhyMint(
        string? existing,
        string identity,
        string room,
        string? displayName,
        string issuer,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(existing))
        {
            return "this frame has never been issued a call token";
        }

        var facts = LiveKitToken.Inspect(existing);

        if (facts is null)
        {
            return "the token this frame holds cannot be read";
        }

        if (!string.Equals(facts.Issuer, issuer, StringComparison.Ordinal))
        {
            // The API secret was rotated, or an operator moved the fleet to a different LiveKit.
            // Either way the signature will not verify any more.
            return "the token was signed by a key this Fleet Manager no longer uses";
        }

        if (!string.Equals(facts.Identity, identity, StringComparison.Ordinal))
        {
            return "the token names a different participant identity";
        }

        if (!string.Equals(facts.Room, room, StringComparison.Ordinal))
        {
            return "the token is for a different room";
        }

        if (!string.Equals(facts.Name, LiveKitToken.NormaliseName(displayName), StringComparison.Ordinal))
        {
            return "this frame has been renamed";
        }

        if (LiveKitToken.NeedsRenewal(existing, now, options.RenewalThreshold))
        {
            return "the token is inside the last third of its life";
        }

        return null;
    }

    private static string? Value(ResolvedSettings resolved, string key) =>
        resolved.Values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
}
