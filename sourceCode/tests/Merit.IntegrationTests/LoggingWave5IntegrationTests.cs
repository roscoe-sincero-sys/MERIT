using System.Data;
using Dapper;
using Merit.Application.Data;
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
        await using var adminConn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await adminConn.OpenAsync();
        DateTime testStartUtc = await adminConn.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME ()");
        var recordedRunIds = new List<Guid>();
        Guid? createdFindingId = null;

        try
        {
            await using var workerConn = new SqlConnection(TestEnvironment.AdminConnectionString());
            await workerConn.OpenAsync();
            await workerConn.ExecuteAsync("EXECUTE AS USER = 'meritWorker';");
            try
            {
                static async Task<(bool verified, long? nativeBlockId, string? digestJson, DateTime verifiedUtc, string detail, bool hasAuto)>
                    RunVerifyAsync(SqlConnection c, string? digestJson, bool useAutomaticStorage = false, bool requireDigest = false)
                {
                    using var grid = await c.QueryMultipleAsync(
                        "logs.uspVerifyAuditLedger", new { digestJson, useAutomaticStorage, requireDigest }, commandType: CommandType.StoredProcedure);
                    long? nativeBlockId = null;
                    (bool verified, long? nativeBlockId, string? digestJson, DateTime verifiedUtc, string detail, bool hasAuto) last = default;
                    while (!grid.IsConsumed)
                    {
                        var rows = (await grid.ReadAsync<dynamic>()).ToList();
                        if (rows.Count > 0 && rows[0] is IDictionary<string, object> dict)
                        {
                            if (dict.TryGetValue("last_verified_block_id", out object? nb) && nb is not null)
                            {
                                nativeBlockId = Convert.ToInt64(nb);
                            }
                            else if (dict.ContainsKey("verified"))
                            {
                                var r = rows[0];
                                last = ((bool)r.verified, nativeBlockId, (string?)r.digestJson, (DateTime)r.verifiedUtc, (string)r.detail, (bool)r.hasAutomaticDigestStorage);
                            }
                        }
                    }
                    return last;
                }

                // Metadata reader runs cleanly as meritWorker.
                var state = await workerConn.QuerySingleAsync<(bool hasAutomaticDigestStorage, int? lastGoodDigestFileCount)>(
                    "logs.uspGetLedgerVerificationState", commandType: CommandType.StoredProcedure);
                Assert.False(state.hasAutomaticDigestStorage);

                var fresh = await RunVerifyAsync(workerConn, null);
                Assert.True(fresh.verified, fresh.detail);
                Assert.NotNull(fresh.nativeBlockId);
                Assert.NotNull(fresh.digestJson);
                Assert.Contains("MERIT_IT", fresh.digestJson, StringComparison.OrdinalIgnoreCase);

                // Re-verify using the previously returned external digest JSON with requireDigest=true.
                var recheck = await RunVerifyAsync(workerConn, fresh.digestJson, requireDigest: true);
                Assert.True(recheck.verified, recheck.detail);
                Assert.Equal(fresh.nativeBlockId, recheck.nativeBlockId);

                // Empty digest with requireDigest=true must fail rather than generating a fresh digest, with no native block.
                var emptyReq = await RunVerifyAsync(workerConn, "", requireDigest: true);
                Assert.False(emptyReq.verified);
                Assert.Null(emptyReq.nativeBlockId);

                // #354: Record failure outcome as meritWorker -> creates critical finding, surfaces in pending outbox,
                // clears outbox once marked delivered, and deduplicates repeat same-day failures.
                var failOutcome = await workerConn.QuerySingleAsync<(Guid ledgerVerificationRunId, bool findingCreated, Guid? securityFindingId, Guid? rootTenantId, DateTime detectedUtc)>(
                    "logs.uspRecordLedgerVerificationOutcome",
                    new { anchorSource = "digestDirectory", verified = false, lastVerifiedBlockId = (long?)null, digestFileCount = 1, detail = "Synthetic #354 failure for outbox test." },
                    commandType: CommandType.StoredProcedure);
                recordedRunIds.Add(failOutcome.ledgerVerificationRunId);
                Assert.True(failOutcome.findingCreated);
                Assert.NotNull(failOutcome.securityFindingId);
                Assert.NotEqual(Guid.Empty, failOutcome.securityFindingId.Value);
                Assert.NotNull(failOutcome.rootTenantId);
                Assert.NotEqual(Guid.Empty, failOutcome.rootTenantId.Value);
                createdFindingId = failOutcome.securityFindingId;

                var pendingBefore = (await workerConn.QueryAsync<NewSecurityFindingRow>(
                    "logs.uspGetPendingLedgerAlerts", commandType: CommandType.StoredProcedure)).ToList();
                Assert.Contains(pendingBefore, p => p.SecurityFindingId == createdFindingId && p.RuleCode == "ledgerVerificationFailed" && p.Severity == "critical");

                await workerConn.ExecuteAsync(
                    "logs.uspMarkSecurityFindingWebhookDelivered",
                    new { securityFindingId = createdFindingId.Value },
                    commandType: CommandType.StoredProcedure);

                var pendingAfter = (await workerConn.QueryAsync<NewSecurityFindingRow>(
                    "logs.uspGetPendingLedgerAlerts", commandType: CommandType.StoredProcedure)).ToList();
                Assert.DoesNotContain(pendingAfter, p => p.SecurityFindingId == createdFindingId);

                // Second failure on the same UTC day deduplicates cleanly (no second finding created).
                var repeatFail = await workerConn.QuerySingleAsync<(Guid ledgerVerificationRunId, bool findingCreated, Guid? securityFindingId, Guid? rootTenantId, DateTime detectedUtc)>(
                    "logs.uspRecordLedgerVerificationOutcome",
                    new { anchorSource = "digestDirectory", verified = false, lastVerifiedBlockId = (long?)null, digestFileCount = 1, detail = "Synthetic #354 repeat failure." },
                    commandType: CommandType.StoredProcedure);
                recordedRunIds.Add(repeatFail.ledgerVerificationRunId);
                Assert.False(repeatFail.findingCreated);

                // Record success outcome as meritWorker using the engine's native verified block id.
                var recorded = await workerConn.QuerySingleAsync<(Guid ledgerVerificationRunId, bool findingCreated, Guid? securityFindingId, Guid? rootTenantId, DateTime detectedUtc)>(
                    "logs.uspRecordLedgerVerificationOutcome",
                    new { anchorSource = "digestDirectory", verified = true, lastVerifiedBlockId = fresh.nativeBlockId, digestFileCount = 1, detail = fresh.detail },
                    commandType: CommandType.StoredProcedure);
                recordedRunIds.Add(recorded.ledgerVerificationRunId);
                Assert.False(recorded.findingCreated);
            }
            finally
            {
                await workerConn.ExecuteAsync("REVERT;");
            }

            // Verify operational logs returns DDL checks (result set 3) separately from LedgerVerification (result set 4).
            using var opGrid = await adminConn.QueryMultipleAsync("logs.uspGetOperationalLogs", new { hours = 24, top = 10 }, commandType: CommandType.StoredProcedure);
            _ = await opGrid.ReadAsync<dynamic>();
            _ = await opGrid.ReadAsync<dynamic>();
            var ddlChecks = (await opGrid.ReadAsync<(string CheckName, string Status, string? Detail)>()).ToList();
            Assert.DoesNotContain(ddlChecks, c => c.CheckName == "auditLedgerVerification");
            var ledgerStatus = await opGrid.ReadSingleOrDefaultAsync<(DateTime? verifiedUtc, string anchorSource, string status, long? lastVerifiedBlockId, int? digestFileCount, string detail)>();
            Assert.Equal("OK", ledgerStatus.status);
            Assert.Equal("digestDirectory", ledgerStatus.anchorSource);
        }
        finally
        {
            // Scope cleanup strictly to the synthetic runs and finding created by this test.
            foreach (Guid rid in recordedRunIds)
            {
                await adminConn.ExecuteAsync("UPDATE logs.ledgerVerificationRun SET isDeleted = 1 WHERE ledgerVerificationRunId = @id;", new { id = rid });
            }
            if (createdFindingId is { } fid)
            {
                await adminConn.ExecuteAsync("UPDATE logs.securityFinding SET isDeleted = 1 WHERE securityFindingId = @id;", new { id = fid });
            }
            await adminConn.ExecuteAsync(
                "UPDATE logs.securityFinding SET isDeleted = 1 WHERE ruleCode = 'ledgerVerificationFailed' AND isDeleted = 0 AND detectedUtc >= @since AND detailJson LIKE N'%Synthetic #354%';",
                new { since = testStartUtc });
        }
    }
}
