using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FrameLink.Control.Storage;

/// <summary>
/// Connection management and schema for the single SQLite file the Fleet Manager owns.
/// </summary>
/// <remarks>
/// <para>
/// Raw <c>Microsoft.Data.Sqlite</c>, not EF Core: §6.2 records that EF Core is documented as
/// production-unsuitable under Native AOT, and the whole delivery format here is an AOT
/// binary. Hand-written SQL is also the honest choice for a schema this small.
/// </para>
/// <para>
/// <b>WAL, plus one writer.</b> WAL lets readers run while a write is in flight, which is
/// what a socket-per-device server needs. Writes are additionally serialised through a
/// semaphore rather than left to SQLite's busy handler: with one process there is no reason
/// to ever meet <c>SQLITE_BUSY</c>, and a lock is cheaper to reason about than a retry
/// policy that only misbehaves under load.
/// </para>
/// </remarks>
public sealed class SqliteDatabase : IDisposable
{
    /// <summary>
    /// 4: <c>fleet_alerts</c>, the open-condition set §3.5's alerting is de-duplicated against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema is additive and every statement below is <c>IF NOT EXISTS</c>, so an existing
    /// volume moves up by starting the new container and nothing else. There is no migration step
    /// because there is nothing to migrate — no existing column has ever changed meaning.
    /// </para>
    /// <para>
    /// <b>That additivity is what makes an image rollback safe</b>, and it is worth stating as a
    /// property rather than leaving as a happy accident. An older Fleet Manager started against a
    /// newer volume finds every table and column it knows about exactly where it left them, and
    /// simply never reads the ones added after it — so the rollback path in the deployment guide
    /// is "run the previous tag", with no dump, no downgrade script and no data loss. The rule
    /// that keeps it true: <b>add tables and nullable columns; never repurpose or drop one.</b>
    /// </para>
    /// </remarks>
    private const int SchemaVersion = 4;

    /// <summary>Key of the settings revision counter in <c>control_meta</c>.</summary>
    internal const string RevisionKey = "settings_revision";

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString;

