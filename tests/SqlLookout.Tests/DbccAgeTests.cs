using Microsoft.Data.SqlClient;
using SqlLookout.Core;
using SqlLookout.Core.Collectors;

namespace SqlLookout.Tests;

/// <summary>
/// The age value only proves itself once real time has passed, so this waits a little over a minute.
/// Separate class so xunit runs it in parallel with the rest of the suite.
/// </summary>
public sealed class DbccAgeTests
{
    [Fact]
    public async Task Age_is_measured_in_minutes_since_the_last_good_checkdb()
    {
        var cs = Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_CONNECTION")
                 ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";
        var name = "SqlLookoutAge_" + Guid.NewGuid().ToString("N")[..8];
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        var server = await ServerInfo.ReadAsync(connection);

        await Exec(connection, $"CREATE DATABASE [{name}];");
        try
        {
            await Exec(connection, $"DBCC CHECKDB([{name}]) WITH NO_INFOMSGS;");
            await Task.Delay(TimeSpan.FromSeconds(65));

            var result = await CollectorRunner.RunAsync(new DbccLastGoodCollector(), connection, server, CancellationToken.None);
            var age = result.Samples.Single(s => s.Subject == name).Value;

            Assert.NotNull(age);
            Assert.InRange(age!.Value, 1, 3);   // at least one whole minute has passed, and not much more
        }
        finally
        {
            await Exec(connection, $"DROP DATABASE [{name}];");
        }
    }

    private static async Task Exec(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }
}
