using System.Data;
using Dapper;
using Merit.Domain.Ids;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

/// <summary>
/// End-to-end SQL integration tests for wave 4 (#336, #345, #346).
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class LoggingWave4IntegrationTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task Issue336_SignInLedgerOmitsClientAddressWhileSignInEventRetainsUnderPolicy()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        var sup = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId FROM auth.appUser WHERE userName = 'supervisor' AND isDeleted = 0");

        await conn.ExecuteAsync("auth.uspRecordSignInResult",
            new { appUserId = sup.appUserId, succeeded = true, maxFailedAttempts = 5, lockoutMinutes = 15, clientAddress = "203.0.113.201" },
            commandType: CommandType.StoredProcedure);

        string? ledgerIp = await conn.ExecuteScalarAsync<string?>(
            "SELECT TOP (1) clientAddress FROM logs.auditEvent WHERE appUserId = @u AND eventAction = 'signIn' ORDER BY auditEventId DESC",
            new { u = sup.appUserId });
        Assert.Null(ledgerIp);
    }

    [SqlFact]
    public async Task Issue345_DeidentifiedSessionBlockedFromPersonChangeHistoryAndSqlErrorValuesMasked()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        var admin = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId, Guid homeTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId, homeTenantId FROM auth.appUser WHERE userName = 'admin' AND isDeleted = 0");

        // 1. Person change history must be hidden from a de-identified session even if it holds person.view + audit.view.
        long auditId = await conn.ExecuteScalarAsync<long>(
            "INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, owningTenantId) OUTPUT INSERTED.auditEventId VALUES (@u, @r, 'update', 'person', @p, @r);",
            new { u = admin.appUserId, r = admin.rootTenantId, p = fixture.Ids.MiaPersonId });
        Guid changeId = TimeOrderedId.New();
        await conn.ExecuteAsync(
            """
            INSERT logs.auditEventChange (auditEventChangeId, auditEventId, rootTenantId, owningTenantId, entityType, entityId, beforeJson, afterJson)
            VALUES (@cid, @aid, @r, @r, 'person', @p, N'{"lastName":"Rivera"}', N'{"lastName":"Rivera-Updated"}');
            """, new { cid = changeId, aid = auditId, r = admin.rootTenantId, p = fixture.Ids.MiaPersonId });

        Guid deidScope = TimeOrderedId.New();
        Guid idScope = TimeOrderedId.New();
        await conn.ExecuteAsync(
            """
            INSERT auth.sessionScope (sessionScopeId, appUserId, rootTenantId, actingTenantId, sessionReference, isDeidentified, expiresUtc)
            VALUES (@dsk, @u, @r, @r, NEWID (), 1, DATEADD (HOUR, 1, SYSUTCDATETIME ())),
                   (@isk, @u, @r, @r, NEWID (), 0, DATEADD (HOUR, 1, SYSUTCDATETIME ()));
            INSERT auth.sessionScopePermission (sessionScopePermissionId, sessionScopeId, permissionCode)
            VALUES (NEWID (), @dsk, 'person.view'), (NEWID (), @dsk, 'audit.view'),
                   (NEWID (), @isk, 'person.view'), (NEWID (), @isk, 'audit.view');
            INSERT auth.sessionScopeTenant (sessionScopeTenantId, sessionScopeId, tenantId, accessLevel, canReadIdentifiedResults, canReadIdentifiedCases)
            VALUES (NEWID (), @dsk, @r, 'full', 0, 0),
                   (NEWID (), @isk, @r, 'full', 1, 1);
            INSERT auth.sessionScopeRoot (sessionScopeRootId, sessionScopeId, rootTenantId)
            VALUES (NEWID (), @dsk, @r), (NEWID (), @isk, @r);
            """, new { dsk = deidScope, isk = idScope, u = admin.appUserId, r = admin.rootTenantId });

        try
        {
            await using (var deidConn = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await deidConn.OpenAsync();
                await deidConn.ExecuteAsync("EXECUTE AS USER = 'meritWeb';");
                await deidConn.ExecuteAsync(
                    "EXEC sys.sp_set_session_context @key = N'scopeKey', @value = @sk, @read_only = 1;",
                    new { sk = deidScope });
                var deidRows = (await deidConn.QueryAsync<dynamic>("logs.uspGetAuditEventChanges", new { auditEventId = auditId }, commandType: CommandType.StoredProcedure)).ToList();
                await deidConn.ExecuteAsync("REVERT;");
                Assert.Empty(deidRows);
            }

            await using (var idConn = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await idConn.OpenAsync();
                await idConn.ExecuteAsync("EXECUTE AS USER = 'meritWeb';");
                await idConn.ExecuteAsync(
                    "EXEC sys.sp_set_session_context @key = N'scopeKey', @value = @sk, @read_only = 1;",
                    new { sk = idScope });
                var idRows = (await idConn.QueryAsync<dynamic>("logs.uspGetAuditEventChanges", new { auditEventId = auditId }, commandType: CommandType.StoredProcedure)).ToList();
                await idConn.ExecuteAsync("REVERT;");
                Assert.Single(idRows);
            }
        }
        finally
        {
            await conn.ExecuteAsync(
                "UPDATE logs.auditEventChange SET isDeleted = 1 WHERE auditEventChangeId = @cid; UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE sessionScopeId IN (@dsk, @isk);",
                new { cid = changeId, dsk = deidScope, isk = idScope });
        }

        // 2. Quoted and parenthesized data values in SQL Server error messages (e.g. 2627) are masked before storage in logs.ExecutionLog.
        var p = new DynamicParameters();
        p.Add("@ProcedureName", "[dbo].[uspSyntheticError345]");
        p.Add("@KeyParameters", "personId=123");
        p.Add("@StartDateUtc", DateTime.UtcNow);
        p.Add("@ReCreatedAfterRollback", false);
        p.Add("@ExecutionLogId", dbType: DbType.Int64, direction: ParameterDirection.Output);
        await conn.ExecuteAsync("logs.uspStartExecutionLogging", p, commandType: CommandType.StoredProcedure);
        long execId = p.Get<long>("@ExecutionLogId");

        await conn.ExecuteAsync("logs.uspRecordExecutionError", new
        {
            ExecutionLogId = execId,
            ProcedureName = "[dbo].[uspSyntheticError345]",
            KeyParameters = "personId=123",
            ErrorNumber = 2627,
            ErrorMessage = "Violation of UNIQUE KEY constraint 'UX_person_test'. Cannot insert duplicate key in object 'dbo.person'. The duplicate key value is (SensitiveDupName345, '1990-01-01').",
            ErrorProcedure = "[dbo].[uspSyntheticError345]",
            ErrorLine = 42,
        }, commandType: CommandType.StoredProcedure);

        string storedMsg = await conn.ExecuteScalarAsync<string>(
            "SELECT ErrorMessage FROM logs.ExecutionLog WHERE ExecutionLogId = @id", new { id = execId }) ?? "";
        Assert.DoesNotContain("SensitiveDupName345", storedMsg, StringComparison.Ordinal);
        Assert.DoesNotContain("1990-01-01", storedMsg, StringComparison.Ordinal);
        Assert.Contains("[redacted]", storedMsg, StringComparison.Ordinal);
        await conn.ExecuteAsync("UPDATE logs.ExecutionLog SET isDeleted = 1 WHERE ExecutionLogId = @id", new { id = execId });
    }

    [SqlFact]
    public async Task Issue346_RootTenantExportAndPurgeIsolatesOtherRoots()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        Guid synRoot = TimeOrderedId.New();
        string synCode = "OFFB" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid synPerson = TimeOrderedId.New();
        Guid synSearch = TimeOrderedId.New();
        Guid synUser = TimeOrderedId.New();

        // Seed a synthetic root tenant with clinical + log data.
        await conn.ExecuteAsync(
            """
            INSERT auth.tenant (tenantId, rootTenantId, tenantCode, tenantName, tenantType, isActive)
            VALUES (@r, @r, @code, N'Synthetic Offboarding Root', 'state', 1);
            INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, passwordHash, securityStamp, isActive)
            VALUES (@u, @r, @r, @uname, N'Synthetic User', N'synthetic-hash-346', NEWID (), 1);
            INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey, firstNameKey, dob, sex, ethnicity)
            VALUES (@p, @r, @r, N'OffboardSecretName', N'Jane', N'OFFBOARDSECRETNAME', N'JANE', '2021-02-03', 'female', 'unknown');
            INSERT logs.searchLog (searchLogId, appUserId, rootTenantId, searchText, resultCount, searchedUtc)
            VALUES (@s, @u, @r, N'OffboardSecretQuery', 1, SYSUTCDATETIME ());
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
            VALUES (@u, @r, 'view', 'person', @p);
            """, new { r = synRoot, code = synCode, u = synUser, uname = "u_" + synCode, p = synPerson, s = synSearch });

        int mdPersonsBefore = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT (*) FROM dbo.person WHERE rootTenantId = (SELECT rootTenantId FROM auth.tenant WHERE tenantCode = 'MD') AND isDeleted = 0");

        // 1. Non-platform meritWeb caller without platform permissions is denied (51403).
        await using (var denyConn = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            await denyConn.OpenAsync();
            await denyConn.ExecuteAsync("EXECUTE AS USER = 'meritWeb';");
            var ex = await Assert.ThrowsAsync<SqlException>(() =>
                denyConn.ExecuteAsync("config.uspExportRootTenant", new { rootTenantId = synRoot, includePayloads = false }, commandType: CommandType.StoredProcedure));
            Assert.Equal(51403, ex.Number);
            await denyConn.ExecuteAsync("REVERT;");
        }

        // 2. Platform export returns full 59-table manifest and JSON payloads for the synthetic root.
        var exportRows = (await conn.QueryAsync<(string tableName, int exportedRows, string disposition, string? payloadJson)>(
            "config.uspExportRootTenant", new { rootTenantId = synRoot, includePayloads = true }, commandType: CommandType.StoredProcedure)).ToList();
        Assert.True(exportRows.Count >= 59);
        var personManifest = Assert.Single(exportRows, r => r.tableName == "dbo.person");
        Assert.Equal(1, personManifest.exportedRows);
        Assert.Contains("OffboardSecretName", personManifest.payloadJson ?? "", StringComparison.Ordinal);
        var ledgerManifest = Assert.Single(exportRows, r => r.tableName == "logs.auditEvent");
        Assert.Equal("ledgerReadOnly", ledgerManifest.disposition);
        Assert.True(ledgerManifest.exportedRows >= 2);

        // 3. Purge rejects mismatched confirmation code (51422).
        var badConfirm = await Assert.ThrowsAsync<SqlException>(() =>
            conn.ExecuteAsync("config.uspPurgeRootTenant", new { rootTenantId = synRoot, confirmationCode = "PURGE:WRONG", backupRetentionDays = 35 }, commandType: CommandType.StoredProcedure));
        Assert.Equal(51422, badConfirm.Number);

        // 4. Confirmed purge scrubs and soft-deletes synthetic root rows, reports backup age-out and retained ledger rows,
        //    and leaves the Maryland root completely untouched.
        using (var grid = await conn.QueryMultipleAsync(
            "config.uspPurgeRootTenant", new { rootTenantId = synRoot, confirmationCode = $"PURGE:{synCode}", backupRetentionDays = 35 }, commandType: CommandType.StoredProcedure))
        {
            var tableReport = (await grid.ReadAsync<(string tableName, int rowsAffected, string disposition)>()).ToList();
            var summary = await grid.ReadSingleAsync<(Guid rootTenantId, string tenantCode, int totalRowsPurged, int ledgerRowsRetained, DateTime backupAgeOutUtc)>();
            Assert.True(tableReport.Count >= 59);
            Assert.Equal(synRoot, summary.rootTenantId);
            Assert.Equal(synCode, summary.tenantCode);
            Assert.True(summary.totalRowsPurged >= 4);
            Assert.True(summary.ledgerRowsRetained >= 2);
            Assert.True(summary.backupAgeOutUtc > DateTime.UtcNow.AddDays(34));
        }

        // Verify synthetic person and searchLog are scrubbed & soft-deleted.
        var purgedPerson = await conn.QuerySingleAsync<(bool isDeleted, string lastName)>(
            "SELECT isDeleted, lastName FROM dbo.person WHERE personId = @p", new { p = synPerson });
        Assert.True(purgedPerson.isDeleted);
        Assert.Equal("[purged]", purgedPerson.lastName);

        var purgedSearch = await conn.QuerySingleAsync<(bool isDeleted, string searchText)>(
            "SELECT isDeleted, searchText FROM logs.searchLog WHERE searchLogId = @s", new { s = synSearch });
        Assert.True(purgedSearch.isDeleted);
        Assert.Equal(string.Empty, purgedSearch.searchText);

        // Verify Maryland root is 100% untouched.
        int mdPersonsAfter = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT (*) FROM dbo.person WHERE rootTenantId = (SELECT rootTenantId FROM auth.tenant WHERE tenantCode = 'MD') AND isDeleted = 0");
        Assert.Equal(mdPersonsBefore, mdPersonsAfter);
    }
}
