using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Always On availability groups: health and connection of each replica, and suspension and health of each database
/// replica, as this instance sees them (the primary sees every replica, a secondary only itself). Numbers are used for
/// decisions (connected_state 0/1, synchronization_health 0 NOT_HEALTHY / 1 PARTIALLY_HEALTHY / 2 HEALTHY), text only for
/// display. Every run writes <c>ag_replicas</c> (the replica count) even with no group, so the newest run is always known
/// and a group that was removed stops showing its last reading. Needs SQL Server 2012 or later and Always On switched on.
/// </summary>
public sealed class AvailabilityGroupsCollector : ICollector
{
    public string Name => "availability_groups";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) =>
        server.IsExpress ? "SQL Server Express does not support availability groups"
        : !server.IsAtLeast(11) ? "Availability groups need SQL Server 2012 or later"
        : null;

    private const string ReplicaSql = """
        SELECT ag.name, ar.replica_server_name, ISNULL(ars.role_desc, N''), ars.connected_state,
               ars.synchronization_health, ISNULL(ars.synchronization_health_desc, N''), ISNULL(ars.connected_state_desc, N'')
        FROM sys.availability_groups AS ag
        JOIN sys.availability_replicas AS ar ON ar.group_id = ag.group_id
        LEFT JOIN sys.dm_hadr_availability_replica_states AS ars ON ars.replica_id = ar.replica_id;
        """;

    private const string DatabaseSql = """
        SELECT ag.name, ar.replica_server_name, DB_NAME(drs.database_id), drs.is_suspended, ISNULL(drs.suspend_reason_desc, N''),
               drs.synchronization_health, ISNULL(drs.synchronization_state_desc, N''), drs.log_send_queue_size, drs.redo_queue_size
        FROM sys.dm_hadr_database_replica_states AS drs
        JOIN sys.availability_replicas AS ar ON ar.replica_id = drs.replica_id
        JOIN sys.availability_groups AS ag ON ag.group_id = drs.group_id;
        """;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var samples = new List<Sample>();
        bool enabled;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT CAST(ISNULL(SERVERPROPERTY('IsHadrEnabled'), 0) AS int);";
            enabled = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
        }

        var replicas = 0;
        if (enabled)
        {
            await Read(connection, ReplicaSql, ct, r =>
            {
                replicas++;
                var subject = $"{r.GetString(0)} / {r.GetString(1)}";
                var role = r.GetString(2);
                var connected = r.IsDBNull(3) ? (int?)null : Convert.ToInt32(r.GetValue(3));
                var health = r.IsDBNull(4) ? (int?)null : Convert.ToInt32(r.GetValue(4));
                // A secondary does not see the state of the other replicas: no reading, not "disconnected".
                if (connected is null && health is null) return;
                samples.Add(new Sample("ag_replica_connected", subject, connected, string.IsNullOrEmpty(role) ? r.GetString(6) : role));
                samples.Add(new Sample("ag_replica_health", subject, health, r.GetString(5)));
            });
            await Read(connection, DatabaseSql, ct, r =>
            {
                if (r.IsDBNull(2)) return;
                var subject = $"{r.GetString(0)} / {r.GetString(1)} / {r.GetString(2)}";
                samples.Add(new Sample("ag_db_suspended", subject, r.IsDBNull(3) ? null : Convert.ToInt32(r.GetValue(3)), r.GetString(4)));
                samples.Add(new Sample("ag_db_health", subject, r.IsDBNull(5) ? null : Convert.ToInt32(r.GetValue(5)), r.GetString(6)));
                samples.Add(new Sample("ag_log_send_queue_kb", subject, r.IsDBNull(7) ? null : Convert.ToDouble(r.GetValue(7))));
                samples.Add(new Sample("ag_redo_queue_kb", subject, r.IsDBNull(8) ? null : Convert.ToDouble(r.GetValue(8))));
            });
        }
        samples.Add(new Sample("ag_replicas", null, replicas));
        return CollectionResult.Ok(samples);
    }

    private static async Task Read(SqlConnection connection, string sql, CancellationToken ct, Action<SqlDataReader> onRow)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) onRow(reader);
    }
}
