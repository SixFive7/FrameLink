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
    /// 2: the reconciliation report and device-event tables of §3.5.
    /// </summary>
    /// <remarks>
    /// The schema is additive and every statement below is <c>IF NOT EXISTS</c>, so an existing
    /// volume moves from 1 to 2 by starting the new container and nothing else. There is no
    /// migration step because there is nothing to migrate — no existing column changed meaning.
    /// </remarks>
    private const int SchemaVersion = 2;

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

    /// <summary>Releases the writer semaphore.</summary>
    public void Dispose() => _writeLock.Dispose();

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
