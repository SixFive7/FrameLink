using FrameLink.Control.Alerting;

namespace FrameLink.Control.Storage;

/// <summary>
/// The open-alert set, in one table keyed by the condition's own identity (§3.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>No foreign key to <c>devices</c>, deliberately.</b> Three of the four rules in
/// <see cref="FleetWatch"/> are about a device and one — the call server being down — is about the
/// server itself, so the column is nullable and unconstrained. A cascade would also be actively
/// wrong for the device rules: forgetting a frame is an operator action that <i>resolves</i> an
/// alert about it, and a cascade would delete the row without anything ever saying so.
/// <see cref="FleetWatch"/> closes it as an ordinary clear instead, and somebody gets told.
/// </para>
/// <para>
/// The table is bounded by construction. There is one row per condition that is true right now,
/// the rule set is fixed at four, and three of them are per-device — so its ceiling is
/// three times the fleet plus one, which is why §3.5's month of retention does not apply to it and
/// the reaper does not sweep it.
/// </para>
/// </remarks>
public sealed class SqliteAlertStore(SqliteDatabase database) : IAlertStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<OpenAlert>> ListOpenAsync(CancellationToken cancellationToken)
    {
        await using var connection = database.OpenRead();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, kind, severity, subject, detail, device_id, device_name,
                   opened_utc, notified_utc
            FROM fleet_alerts
            ORDER BY opened_utc ASC, key ASC;
            """;

        var open = new List<OpenAlert>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            open.Add(new OpenAlert(
                new FleetAlert
                {
                    Key = reader.GetString(0),
                    Kind = reader.GetString(1),
                    Severity = reader.GetString(2) == nameof(AlertSeverity.Critical)
                        ? AlertSeverity.Critical
                        : AlertSeverity.Warning,
                    Subject = reader.GetString(3),
                    Detail = reader.GetString(4),
                    DeviceId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    DeviceName = reader.IsDBNull(6) ? null : reader.GetString(6),
                },
                SqliteDatabase.ParseTimestamp(reader.GetString(7)),
                reader.IsDBNull(8) ? null : SqliteDatabase.ParseTimestamp(reader.GetString(8))));
        }

        return open;
    }

    /// <inheritdoc/>
    public async Task<OpenAlert> OpenAsync(
        FleetAlert alert,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);

        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();

        // The upsert refreshes the wording and leaves opened_utc and notified_utc alone. Both
        // omissions are load-bearing: moving opened_utc would make a week-old condition look new
        // on every tick, and clearing notified_utc would re-deliver it on every tick. The wording
        // is refreshed because it carries a duration — "out of contact for 3 days" has to become
        // "for 4 days" for the console to be worth reading.
        command.CommandText = """
            INSERT INTO fleet_alerts
                (key, kind, severity, subject, detail, device_id, device_name, opened_utc, notified_utc)
            VALUES
                ($key, $kind, $severity, $subject, $detail, $deviceId, $deviceName, $opened, NULL)
            ON CONFLICT (key) DO UPDATE SET
                kind        = excluded.kind,
                severity    = excluded.severity,
                subject     = excluded.subject,
                detail      = excluded.detail,
                device_id   = excluded.device_id,
                device_name = excluded.device_name
            RETURNING opened_utc, notified_utc;
            """;

        command.Parameters.AddWithValue("$key", alert.Key);
        command.Parameters.AddWithValue("$kind", alert.Kind);
        command.Parameters.AddWithValue("$severity", alert.Severity.ToString());
        command.Parameters.AddWithValue("$subject", alert.Subject);
        command.Parameters.AddWithValue("$detail", alert.Detail);
        command.Parameters.AddWithValue("$deviceId", (object?)alert.DeviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$deviceName", (object?)alert.DeviceName ?? DBNull.Value);
        command.Parameters.AddWithValue("$opened", SqliteDatabase.FormatTimestamp(nowUtc));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"The alert '{alert.Key}' was written and could not be read back.");
        }

        return new OpenAlert(
            alert,
            SqliteDatabase.ParseTimestamp(reader.GetString(0)),
            reader.IsDBNull(1) ? null : SqliteDatabase.ParseTimestamp(reader.GetString(1)));
    }

    /// <inheritdoc/>
    public async Task MarkNotifiedAsync(string key, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = "UPDATE fleet_alerts SET notified_utc = $now WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", SqliteDatabase.FormatTimestamp(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> CloseAsync(string key, CancellationToken cancellationToken)
    {
        await using var scope = await database.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = "DELETE FROM fleet_alerts WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }
}
