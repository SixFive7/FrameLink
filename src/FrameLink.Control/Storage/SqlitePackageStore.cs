using FrameLink.Protocol;
using Microsoft.Data.Sqlite;

namespace FrameLink.Control.Storage;

/// <summary>
/// Package inventories on raw <c>Microsoft.Data.Sqlite</c>, deduplicated by content.
/// </summary>
/// <remarks>
/// <para>
/// <b>The set is stored as its canonical text, not as JSON and not as rows.</b> Rows were the
/// obvious shape and are the wrong one: ~930 of them per device per report, exploded and
/// re-collapsed on every read, to represent something no query ever filters on. The canonical
/// text is ~23 kB against ~30 kB of JSON, it is exactly the bytes the content hash describes —
/// so the key can be re-derived from what was stored and checked — and reading it back is a
/// split on two characters.
/// </para>
/// <para>
/// <b>The hash is recomputed here and the agent's claim is not believed.</b> It is a primary key
/// shared across every device in the fleet, so a wrong one would file two different sets under a
/// single row and make two frames appear identical for as long as the row survived. Recomputing
/// costs one SHA-256 over 23 kB per report — a handful of times a month per frame — and removes
/// an entire class of question about what a peer could do to the store.
/// </para>
/// </remarks>
public sealed class SqlitePackageStore(SqliteDatabase database, ILogger<SqlitePackageStore> logger)
    : IPackageStore
{
    /// <inheritdoc/>
    public async Task RecordInventoryAsync(PackageInventory inventory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var body = PackageInventory.Canonicalise(inventory.Packages);
        var hash = PackageInventory.HashOf(inventory.Packages);

        if (!string.Equals(hash, inventory.ContentHash, StringComparison.Ordinal))
        {
            // Not a refusal: the set itself is still perfectly usable and the server's own hash is
            // the one everything downstream uses. What it means is that the two ends disagree
            // about the canonical rendering, which is a bug worth a line in the log rather than a
            // reason to throw a frame's inventory away.
            logger.PackageHashMismatch(inventory.DeviceId, inventory.ContentHash, hash);
        }

        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await scope.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var set = scope.Connection.CreateCommand())
        {
            // The blob first, because both rows below reference it. OR IGNORE is the whole
            // deduplication: a set the fleet has already seen writes nothing at all.
            set.Transaction = (SqliteTransaction)transaction;
            set.CommandText = """
                INSERT OR IGNORE INTO package_sets (content_hash, package_count, body)
                VALUES ($hash, $count, $body);
                """;
            set.Parameters.AddWithValue("$hash", hash);
            set.Parameters.AddWithValue("$count", inventory.Packages.Count);
            set.Parameters.AddWithValue("$body", body);
            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var current = scope.Connection.CreateCommand())
        {
            current.Transaction = (SqliteTransaction)transaction;
            current.CommandText = """
                INSERT INTO device_packages (device_id, sequence, observed_utc, content_hash, observed_count)
                VALUES ($id, $sequence, $observed, $hash, $observedCount)
                ON CONFLICT (device_id) DO UPDATE SET
                    sequence       = excluded.sequence,
                    observed_utc   = excluded.observed_utc,
                    content_hash   = excluded.content_hash,
                    observed_count = excluded.observed_count
                WHERE excluded.sequence >= device_packages.sequence;
                """;
            current.Parameters.AddWithValue("$id", inventory.DeviceId);
            current.Parameters.AddWithValue("$sequence", inventory.Sequence);
            current.Parameters.AddWithValue("$observed", SqliteDatabase.FormatTimestamp(inventory.GeneratedUtc));
            current.Parameters.AddWithValue("$hash", hash);
            current.Parameters.AddWithValue("$observedCount", inventory.ObservedCount);
            await current.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var history = scope.Connection.CreateCommand())
        {
            // One row per (device, sequence), so a buffered inventory that arrives twice — a
            // drain that raced a reconnect — does not become two history entries claiming the
            // frame changed and changed back.
            history.Transaction = (SqliteTransaction)transaction;
            history.CommandText = """
                INSERT OR IGNORE INTO device_package_history (device_id, sequence, observed_utc, content_hash)
                VALUES ($id, $sequence, $observed, $hash);
                """;
            history.Parameters.AddWithValue("$id", inventory.DeviceId);
            history.Parameters.AddWithValue("$sequence", inventory.Sequence);
            history.Parameters.AddWithValue("$observed", SqliteDatabase.FormatTimestamp(inventory.GeneratedUtc));
            history.Parameters.AddWithValue("$hash", hash);
            await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DevicePackageSet?> GetAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.device_id, d.sequence, d.observed_utc, d.content_hash, d.observed_count, s.body
            FROM device_packages d
            JOIN package_sets s ON s.content_hash = d.content_hash
            WHERE d.device_id = $id;
            """;
        command.Parameters.AddWithValue("$id", deviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSet(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DevicePackageSet>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.device_id, d.sequence, d.observed_utc, d.content_hash, d.observed_count, s.body
            FROM device_packages d
            JOIN package_sets s ON s.content_hash = d.content_hash
            ORDER BY d.device_id;
            """;

        var sets = new List<DevicePackageSet>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sets.Add(ReadSet(reader));
        }

        return sets;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DevicePackageHistoryEntry>> ListHistoryAsync(
        string deviceId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();

        // Ordered by sequence as well as by time, because a frame's own counter is the only
        // ordering that survives a clock that moved while it was offline.
        command.CommandText = """
            SELECT h.observed_utc, h.content_hash, s.body
            FROM device_package_history h
            JOIN package_sets s ON s.content_hash = h.content_hash
            WHERE h.device_id = $id
            ORDER BY h.sequence DESC, h.observed_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));

        var entries = new List<DevicePackageHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new DevicePackageHistoryEntry(
                SqliteDatabase.ParseTimestamp(reader.GetString(0)),
                reader.GetString(1),
                PackageInventory.ParseCanonical(reader.GetString(2))));
        }

        return entries;
    }

    /// <inheritdoc/>
    public async Task<int> ExpireHistoryAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();

        // The newest entry per device is exempt whatever its age. It is the row a frame's current
        // set was last seen changing at, and on a frame that has been stable for six months it is
        // the only evidence of when its packages last moved — rolling it off would leave a device
        // with a current inventory and an empty history that reads as "nothing has ever happened".
        command.CommandText = """
            DELETE FROM device_package_history
            WHERE observed_utc < $cutoff
              AND sequence < (
                  SELECT MAX(newest.sequence) FROM device_package_history newest
                  WHERE newest.device_id = device_package_history.device_id);
            """;
        command.Parameters.AddWithValue("$cutoff", SqliteDatabase.FormatTimestamp(cutoffUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> CollectUnreferencedSetsAsync(CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = """
            DELETE FROM package_sets
            WHERE content_hash NOT IN (SELECT content_hash FROM device_packages)
              AND content_hash NOT IN (SELECT content_hash FROM device_package_history);
            """;

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DevicePackageSet ReadSet(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt64(1),
        SqliteDatabase.ParseTimestamp(reader.GetString(2)),
        reader.GetString(3),
        reader.GetInt32(4),
        PackageInventory.ParseCanonical(reader.GetString(5)));
}
