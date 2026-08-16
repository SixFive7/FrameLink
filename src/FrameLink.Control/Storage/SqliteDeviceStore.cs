using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FrameLink.Control.Storage;

/// <summary>The device table on raw <c>Microsoft.Data.Sqlite</c>.</summary>
public sealed class SqliteDeviceStore(SqliteDatabase database, TimeProvider clock) : IDeviceStore
{
    private const string SelectColumns = """
        SELECT device_id, public_key, state, display_name, hardware_serial, agent_version,
               agent_status, protocol_version, first_seen_utc, last_seen_utc,
               state_changed_utc, last_remote_addr
        FROM devices
        """;

    /// <inheritdoc/>
    public async Task<DeviceRecord?> FindAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        return await ReadOneAsync(connection, deviceId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DeviceRecord> RecordContactAsync(
        DeviceContact contact,
        int pendingCap,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);

        var now = clock.GetUtcNow();
        var timestamp = SqliteDatabase.FormatTimestamp(now);

        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        var connection = scope.Connection;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = await ReadOneAsync(connection, contact.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            // The cap is enforced only against newcomers, and by eviction rather than refusal.
            // Refusing would answer a genuine frame with silence once an attacker had filled
            // the queue, and §2.6 forbids exactly that.
            await EvictOldestPendingAsync(connection, pendingCap, cancellationToken).ConfigureAwait(false);

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO devices (
                    device_id, public_key, state, display_name, hardware_serial, agent_version,
                    agent_status, protocol_version, first_seen_utc, last_seen_utc,
                    state_changed_utc, last_remote_addr)
                VALUES ($id, $key, 'pending', NULL, $serial, $version, $status, $protocol,
                        $now, $now, $now, $address);
                """;
            insert.Parameters.AddWithValue("$id", contact.DeviceId);
            insert.Parameters.AddWithValue("$key", contact.PublicKey);
            insert.Parameters.AddWithValue("$serial", (object?)contact.HardwareSerial ?? DBNull.Value);
            insert.Parameters.AddWithValue("$version", (object?)contact.AgentVersion ?? DBNull.Value);
            insert.Parameters.AddWithValue("$status", (object?)contact.AgentStatus ?? DBNull.Value);
            insert.Parameters.AddWithValue("$protocol", contact.ProtocolVersion);
            insert.Parameters.AddWithValue("$now", timestamp);
            insert.Parameters.AddWithValue("$address", (object?)contact.RemoteAddress ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Adoption state is untouched. Only the operator moves a device between states —
            // reconnecting must never launder a blocked frame back into the list.
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE devices
                SET hardware_serial  = $serial,
                    agent_version    = $version,
                    agent_status     = $status,
                    protocol_version = $protocol,
                    last_seen_utc    = $now,
                    last_remote_addr = $address
                WHERE device_id = $id;
                """;
            update.Parameters.AddWithValue("$id", contact.DeviceId);
            update.Parameters.AddWithValue("$serial", (object?)contact.HardwareSerial ?? DBNull.Value);
            update.Parameters.AddWithValue("$version", (object?)contact.AgentVersion ?? DBNull.Value);
            update.Parameters.AddWithValue("$status", (object?)contact.AgentStatus ?? DBNull.Value);
            update.Parameters.AddWithValue("$protocol", contact.ProtocolVersion);
            update.Parameters.AddWithValue("$now", timestamp);
            update.Parameters.AddWithValue("$address", (object?)contact.RemoteAddress ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = await ReadOneAsync(connection, contact.DeviceId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return record ?? throw new InvalidOperationException(
            $"Device '{contact.DeviceId}' vanished during its own registration.");
    }

    /// <inheritdoc/>
    public async Task<bool> RecordStatusAsync(
        string deviceId,
        string? agentStatus,
        CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = "UPDATE devices SET agent_status = $status WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$status", (object?)agentStatus ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeviceRecord>> ListAsync(
        bool includeBlocked,
        CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();
        command.CommandText = includeBlocked
            ? $"{SelectColumns} ORDER BY last_seen_utc DESC;"
            : $"{SelectColumns} WHERE state <> 'blocked' ORDER BY last_seen_utc DESC;";

        var results = new List<DeviceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task<DeviceAdoption> AdoptAsync(
        string deviceId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var record = await TransitionAsync(
            deviceId,
            DeviceState.Adopted,
            displayName,
            clearOverrides: false,
            cancellationToken).ConfigureAwait(false);

        if (record is not null)
        {
            return new DeviceAdoption { Result = DeviceAdoptionResult.Adopted, Record = record };
        }

        // The UPDATE excludes blocked rows, so nothing changed for two different reasons and
        // only a read can tell them apart. Doing it after the failed write rather than before
        // it keeps the ordinary path one round trip and leaves no window where a block landing
        // between a check and a write would be adopted anyway.
        var existing = await FindAsync(deviceId, cancellationToken).ConfigureAwait(false);

        return new DeviceAdoption
        {
            Result = existing is { State: DeviceState.Blocked }
                ? DeviceAdoptionResult.Blocked
                : DeviceAdoptionResult.Unknown,
        };
    }

    /// <inheritdoc/>
    public Task<DeviceRecord?> BlockAsync(string deviceId, CancellationToken cancellationToken) =>
        TransitionAsync(deviceId, DeviceState.Blocked, displayName: null, clearOverrides: false, cancellationToken);

    /// <inheritdoc/>
    public Task<DeviceRecord?> ReturnToPendingAsync(string deviceId, CancellationToken cancellationToken) =>
        TransitionAsync(deviceId, DeviceState.Pending, displayName: null, clearOverrides: true, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> ForgetAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = "DELETE FROM devices WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc/>
    public async Task<int> CountPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM devices WHERE state = 'pending';";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task<int> ExpirePendingAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = """
            DELETE FROM devices
            WHERE state = 'pending' AND last_seen_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", SqliteDatabase.FormatTimestamp(cutoffUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeviceRecord?> TransitionAsync(
        string deviceId,
        DeviceState state,
        string? displayName,
        bool clearOverrides,
        CancellationToken cancellationToken)
    {
        var timestamp = SqliteDatabase.FormatTimestamp(clock.GetUtcNow());

        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        var connection = scope.Connection;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var update = connection.CreateCommand())
        {
            // Adoption assigns the name; returning to pending clears it, because a name is
            // something adoption granted and un-adoption has to take back.
            update.CommandText = state switch
            {
                // `state <> 'blocked'` is the whole of §3.3's "re-trusting a device is a
                // separate, deliberate press". Without it, POST /adopt on a blocked frame
                // adopted it in one step and `UnblockAsync` enforced a rule the route ignored.
                // Already-adopted rows still match, because writing the name IS renaming.
                // The CASE keeps "adopted on" meaning adopted on. Adoption doubles as the rename
                // route, so stamping the timestamp unconditionally would move it every time
                // somebody corrected a typo — and, worse, would briefly make last-seen older
                // than state-changed, which is exactly how §3.5's `Never enrolled` rung is read.
                DeviceState.Adopted => """
                    UPDATE devices
                    SET state = 'adopted',
                        display_name = $name,
                        state_changed_utc = CASE WHEN state = 'adopted' THEN state_changed_utc ELSE $now END
                    WHERE device_id = $id AND state <> 'blocked';
                    """,
                DeviceState.Pending => """
                    UPDATE devices
                    SET state = 'pending', display_name = NULL, state_changed_utc = $now
                    WHERE device_id = $id;
                    """,
                _ => """
                    UPDATE devices
                    SET state = 'blocked', state_changed_utc = $now
                    WHERE device_id = $id;
                    """,
            };
            update.Parameters.AddWithValue("$id", deviceId);
            update.Parameters.AddWithValue("$now", timestamp);
            if (state is DeviceState.Adopted)
            {
                update.Parameters.AddWithValue("$name", (object?)displayName ?? DBNull.Value);
            }

            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return null;
            }
        }

        if (clearOverrides)
        {
            await using var wipe = connection.CreateCommand();
            wipe.CommandText = "DELETE FROM device_settings WHERE device_id = $id;";
            wipe.Parameters.AddWithValue("$id", deviceId);
            await wipe.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = await ReadOneAsync(connection, deviceId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    private static async Task EvictOldestPendingAsync(
        SqliteConnection connection,
        int pendingCap,
        CancellationToken cancellationToken)
    {
        if (pendingCap <= 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM devices
            WHERE device_id IN (
                SELECT device_id FROM devices
                WHERE state = 'pending'
                ORDER BY last_seen_utc DESC
                LIMIT -1 OFFSET $keep
            );
            """;
        command.Parameters.AddWithValue("$keep", Math.Max(0, pendingCap - 1));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DeviceRecord?> ReadOneAsync(
        SqliteConnection connection,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static DeviceRecord Map(SqliteDataReader reader) => new()
    {
        DeviceId = reader.GetString(0),
        PublicKey = reader.GetString(1),
        State = ParseState(reader.GetString(2)),
        DisplayName = reader.IsDBNull(3) ? null : reader.GetString(3),
        HardwareSerial = reader.IsDBNull(4) ? null : reader.GetString(4),
        AgentVersion = reader.IsDBNull(5) ? null : reader.GetString(5),
        AgentStatus = reader.IsDBNull(6) ? null : reader.GetString(6),
        ProtocolVersion = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        FirstSeenUtc = SqliteDatabase.ParseTimestamp(reader.GetString(8)),
        LastSeenUtc = SqliteDatabase.ParseTimestamp(reader.GetString(9)),
        StateChangedUtc = SqliteDatabase.ParseTimestamp(reader.GetString(10)),
        LastRemoteAddress = reader.IsDBNull(11) ? null : reader.GetString(11),
    };

    private static DeviceState ParseState(string value) => value switch
    {
        "adopted" => DeviceState.Adopted,
        "blocked" => DeviceState.Blocked,
        _ => DeviceState.Pending,
    };
}
