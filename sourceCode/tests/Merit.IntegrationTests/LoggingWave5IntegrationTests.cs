using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>
/// End-to-end SQL integration tests for wave 5 (#334 ExecutionLog retention &amp; ledger verification, #352 single-pass purge).
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class LoggingWave5IntegrationTests
{
    [SqlFact]
    public async Task Issue334_ExecutionLogPurgerDeletesExpiredRowsAndRetainsRecentRows()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        // Seed one expired row (200 days old) and one recent row (1 day old).
        long oldId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT logs.ExecutionLog (ProcedureName, KeyParameters, StartDateUtc, EndDateUtc, ElapsedMilliseconds, Successful)
            OUTPUT INSERTED.ExecutionLogId
            VALUES (N'[dbo].[uspSyntheticOld334]', N'test=1', DATEADD (DAY, -200, SYSUTCDATETIME ()), DATEADD (DAY, -200, SYSUTCDATETIME ()), 1, 1);
            """);
        long recentId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT logs.ExecutionLog (ProcedureName, KeyParameters, StartDateUtc, EndDateUtc, ElapsedMilliseconds, Successful)
            OUTPUT INSERTED.ExecutionLogId
            VALUES (N'[dbo].[uspSyntheticRecent334]', N'test=2', DATEADD (DAY, -1, SYSUTCDATETIME ()), DATEADD (DAY, -1, SYSUTCDATETIME ()), 1, 1);
            """);

        try
        {
            // Verify clamping rejection (< 30 days -> 51422) and execution as meritWorker.
            await using var workerConn = new SqlConnection(TestEnvironment.AdminConnectionString());
            await workerConn.OpenAsync();
            await workerConn.ExecuteAsync("EXECUTE AS USER = 'meritWorker';");
            try
            {
                var clampEx = await Assert.ThrowsAsync<SqlException>(() =>
                    workerConn.ExecuteAsync("logs.uspPurgeExecutionLog", new { retentionDays = 10 }, commandType: CommandType.StoredProcedure));
                Assert.Equal(51422, clampEx.Number);

                int purged = await workerConn.QuerySingleAsync<int>(
                    "logs.uspPurgeExecutionLog", new { retentionDays = 90 }, commandType: CommandType.StoredProcedure);
                Assert.True(purged >= 1);
            }
            finally
            {
                await workerConn.ExecuteAsync("REVERT;");
            }

            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM logs.ExecutionLog WHERE ExecutionLogId = @id", new { id = oldId }));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM logs.ExecutionLog WHERE ExecutionLogId = @id", new { id = recentId }));
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM logs.ExecutionLog WHERE ExecutionLogId IN (@a, @b);", new { a = oldId, b = recentId });
        }
    }

    [SqlFact]
    public async Task Issue334_AuditLedgerDigestGeneratesAndVerifiesAsMeritWorker()
    {
        await using var workerConn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await workerConn.OpenAsync();
        await workerConn.ExecuteAsync("EXECUTE AS USER = 'meritWorker';");
        try
        {
            static async Task<(bool verified, long? lastVerifiedBlockId, string? digestJson, DateTime verifiedUtc, string detail)>
                RunVerifyAsync(SqlConnection c, string? digestJson)
            {
                using var grid = await c.QueryMultipleAsync(
                    "logs.uspVerifyAuditLedger", new { digestJson }, commandType: CommandType.StoredProcedure);
                (bool verified, long? lastVerifiedBlockId, string? digestJson, DateTime verifiedUtc, string detail) last = default;
                while (!grid.IsConsumed)
                {
                    var rows = (await grid.ReadAsync<dynamic>()).ToList();
                    if (rows.Count > 0 && ((IDictionary<string, object>)rows[0]).ContainsKey("verified"))
                    {
                        var r = rows[0];
                        last = ((bool)r.verified, (long?)r.lastVerifiedBlockId, (string?)r.digestJson, (DateTime)r.verifiedUtc, (string)r.detail);
                    }
                }
                return last;
            }

            var fresh = await RunVerifyAsync(workerConn, null);
            Assert.True(fresh.verified, fresh.detail);
            Assert.NotNull(fresh.digestJson);
            Assert.Contains("MERIT_IT", fresh.digestJson, StringComparison.OrdinalIgnoreCase);

            // Re-verify using the previously returned external digest JSON.
            var recheck = await RunVerifyAsync(workerConn, fresh.digestJson);
            Assert.True(recheck.verified, recheck.detail);
            Assert.Equal(fresh.lastVerifiedBlockId, recheck.lastVerifiedBlockId);
        }
        finally
        {
            await workerConn.ExecuteAsync("REVERT;");
        }
    }
}
