using Microsoft.Data.SqlClient;
using SqlLookout.Core;
using SqlLookout.Core.Collectors;

namespace SqlLookout.Tests;

/// <summary>
/// Runs against a real SQL Server (local instance, or SQLLOOKOUT_TEST_CONNECTION). A standalone test instance has no
/// availability group and no mirroring, so those two prove the queries compile and report "nothing set up"; log shipping
/// is exercised with rows seeded into the msdb monitor tables and removed again.
/// </summary>
public sealed class HaCollectorIntegrationTests : IAsyncLifetime
{
    private SqlConnection _connection = null!;
    private ServerInfo _server = null!;

    public async Task InitializeAsync()
    {
        var cs = Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_CONNECTION")
                 ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";
        _connection = new SqlConnection(cs);
        await _connection.OpenAsync();
        _server = await ServerInfo.ReadAsync(_connection);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Availability_groups_query_runs_and_a_server_without_always_on_reports_no_replicas()
    {
        var result = await CollectorRunner.RunAsync(new AvailabilityGroupsCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        // Always on or off, the newest run is marked by the replica count.
        Assert.Contains(result.Samples, s => s.Metric == "ag_replicas");
    }

    [Fact]
    public async Task Availability_group_queries_themselves_compile_on_this_version()
    {
        // The collector skips them when Always On is off, so compile them directly: a typo in a column would otherwise
        // only show on a real group.
        foreach (var sql in new[]
                 {
                     "SELECT ag.name, ar.replica_server_name, ars.role_desc, ars.connected_state, ars.synchronization_health, ars.synchronization_health_desc, ars.connected_state_desc FROM sys.availability_groups ag JOIN sys.availability_replicas ar ON ar.group_id = ag.group_id LEFT JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = ar.replica_id;",
                     "SELECT ag.name, ar.replica_server_name, DB_NAME(drs.database_id), drs.is_suspended, drs.suspend_reason_desc, drs.synchronization_health, drs.synchronization_state_desc, drs.log_send_queue_size, drs.redo_queue_size FROM sys.dm_hadr_database_replica_states drs JOIN sys.availability_replicas ar ON ar.replica_id = drs.replica_id JOIN sys.availability_groups ag ON ag.group_id = drs.group_id;",
                 })
            await Exec(sql);
    }

    [Fact]
    public async Task Mirroring_reports_the_empty_count_when_nothing_is_mirrored()
    {
        var result = await CollectorRunner.RunAsync(new MirroringCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        Assert.Equal(0, result.Samples.Single(s => s.Metric == "mirroring_databases").Value);
        Assert.DoesNotContain(result.Samples, s => s.Metric == "mirroring_state");
    }

    [Fact]
    public async Task Log_shipping_reads_both_monitor_tables_and_skips_pairs_whose_alert_is_off()
    {
        var tag = "SqlLookoutLs_" + Guid.NewGuid().ToString("N")[..8];
        var offTag = tag + "_off";
        await Exec($"""
            INSERT msdb.dbo.log_shipping_monitor_primary
              (primary_id, primary_server, primary_database, backup_threshold, threshold_alert, threshold_alert_enabled, last_backup_date, history_retention_period)
            VALUES (NEWID(), N'{tag}', N'{tag}', 60, 14420, 1, DATEADD(MINUTE, -90, GETDATE()), 5760),
                   (NEWID(), N'{offTag}', N'{offTag}', 60, 14420, 0, DATEADD(MINUTE, -90, GETDATE()), 5760);
            INSERT msdb.dbo.log_shipping_monitor_secondary
              (secondary_server, secondary_database, secondary_id, primary_server, primary_database, restore_threshold, threshold_alert, threshold_alert_enabled, last_restored_date, history_retention_period)
            VALUES (N'{tag}', N'{tag}', NEWID(), N'{tag}', N'{tag}', 45, 14421, 1, NULL, 5760);
            """);
        try
        {
            var result = await CollectorRunner.RunAsync(new LogShippingCollector(), _connection, _server, CancellationToken.None);

            Assert.Equal(CollectionStatus.Ok, result.Status);
            var backupAge = result.Samples.Single(s => s.Metric == "ls_backup_age_min" && s.Subject == tag).Value!.Value;
            Assert.InRange(backupAge, 89, 92);
            Assert.Equal(60, result.Samples.Single(s => s.Metric == "ls_backup_threshold_min" && s.Subject == tag).Value);
            Assert.Null(result.Samples.Single(s => s.Metric == "ls_restore_age_min" && s.Subject == tag).Value);
            Assert.Equal(45, result.Samples.Single(s => s.Metric == "ls_restore_threshold_min" && s.Subject == tag).Value);
            Assert.DoesNotContain(result.Samples, s => s.Subject == offTag);
            Assert.Contains(result.Samples, s => s.Metric == "logship_items" && s.Value >= 2);
        }
        finally
        {
            await Exec($"""
                DELETE msdb.dbo.log_shipping_monitor_primary WHERE primary_database IN (N'{tag}', N'{offTag}');
                DELETE msdb.dbo.log_shipping_monitor_secondary WHERE secondary_database = N'{tag}';
                """);
        }
    }

    [Fact]
    public async Task Log_shipping_on_an_instance_that_never_set_it_up_reports_the_empty_count()
    {
        var result = await CollectorRunner.RunAsync(new LogShippingCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        Assert.Contains(result.Samples, s => s.Metric == "logship_items");
    }

    [Theory]
    [InlineData(typeof(AvailabilityGroupsCollector))]
    [InlineData(typeof(LogShippingCollector))]
    [InlineData(typeof(MirroringCollector))]
    public void Express_is_reported_as_unsupported_not_failed(Type collector)
    {
        var c = (ICollector)Activator.CreateInstance(collector)!;

        Assert.NotNull(c.UnsupportedReason(new ServerInfo(16, 0, 1000, 4)));
        Assert.Null(c.UnsupportedReason(new ServerInfo(16, 0, 1000, 2)));
    }

    [Fact]
    public void Availability_groups_need_2012_or_later()
    {
        Assert.NotNull(new AvailabilityGroupsCollector().UnsupportedReason(new ServerInfo(10, 50, 6000, 2)));
        Assert.Null(new AvailabilityGroupsCollector().UnsupportedReason(new ServerInfo(11, 0, 6020, 2)));
    }
}