    /// <summary>Opens (or creates) the database file and brings the schema up to date.</summary>
    public SqliteDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        Initialise();
    }

    /// <summary>Opens a pooled connection for reading.</summary>
    /// <remarks>WAL means this never blocks on, and is never blocked by, the writer.</remarks>
    public SqliteConnection OpenRead()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Takes the single writer slot and opens a connection for it.
    /// </summary>
    /// <remarks>Dispose the returned scope to release the slot; the connection dies with it.</remarks>
    public async Task<WriteScope> OpenWriteAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new WriteScope(connection, _writeLock);
        }
        catch
        {
            _writeLock.Release();
            throw;
        }
    }

    /// <summary>Formats a timestamp the way every column in this schema stores one.</summary>
    /// <remarks>
    /// Round-trip ISO-8601 in UTC. Text rather than a numeric epoch because it sorts
    /// lexicographically in the same order it sorts chronologically — so range queries work —
    /// and because an operator inspecting the volume-mapped file can read it.
    /// </remarks>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Parses a timestamp written by <see cref="FormatTimestamp"/>.</summary>
    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    /// <summary>Releases the writer semaphore and this database's pooled file handles.</summary>
    /// <remarks>
    /// <para>
    /// Closing a <see cref="SqliteConnection"/> returns it to a pool rather than closing the
    /// file, so after the last connection is disposed the operating system still holds this
    /// database open. Disposing only the semaphore therefore left <c>Dispose</c> not meaning
    /// what it says: the object was gone and the file was not released.
    /// </para>
    /// <para>
    /// <b>Scoped, never <c>ClearAllPools</c>.</b> The pool is process-global and keyed by
    /// connection string, so <see cref="SqliteConnection.ClearAllPools"/> reaches into every
    /// other database open in the process — including ones another thread is using right now.
    /// <see cref="SqliteConnection.ClearPool"/> resolves the pool group from the connection
    /// string alone, without opening anything, and so touches this file and no other.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        _writeLock.Dispose();

        using var handle = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(handle);
    }

    private void Initialise()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA busy_timeout=5000;");

        Execute(
            connection,
            """
            CREATE TABLE IF NOT EXISTS control_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS devices (
                device_id         TEXT PRIMARY KEY,
                public_key        TEXT NOT NULL,
                state             TEXT NOT NULL CHECK (state IN ('pending', 'adopted', 'blocked')),
                display_name      TEXT,
                hardware_serial   TEXT,
                agent_version     TEXT,
                agent_status      TEXT,
                protocol_version  INTEGER,
                first_seen_utc    TEXT NOT NULL,
                last_seen_utc     TEXT NOT NULL,
                state_changed_utc TEXT NOT NULL,
                last_remote_addr  TEXT
            );

            CREATE INDEX IF NOT EXISTS devices_state_last_seen
                ON devices (state, last_seen_utc);

            CREATE TABLE IF NOT EXISTS fleet_settings (
                key         TEXT PRIMARY KEY,
                value       TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS device_settings (
                device_id   TEXT NOT NULL REFERENCES devices (device_id) ON DELETE CASCADE,
                key         TEXT NOT NULL,
                value       TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (device_id, key)
            );

            CREATE TABLE IF NOT EXISTS device_reports (
                device_id        TEXT PRIMARY KEY REFERENCES devices (device_id) ON DELETE CASCADE,
                sequence         INTEGER NOT NULL,
                generated_utc    TEXT NOT NULL,
                loop_state       TEXT NOT NULL,
                in_sync          INTEGER NOT NULL,
                drifted          INTEGER NOT NULL,
                blocked          INTEGER NOT NULL,
                reboots_expected INTEGER NOT NULL,
                current_resource TEXT,
                current_phase    TEXT,
                payload          TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS device_events (
                device_id    TEXT NOT NULL REFERENCES devices (device_id) ON DELETE CASCADE,
                occurred_utc TEXT NOT NULL,
                kind         TEXT NOT NULL,
                resource     TEXT,
                summary      TEXT NOT NULL,
                delta        TEXT,
                attempts     INTEGER NOT NULL DEFAULT 0,
                payload      TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS device_events_device_time
                ON device_events (device_id, occurred_utc DESC);

            CREATE INDEX IF NOT EXISTS device_events_time
                ON device_events (occurred_utc);

            -- One row per DISTINCT package set across the whole fleet, keyed by the hash of its
            -- canonical rendering. Ten converged frames share one row; a frame reporting the same
            -- set twice writes nothing. This is what keeps ~930 packages per device per report
            -- from becoming the largest thing in the database by two orders of magnitude.
            CREATE TABLE IF NOT EXISTS package_sets (
                content_hash  TEXT PRIMARY KEY,
                package_count INTEGER NOT NULL,
                body          TEXT NOT NULL
            );

            -- The current set, one row per device, replaced. No cascade to package_sets: a blob
            -- outlives the last device that referenced it until the sweep collects it, which is
            -- what lets a device be forgotten inside a transaction that knows nothing about who
            -- else shares its packages.
            CREATE TABLE IF NOT EXISTS device_packages (
                device_id      TEXT PRIMARY KEY REFERENCES devices (device_id) ON DELETE CASCADE,
                sequence       INTEGER NOT NULL,
                observed_utc   TEXT NOT NULL,
                content_hash   TEXT NOT NULL REFERENCES package_sets (content_hash),
                observed_count INTEGER NOT NULL
            );

            -- §3.5's month of history. One row per reported change — the agent only reports when
            -- the set moved — at roughly 60 bytes each, so a frame's whole month is a few hundred
            -- bytes of index plus whatever distinct blobs it passed through.
            CREATE TABLE IF NOT EXISTS device_package_history (
                device_id    TEXT NOT NULL REFERENCES devices (device_id) ON DELETE CASCADE,
                sequence     INTEGER NOT NULL,
                observed_utc TEXT NOT NULL,
                content_hash TEXT NOT NULL REFERENCES package_sets (content_hash),
                PRIMARY KEY (device_id, sequence)
            );

            CREATE INDEX IF NOT EXISTS device_package_history_time
                ON device_package_history (observed_utc);

            -- One row per alert condition that is true RIGHT NOW (§3.5). Deliberately not a
            -- history table: what is closed has already been written to the log, and a second copy
            -- would need retention rules of its own. No foreign key to devices, because one of the
            -- rules is about this server rather than about a frame, and because forgetting a frame
            -- must clear its alerts through the ordinary path that tells somebody — not through a
            -- cascade that deletes the row in silence.
            CREATE TABLE IF NOT EXISTS fleet_alerts (
                key          TEXT PRIMARY KEY,
                kind         TEXT NOT NULL,
                severity     TEXT NOT NULL,
                subject      TEXT NOT NULL,
                detail       TEXT NOT NULL,
                device_id    TEXT,
                device_name  TEXT,
                opened_utc   TEXT NOT NULL,
                notified_utc TEXT
            );

            INSERT OR IGNORE INTO control_meta (key, value) VALUES ('schema_version', '1');
            INSERT OR IGNORE INTO control_meta (key, value) VALUES ('settings_revision', '0');
            """);

        // Recorded so a future migration knows what it is migrating from. There is nothing to
        // migrate yet, which is exactly when the marker has to start being written.
        using var stamp = connection.CreateCommand();
        stamp.CommandText = "UPDATE control_meta SET value = $version WHERE key = 'schema_version';";
        stamp.Parameters.AddWithValue("$version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        stamp.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>An exclusive write connection. Disposing it frees the writer slot.</summary>
    public sealed class WriteScope(SqliteConnection connection, SemaphoreSlim writeLock) : IAsyncDisposable
    {
        /// <summary>The open connection. Valid until this scope is disposed.</summary>
        public SqliteConnection Connection { get; } = connection;

        /// <summary>Closes the connection and releases the writer slot.</summary>
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            writeLock.Release();
        }
    }
}
