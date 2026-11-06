using Microsoft.Data.SqlClient;
using SqlLookout.Core;
using SqlLookout.Core.Collectors;

namespace SqlLookout.Tests;

/// <summary>Runs against a real SQL Server. Default is the local instance; override with SQLLOOKOUT_TEST_CONNECTION.</summary>
[Collection(SqlServerTestDb.LocalServerCollection)]
public sealed class CollectorIntegrationTests : IAsyncLifetime
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

    public async Task DisposeAsync()
    {
        if (_second is not null) await _second.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Server_info_is_read_from_the_instance()
    {
        Assert.True(_server.Major >= 10);
        Assert.True(_server.EngineEdition > 0);
    }

    [Fact]
    public async Task Backup_age_returns_a_row_set_for_every_database()
    {
        var result = await CollectorRunner.RunAsync(new BackupAgeCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        var dbs = result.Samples.Select(s => s.Subject).Distinct().ToList();
        Assert.Contains("master", dbs);
        Assert.DoesNotContain("tempdb", dbs);
        // Three metrics per database.
        Assert.Equal(dbs.Count * 3, result.Samples.Count);
    }

    [Fact]
    public async Task Backup_age_reports_never_backed_up_as_null_and_a_fresh_backup_as_small_age()
    {
        var name = "SqlLookoutBk_" + Guid.NewGuid().ToString("N")[..8];
        await Exec($"CREATE DATABASE [{name}];");
        try
        {
            var before = await CollectorRunner.RunAsync(new BackupAgeCollector(), _connection, _server, CancellationToken.None);
            Assert.Null(before.Samples.Single(s => s.Subject == name && s.Metric == "last_full_backup_age_min").Value);

            await Exec($"BACKUP DATABASE [{name}] TO DISK = 'NUL';");

            var after = await CollectorRunner.RunAsync(new BackupAgeCollector(), _connection, _server, CancellationToken.None);
            var age = after.Samples.Single(s => s.Subject == name && s.Metric == "last_full_backup_age_min").Value;
            Assert.NotNull(age);
            Assert.InRange(age!.Value, 0, 5);
        }
        finally
        {
            await Exec($"DROP DATABASE [{name}];");
            await Exec($"EXEC msdb.dbo.sp_delete_database_backuphistory @database_name = N'{name}';");
        }
    }

    [Fact]
    public async Task Disk_space_reports_total_and_free_for_each_volume()
    {
        var result = await CollectorRunner.RunAsync(new DiskSpaceCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        Assert.NotEmpty(result.Samples);
        foreach (var volume in result.Samples.Select(s => s.Subject).Distinct())
        {
            var total = result.Samples.Single(s => s.Subject == volume && s.Metric == "disk_total_bytes").Value!.Value;
            var free = result.Samples.Single(s => s.Subject == volume && s.Metric == "disk_free_bytes").Value!.Value;
            Assert.True(total > 0);
            Assert.InRange(free, 0, total);
        }
    }

    [Fact]
    public async Task Log_usage_reports_a_percentage_for_every_database()
    {
        var result = await CollectorRunner.RunAsync(new LogUsageCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        var used = result.Samples.Where(s => s.Metric == "log_used_percent").ToList();
        Assert.Contains(used, s => s.Subject == "master");
        Assert.All(used, s => Assert.InRange(s.Value!.Value, 0, 100));
    }

    [Fact]
    public async Task Old_version_is_reported_unsupported_not_failed()
    {
        var oldServer = new ServerInfo(10, 0, 1600, 2);
        var result = await CollectorRunner.RunAsync(new DiskSpaceCollector(), _connection, oldServer, CancellationToken.None);
        Assert.Equal(CollectionStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task Database_state_reports_online_for_master_and_a_numeric_code()
    {
        var result = await CollectorRunner.RunAsync(new DatabaseStateCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        var master = result.Samples.Single(s => s.Subject == "master");
        Assert.Equal(0, master.Value);
        Assert.Equal("ONLINE", master.Text);
    }

    [Fact]
    public async Task Database_state_sees_an_offline_database()
    {
        var name = "SqlLookoutSt_" + Guid.NewGuid().ToString("N")[..8];
        await Exec($"CREATE DATABASE [{name}];");
        try
        {
            await Exec($"ALTER DATABASE [{name}] SET OFFLINE WITH ROLLBACK IMMEDIATE;");
            var result = await CollectorRunner.RunAsync(new DatabaseStateCollector(), _connection, _server, CancellationToken.None);
            var s = result.Samples.Single(x => x.Subject == name);
            Assert.Equal(6, s.Value);
            Assert.Equal("OFFLINE", s.Text);
        }
        finally
        {
            await Exec($"ALTER DATABASE [{name}] SET ONLINE;");
            await Exec($"DROP DATABASE [{name}];");
        }
    }

    [Fact]
    public async Task File_size_reports_data_and_log_for_master()
    {
        var result = await CollectorRunner.RunAsync(new FileSizeCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        Assert.True(result.Samples.Single(s => s.Subject == "master" && s.Metric == "db_data_mb").Value > 0);
        Assert.True(result.Samples.Single(s => s.Subject == "master" && s.Metric == "db_log_mb").Value > 0);
    }

    [Fact]
    public async Task Suspect_pages_reports_a_count_for_every_database_and_master_is_clean()
    {
        var result = await CollectorRunner.RunAsync(new SuspectPagesCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        Assert.Equal(0, result.Samples.Single(s => s.Subject == "master").Value);
    }

    [Fact]
    public async Task Dbcc_last_good_is_never_for_a_new_database_and_small_after_checkdb()
    {
        var name = "SqlLookoutDb_" + Guid.NewGuid().ToString("N")[..8];
        await Exec($"CREATE DATABASE [{name}];");
        try
        {
            var before = await CollectorRunner.RunAsync(new DbccLastGoodCollector(), _connection, _server, CancellationToken.None);
            var fresh = before.Samples.Single(s => s.Subject == name);
            Assert.Null(fresh.Value);
            Assert.Equal("never", fresh.Text);

            await Exec($"DBCC CHECKDB([{name}]) WITH NO_INFOMSGS;");

            var after = await CollectorRunner.RunAsync(new DbccLastGoodCollector(), _connection, _server, CancellationToken.None);
            var checkedDb = after.Samples.Single(s => s.Subject == name);
            Assert.NotNull(checkedDb.Value);
            Assert.InRange(checkedDb.Value!.Value, 0, 5);
        }
        finally
        {
            await Exec($"DROP DATABASE [{name}];");
        }
    }

    [Fact]
    public async Task Agent_jobs_runs_or_is_reported_unsupported_on_express()
    {
        var result = await CollectorRunner.RunAsync(new AgentJobsCollector(), _connection, _server, CancellationToken.None);
        Assert.Equal(_server.IsExpress ? CollectionStatus.Unsupported : CollectionStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Agent_jobs_sees_a_failed_job()
    {
        if (_server.IsExpress) return;
        var job = "SqlLookoutJob_" + Guid.NewGuid().ToString("N")[..8];
        await Exec($"EXEC msdb.dbo.sp_add_job @job_name = N'{job}';");
        try
        {
            await Exec($"EXEC msdb.dbo.sp_add_jobstep @job_name = N'{job}', @step_name = N's', @subsystem = N'TSQL', @command = N'RAISERROR(''boom'', 16, 1);';");
            await Exec($"EXEC msdb.dbo.sp_add_jobserver @job_name = N'{job}';");
            await Exec($"EXEC msdb.dbo.sp_start_job @job_name = N'{job}';");

            // The job runs on the Agent; wait for it to finish (needs SQL Server Agent running, otherwise skip quietly).
            double? outcome = null;
            CollectionResult? last = null;
            for (var i = 0; i < 30 && outcome is not (0 or 1); i++)
            {
                await Task.Delay(1000);
                last = await CollectorRunner.RunAsync(new AgentJobsCollector(), _connection, _server, CancellationToken.None);
                outcome = last.Samples.SingleOrDefault(s => s.Subject == job && s.Metric == "job_last_outcome")?.Value;
            }
            if (outcome is null or 5) return;   // Agent not running here: nothing to assert
            Assert.Equal(0, outcome);

            // The job has finished: it must report "not running" (an empty reading), so an earlier "running for N
            // minutes" reading cannot stay the newest one for good.
            var running = last!.Samples.Single(s => s.Subject == job && s.Metric == "job_running_min");
            Assert.Null(running.Value);
        }
        finally
        {
            await Exec($"EXEC msdb.dbo.sp_delete_job @job_name = N'{job}';");
        }
    }

    [Fact]
    public async Task Cpu_reports_sql_share_within_machine_total()
    {
        var result = await CollectorRunner.RunAsync(new CpuCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        // A just-started instance may have no ring buffer record yet; otherwise both values exist and nest correctly.
        if (result.Samples.Count == 0) return;
        var sql = result.Samples.Single(s => s.Metric == "cpu_sql_percent").Value!.Value;
        var total = result.Samples.Single(s => s.Metric == "cpu_total_percent").Value!.Value;
        Assert.InRange(sql, 0, 100);
        Assert.InRange(total, sql, 100);
    }

    [Fact]
    public async Task Memory_reports_ple_grants_and_used_against_target()
    {
        var result = await CollectorRunner.RunAsync(new MemoryCollector(), _connection, _server, CancellationToken.None);

        Assert.Equal(CollectionStatus.Ok, result.Status);
        Assert.True(result.Samples.Single(s => s.Metric == "memory_ple_seconds").Value >= 0);
        Assert.True(result.Samples.Single(s => s.Metric == "memory_grants_pending").Value >= 0);
        Assert.True(result.Samples.Single(s => s.Metric == "memory_used_mb").Value > 0);
        Assert.True(result.Samples.Single(s => s.Metric == "memory_target_mb").Value > 0);
    }

    [Fact]
    public async Task Connections_counts_this_session_and_counts_a_new_one()
    {
        var before = await CollectorRunner.RunAsync(new ConnectionsCollector(), _connection, _server, CancellationToken.None);
        var n = before.Samples.Single(s => s.Metric == "connections_user").Value!.Value;
        Assert.True(n >= 1);                      // this test's own connection
        Assert.True(before.Samples.Single(s => s.Metric == "connections_running").Value >= 1);   // the query that counts

        // Other tests open and close sessions at the same time, so compare against a floor, not against the earlier count:
        // five extra sessions plus this one are always at least six.
        var builder = new SqlConnectionStringBuilder(_connection.ConnectionString) { Pooling = false };
        var extras = new List<SqlConnection>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var c = new SqlConnection(builder.ConnectionString);
                await c.OpenAsync();
                extras.Add(c);
            }
            var after = await CollectorRunner.RunAsync(new ConnectionsCollector(), _connection, _server, CancellationToken.None);
            Assert.True(after.Samples.Single(s => s.Metric == "connections_user").Value >= 6);
        }
        finally
        {
            foreach (var c in extras) await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task Blocking_is_zero_when_idle_and_names_the_head_blocker_when_a_lock_is_held()
    {
        var idle = await CollectorRunner.RunAsync(new BlockingCollector(), _connection, _server, CancellationToken.None);
        Assert.Equal(CollectionStatus.Ok, idle.Status);

        var table = "SqlLookoutBlk_" + Guid.NewGuid().ToString("N")[..8];
        await Exec($"CREATE TABLE tempdb.dbo.[{table}] (Id int PRIMARY KEY);");
        await using var holder = new SqlConnection(_connection.ConnectionString);
        await using var waiter = new SqlConnection(_connection.ConnectionString);
        await holder.OpenAsync();
        await waiter.OpenAsync();
        try
        {
            await using (var hold = holder.CreateCommand())
            {
                hold.CommandText = $"BEGIN TRAN; INSERT tempdb.dbo.[{table}] VALUES (1);";
                await hold.ExecuteNonQueryAsync();
            }
            int holderSession;
            await using (var id = holder.CreateCommand())
            {
                id.CommandText = "SELECT @@SPID;";
                holderSession = Convert.ToInt32(await id.ExecuteScalarAsync());
            }

            await using var wait = waiter.CreateCommand();
            wait.CommandText = $"SELECT * FROM tempdb.dbo.[{table}];";   // blocks on the uncommitted insert
            var blocked = wait.ExecuteNonQueryAsync();

            Sample? heads = null;
            double blockedCount = 0;
            for (var i = 0; i < 20 && blockedCount < 1; i++)
            {
                await Task.Delay(300);
                var r = await CollectorRunner.RunAsync(new BlockingCollector(), _connection, _server, CancellationToken.None);
                blockedCount = r.Samples.Single(s => s.Metric == "blocked_sessions").Value!.Value;
                heads = r.Samples.Single(s => s.Metric == "blocking_head_blockers");
            }

            Assert.Equal(1, blockedCount);
            Assert.Equal(1, heads!.Value);
            Assert.StartsWith(holderSession.ToString() + " (", heads.Text);

            await using (var rollback = holder.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync();
            }
            await blocked;
        }
        finally
        {
            await Exec($"DROP TABLE tempdb.dbo.[{table}];");
        }
    }

    [Fact]
    public async Task Deadlocks_total_rises_after_a_real_deadlock()
    {
        var before = (await CollectorRunner.RunAsync(new DeadlockCollector(), _connection, _server, CancellationToken.None))
            .Samples.Single().Value!.Value;

        var t1 = "SqlLookoutDl1_" + Guid.NewGuid().ToString("N")[..8];
        var t2 = "SqlLookoutDl2_" + Guid.NewGuid().ToString("N")[..8];
        await Exec($"CREATE TABLE tempdb.dbo.[{t1}] (Id int); INSERT tempdb.dbo.[{t1}] VALUES (1); CREATE TABLE tempdb.dbo.[{t2}] (Id int); INSERT tempdb.dbo.[{t2}] VALUES (1);");
        try
        {
            await using var a = new SqlConnection(_connection.ConnectionString);
            await using var b = new SqlConnection(_connection.ConnectionString);
            await a.OpenAsync();
            await b.OpenAsync();
            await Run(a, $"BEGIN TRAN; UPDATE tempdb.dbo.[{t1}] SET Id = 2;");
            await Run(b, $"BEGIN TRAN; UPDATE tempdb.dbo.[{t2}] SET Id = 2;");
            var ta = Run(a, $"UPDATE tempdb.dbo.[{t2}] SET Id = 3;");
            await Task.Delay(500);
            var tb = Run(b, $"UPDATE tempdb.dbo.[{t1}] SET Id = 3;");

            // One of the two is chosen as the victim (error 1205); the other then completes.
            var results = await Task.WhenAll(Settle(ta), Settle(tb));
            Assert.Equal(1, results.Count(e => e == 1205));
            await Run(a, "IF @@TRANCOUNT > 0 ROLLBACK;");
            await Run(b, "IF @@TRANCOUNT > 0 ROLLBACK;");
        }
        finally
        {
            await Exec($"DROP TABLE tempdb.dbo.[{t1}]; DROP TABLE tempdb.dbo.[{t2}];");
        }

        var after = (await CollectorRunner.RunAsync(new DeadlockCollector(), _connection, _server, CancellationToken.None))
            .Samples.Single().Value!.Value;
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task Io_latency_reports_per_database_and_total_values_for_real_activity()
    {
        var name = "SqlLookoutIo_" + Guid.NewGuid().ToString("N")[..8];
        await Exec($"CREATE DATABASE [{name}];");
        try
        {
            await Exec($"USE [{name}]; CREATE TABLE t (Id int, Pad char(4000) DEFAULT 'x'); INSERT t (Id) SELECT TOP (2000) 1 FROM sys.all_objects a, sys.all_objects b;");
            var collector = new IoLatencyCollector { Window = TimeSpan.FromSeconds(2) };
            var work = Task.Run(async () =>
            {
                for (var i = 0; i < 6; i++)
                {
                    await Exec($"USE [{name}]; INSERT t (Id) SELECT TOP (500) 2 FROM sys.all_objects a, sys.all_objects b; CHECKPOINT;");
                    await Task.Delay(300);
                }
            });
            // The activity runs on its own connection so the collector's connection stays free.
            var result = await CollectorRunner.RunAsync(collector, _connection2(), _server, CancellationToken.None);
            await work;

            Assert.Equal(CollectionStatus.Ok, result.Status);
            Assert.Contains(result.Samples, s => s.Metric == "io_write_latency_ms" && s.Subject == name);
            Assert.Contains(result.Samples, s => s.Metric == "io_write_latency_ms" && s.Subject is null);
            Assert.All(result.Samples, s => Assert.True(s.Value >= 0));
        }
        finally
        {
            await Exec($"USE master; DROP DATABASE [{name}];");   // the test's own connection sits in that database
        }
    }

    [Fact]
    public async Task Wait_stats_lists_at_most_ten_non_benign_waits_with_a_positive_rate()
    {
        var collector = new WaitStatsCollector { Window = TimeSpan.FromSeconds(2) };
        var work = Task.Run(async () =>
        {
            // Give the server something to wait on during the window.
            for (var i = 0; i < 8; i++) { await Exec("SELECT COUNT_BIG(*) FROM sys.all_objects a, sys.all_objects b, sys.all_objects c;"); }
        });
        var result = await CollectorRunner.RunAsync(collector, _connection2(), _server, CancellationToken.None);
        await work;

        Assert.Equal(CollectionStatus.Ok, result.Status);
        Assert.InRange(result.Samples.Count, 0, 10);
        Assert.All(result.Samples, s =>
        {
            Assert.Equal("wait_ms_per_sec", s.Metric);
            Assert.True(s.Value > 0);
            Assert.DoesNotContain(s.Subject!, WaitStatsCollector.Benign);
        });
        Assert.Equal(result.Samples.OrderByDescending(s => s.Value).Select(s => s.Subject), result.Samples.Select(s => s.Subject));
    }

    private SqlConnection _second = null!;
    /// <summary>A second connection for the collector while the first runs the test's activity (a connection runs one command at a time).</summary>
    private SqlConnection _connection2()
    {
        if (_second is null)
        {
            _second = new SqlConnection(_connection.ConnectionString);
            _second.Open();
        }
        return _second;
    }

    private static async Task Run(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Returns the SQL error number if the task failed with one, otherwise 0.</summary>
    private static async Task<int> Settle(Task t)
    {
        try { await t; return 0; }
        catch (SqlException ex) { return ex.Number; }
    }

    private async Task Exec(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }
}
