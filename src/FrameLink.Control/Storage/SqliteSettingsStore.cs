using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FrameLink.Control.Storage;

/// <summary>
/// The fleet-default-plus-override mechanism of §3.4 on raw <c>Microsoft.Data.Sqlite</c>.
/// </summary>
/// <remarks>
/// Every statement that touches a per-device row carries the same
/// <c>EXISTS (… state = 'adopted')</c> guard. Repeating the guard in SQL rather than
/// checking once in a service method is deliberate: it makes "a pending device receives
/// nothing" a property of the data layer, so a future call site cannot forget it, and it
/// stays true even if two requests race adoption against a settings write.
/// </remarks>
public sealed class SqliteSettingsStore(SqliteDatabase database, TimeProvider clock) : ISettingsStore
{
    private const string AdoptedGuard =
        "EXISTS (SELECT 1 FROM devices d WHERE d.device_id = $id AND d.state = 'adopted')";

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> GetFleetDefaultsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM fleet_settings ORDER BY key;";
        return await ReadPairsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> GetDeviceOverridesAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM device_settings WHERE device_id = $id ORDER BY key;";
        command.Parameters.AddWithValue("$id", deviceId);
        return await ReadPairsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ResolvedSettings> ResolveAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();

        // Fleet defaults overlaid by overrides, plus overrides for keys that have no fleet
        // default at all. The guard sits in both halves, so a pending, blocked or unknown
        // device resolves to nothing rather than to the fleet defaults.
        command.CommandText = $"""
            SELECT f.key, COALESCE(o.value, f.value)
            FROM fleet_settings f
            LEFT JOIN device_settings o ON o.device_id = $id AND o.key = f.key
            WHERE {AdoptedGuard}
            UNION
            SELECT o.key, o.value
            FROM device_settings o
            WHERE o.device_id = $id AND {AdoptedGuard}
            ORDER BY 1;
            """;
        command.Parameters.AddWithValue("$id", deviceId);

        var values = await ReadPairsAsync(command, cancellationToken).ConfigureAwait(false);
        var revision = await ReadRevisionAsync(connection, cancellationToken).ConfigureAwait(false);

        return new ResolvedSettings
        {
            DeviceId = deviceId,
            Revision = revision,
            Values = values,
        };
    }

    /// <inheritdoc/>
    public async Task SetFleetDefaultAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await scope.Connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var command = scope.Connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO fleet_settings (key, value, updated_utc)
                VALUES ($key, $value, $now)
                ON CONFLICT (key) DO UPDATE SET value = $value, updated_utc = $now;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(clock.GetUtcNow()));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await BumpRevisionAsync(scope.Connection, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveFleetDefaultAsync(string key, CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await scope.Connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        int affected;
        await using (var command = scope.Connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM fleet_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await BumpRevisionAsync(scope.Connection, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> SetDeviceOverrideAsync(
        string deviceId,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await scope.Connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        int affected;
        await using (var command = scope.Connection.CreateCommand())
        {
            // The SELECT … WHERE EXISTS form is what makes the write conditional: with a
            // non-adopted device the inner query yields no row and the INSERT is a no-op,
            // so nothing is ever allocated against a pending record.
            command.CommandText = $"""
                INSERT INTO device_settings (device_id, key, value, updated_utc)
                SELECT $id, $key, $value, $now WHERE {AdoptedGuard}
                ON CONFLICT (device_id, key) DO UPDATE SET value = $value, updated_utc = $now;
                """;
            command.Parameters.AddWithValue("$id", deviceId);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(clock.GetUtcNow()));
            affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (affected > 0)
        {
            await BumpRevisionAsync(scope.Connection, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveDeviceOverrideAsync(
        string deviceId,
        string key,
        CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await scope.Connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        int affected;
        await using (var command = scope.Connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM device_settings WHERE device_id = $id AND key = $key;";
            command.Parameters.AddWithValue("$id", deviceId);
            command.Parameters.AddWithValue("$key", key);
            affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (affected > 0)
        {
            await BumpRevisionAsync(scope.Connection, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<long> GetRevisionAsync(CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        return await ReadRevisionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadRevisionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM control_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", SqliteDatabase.RevisionKey);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string text && long.TryParse(text, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static async Task BumpRevisionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE control_meta
            SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$key", SqliteDatabase.RevisionKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadPairsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }
}
