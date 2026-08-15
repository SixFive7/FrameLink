using System.Text.Json;
using FrameLink.Protocol;
using Microsoft.Data.Sqlite;

namespace FrameLink.Control.Storage;

/// <summary>
/// Reports and events on raw <c>Microsoft.Data.Sqlite</c>.
/// </summary>
/// <remarks>
/// <para>
/// The per-resource list inside a report is stored as the JSON the agent sent, in one column,
/// rather than exploded into a resource table. That is a deliberate choice about what this data
/// is: the report is an <i>observation the frame made</i>, and the Fleet Manager's job is to
/// keep it verbatim and hand it back. Normalising it would make the server the second author of
/// a fact only the frame can know, and would need a migration every time §2.3's vocabulary grew.
/// </para>
/// <para>
/// The scalar columns beside it — loop state, counts, sequence — are duplicated out of the JSON
/// so a fleet-wide query ("which frames are not converged") does not have to parse every blob.
/// </para>
/// </remarks>
public sealed class SqliteFleetTelemetryStore(SqliteDatabase database) : IFleetTelemetryStore
{
    /// <inheritdoc/>
    public async Task RecordReportAsync(ReconcileReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();

        // The sequence guard is what makes a drained offline buffer safe: a report that predates
        // the one already stored is discarded rather than overwriting a newer picture.
        command.CommandText = """
            INSERT INTO device_reports (device_id, sequence, generated_utc, loop_state, in_sync, drifted,
                                        blocked, reboots_expected, current_resource, current_phase, payload)
            VALUES ($id, $sequence, $generated, $state, $inSync, $drifted, $blocked, $reboots, $resource, $phase, $payload)
            ON CONFLICT (device_id) DO UPDATE SET
                sequence         = excluded.sequence,
                generated_utc    = excluded.generated_utc,
                loop_state       = excluded.loop_state,
                in_sync          = excluded.in_sync,
                drifted          = excluded.drifted,
                blocked          = excluded.blocked,
                reboots_expected = excluded.reboots_expected,
                current_resource = excluded.current_resource,
                current_phase    = excluded.current_phase,
                payload          = excluded.payload
            WHERE excluded.sequence >= device_reports.sequence;
            """;

        command.Parameters.AddWithValue("$id", report.DeviceId);
        command.Parameters.AddWithValue("$sequence", report.Sequence);
        command.Parameters.AddWithValue("$generated", SqliteDatabase.FormatTimestamp(report.GeneratedUtc));
        command.Parameters.AddWithValue("$state", report.LoopState);
        command.Parameters.AddWithValue("$inSync", report.InSync);
        command.Parameters.AddWithValue("$drifted", report.Drifted);
        command.Parameters.AddWithValue("$blocked", report.Blocked);
        command.Parameters.AddWithValue("$reboots", report.RebootsExpected);
        command.Parameters.AddWithValue("$resource", (object?)report.CurrentResource ?? DBNull.Value);
        command.Parameters.AddWithValue("$phase", (object?)report.CurrentPhase ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$payload",
            JsonSerializer.Serialize(report, ProtocolJson.Default.ReconcileReport));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ReconcileReport?> GetReportAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM device_reports WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Deserialise(reader.GetString(0), ProtocolJson.Default.ReconcileReport);
    }

    /// <inheritdoc/>
    public async Task RecordEventAsync(DeviceEvent deviceEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceEvent);

        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_events (device_id, occurred_utc, kind, resource, summary, delta, attempts, payload)
            VALUES ($id, $occurred, $kind, $resource, $summary, $delta, $attempts, $payload);
            """;

        command.Parameters.AddWithValue("$id", deviceEvent.DeviceId);
        command.Parameters.AddWithValue("$occurred", SqliteDatabase.FormatTimestamp(deviceEvent.OccurredUtc));
        command.Parameters.AddWithValue("$kind", deviceEvent.Kind);
        command.Parameters.AddWithValue("$resource", (object?)deviceEvent.Resource ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", deviceEvent.Summary);
        command.Parameters.AddWithValue("$delta", (object?)deviceEvent.Delta ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempts", deviceEvent.Attempts);
        command.Parameters.AddWithValue(
            "$payload",
            JsonSerializer.Serialize(deviceEvent, ProtocolJson.Default.DeviceEvent));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeviceEvent>> ListEventsAsync(
        string deviceId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();

        // Ordered by rowid as well as by time, because a frame that buffered a burst while
        // offline can stamp several events on the same second and the arrival order is the only
        // remaining tie-break.
        command.CommandText = """
            SELECT payload FROM device_events
            WHERE device_id = $id
            ORDER BY occurred_utc DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var events = new List<DeviceEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Deserialise(reader.GetString(0), ProtocolJson.Default.DeviceEvent) is { } stored)
            {
                events.Add(stored);
            }
        }

        return events;
    }

    /// <inheritdoc/>
    public async Task<int> ExpireEventsAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = "DELETE FROM device_events WHERE occurred_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", SqliteDatabase.FormatTimestamp(cutoffUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a stored payload, treating a row this build cannot parse as absent.
    /// </summary>
    /// <remarks>
    /// Retention is a month, so a server downgrade can meet a row written by a newer build. A
    /// throw here would take out the whole device page over one unreadable row.
    /// </remarks>
    private static T? Deserialise<T>(string payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(payload, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
