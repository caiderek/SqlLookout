using Microsoft.Data.SqlClient;
using SqlLookout.Service;

namespace SqlLookout.Tests;

/// <summary>The store check of the real setup host against a real SQL Server: a database that does not exist yet is fine.</summary>
[Collection(SqlServerTestDb.LocalServerCollection)]
public class StoreCheckTests
{
    private static string Server =>
        Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_SQL") ?? "Server=.;Integrated Security=true;TrustServerCertificate=true";

    private static string With(string database) => new SqlConnectionStringBuilder(Server) { InitialCatalog = database }.ConnectionString;

    private static async Task<bool> ExistsAsync(string name)
    {
        await using var connection = new SqlConnection(Server);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DB_ID(@n);";
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync() is not (null or DBNull);
    }

    [Fact]
    public async Task A_missing_database_passes_the_test_without_being_created_and_is_created_on_install()
    {
        var host = new WindowsSetupHost();
        var name = "SqlLookoutCheck_" + Guid.NewGuid().ToString("N");
        try
        {
            var test = await host.CheckStoreAsync(With(name), create: false);
            Assert.Null(test.Failure);
            Assert.True(test.WillCreate);
            Assert.False(await ExistsAsync(name));

            var install = await host.CheckStoreAsync(With(name), create: true);
            Assert.Null(install.Failure);
            Assert.True(await ExistsAsync(name));

            var again = await host.CheckStoreAsync(With(name), create: true);
            Assert.Null(again.Failure);
            Assert.False(again.WillCreate);
        }
        finally
        {
            await using var connection = new SqlConnection(Server);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task A_wrong_server_gives_a_failure_text()
    {
        var bad = new SqlConnectionStringBuilder(Server) { InitialCatalog = "X", UserID = "nobody", Password = "x", IntegratedSecurity = false }.ConnectionString;
        var check = await new WindowsSetupHost().CheckStoreAsync(bad, create: false);
        Assert.NotNull(check.Failure);
    }
}
