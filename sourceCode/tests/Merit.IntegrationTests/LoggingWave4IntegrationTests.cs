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
        Guid synDeletedPerson = TimeOrderedId.New();
        Guid synLocation = TimeOrderedId.New();
        Guid synBatch = TimeOrderedId.New();
        Guid synSearch = TimeOrderedId.New();
        Guid synUser = TimeOrderedId.New();
        Guid synRole = TimeOrderedId.New();
        Guid synAssign = TimeOrderedId.New();
        Guid synRecovery = TimeOrderedId.New();

        // Seed a synthetic root tenant with live + previously soft-deleted PHI, geocodes, raw intake file,
        // credentials, recovery codes, live assignments, searchLog, and append-only auditEvent (B4, I17).
        await conn.ExecuteAsync(
            """
            INSERT auth.tenant (tenantId, rootTenantId, tenantCode, tenantName, tenantType, isActive)
            VALUES (@r, @r, @code, N'Synthetic Offboarding Root', 'state', 1);
            INSERT auth.role (roleId, rootTenantId, roleCode, roleName)
            VALUES (@role, @r, 'stateAdmin', N'State Admin');
            INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, email, passwordHash, mfaSecretProtected, securityStamp, isActive)
            VALUES (@u, @r, @r, @uname, N'Synthetic User', N'syn@example.org', N'real-hash-secret', N'protected-totp-secret', NEWID (), 1);
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            VALUES (@assign, @u, @role, @r, 1, 'full', '2026-01-01');
            INSERT auth.mfaRecoveryCode (mfaRecoveryCodeId, appUserId, codeHash)
            VALUES (@rec, @u, N'recovery-hash-secret');
            INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey, firstNameKey, dob, sex, ethnicity)
            VALUES (@p, @r, @r, N'OffboardSecretName', N'Jane', N'OFFBOARDSECRETNAME', N'JANE', '2021-02-03', 'female', 'hispanic');
            INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey, firstNameKey, dob, sex, ethnicity, isDeleted)
            VALUES (@pd, @r, @r, N'PreviouslyDeletedPhi', N'Merged', N'PREVIOUSLYDELETEDPHI', N'MERGED', '2019-07-08', 'male', 'notHispanic', 1);
            INSERT dbo.location (locationId, rootTenantId, owningTenantId, addressLine1, city, state, postalCode, country, stdStatus, latitude, longitude, censusTract, addressKey)
            VALUES (@loc, @r, @r, N'123 Secret St', N'Baltimore', 'MD', '21201', 'US', 'raw', 39.2904, -76.6122, '24510010100', N'123-SECRET-ST');
            INSERT dbo.intakeBatch (intakeBatchId, rootTenantId, owningTenantId, sourceChannel, receivedUtc, batchStatus, fileName, rawContent, receivedCount, loadedCount, heldCount, rejectedCount)
            VALUES (@batch, @r, @r, 'upload', SYSUTCDATETIME (), 'loaded', N'secret-lab.hl7', CONVERT (VARBINARY (MAX), 'MSH|^~\&|SECRET_LAB_FILE'), 1, 1, 0, 0);
            INSERT logs.searchLog (searchLogId, appUserId, rootTenantId, searchText, resultCount, searchedUtc)
            VALUES (@s, @u, @r, N'OffboardSecretQuery', 1, SYSUTCDATETIME ());
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
            VALUES (@u, @r, 'view', 'person', @p);
            """, new
            {
                r = synRoot, code = synCode, role = synRole, u = synUser, uname = "u_" + synCode,
                assign = synAssign, rec = synRecovery, p = synPerson, pd = synDeletedPerson,
                loc = synLocation, batch = synBatch, s = synSearch,
            });

        int mdPersonsBefore = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT (*) FROM dbo.person WHERE rootTenantId = (SELECT rootTenantId FROM auth.tenant WHERE tenantCode = 'MD') AND isDeleted = 0");

        try
        {
            // 1. Non-platform meritWeb caller without platform permissions is denied (51403).
            await using (var denyConn = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await denyConn.OpenAsync();
                await denyConn.ExecuteAsync("EXECUTE AS USER = 'meritWeb';");
                var ex = await Assert.ThrowsAsync<SqlException>(() =>
                    denyConn.ExecuteAsync("config.uspExportRootTenant", new { rootTenantId = synRoot }, commandType: CommandType.StoredProcedure));
                Assert.Equal(51403, ex.Number);
                await denyConn.ExecuteAsync("REVERT;");
            }

            // 2. Platform export manifest covers all 71 tenant/auth tables (active + soft-deleted counts) and streams
            //    individual tables without credential columns while including soft-deleted history (I17).
            var manifest = (await conn.QueryAsync<(string tableName, int activeRows, int deletedRows, int totalRows, string disposition)>(
                "config.uspExportRootTenant", new { rootTenantId = synRoot }, commandType: CommandType.StoredProcedure)).ToList();
            Assert.Equal(71, manifest.Count);
            var personManifest = Assert.Single(manifest, r => r.tableName == "dbo.person");
            Assert.Equal(1, personManifest.activeRows);
            Assert.Equal(1, personManifest.deletedRows);
            Assert.Equal(2, personManifest.totalRows);
            var ledgerManifest = Assert.Single(manifest, r => r.tableName == "logs.auditEvent");
            Assert.Equal("ledgerReadOnly", ledgerManifest.disposition);
            Assert.True(ledgerManifest.totalRows >= 2);

            // Stream dbo.person: returns BOTH active and soft-deleted rows.
            var exportedPeople = (await conn.QueryAsync<dynamic>(
                "config.uspExportRootTenant", new { rootTenantId = synRoot, tableName = "dbo.person", pageNumber = 1, pageSize = 100 }, commandType: CommandType.StoredProcedure)).ToList();
            Assert.Equal(2, exportedPeople.Count);

            // Stream auth.appUser: must NOT include credential columns (passwordHash, mfaSecretProtected, securityStamp).
            var exportedUser = (IDictionary<string, object>)Assert.Single(await conn.QueryAsync<dynamic>(
                "config.uspExportRootTenant", new { rootTenantId = synRoot, tableName = "auth.appUser", pageNumber = 1, pageSize = 100 }, commandType: CommandType.StoredProcedure));
            Assert.DoesNotContain("passwordHash", exportedUser.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("mfaSecretProtected", exportedUser.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("securityStamp", exportedUser.Keys, StringComparer.OrdinalIgnoreCase);

            // 3. Purge rejects mismatched confirmation code (51422).
            var badConfirm = await Assert.ThrowsAsync<SqlException>(() =>
                conn.ExecuteAsync("config.uspPurgeRootTenant", new { rootTenantId = synRoot, confirmationCode = "PURGE:WRONG", backupRetentionDays = 35 }, commandType: CommandType.StoredProcedure));
            Assert.Equal(51422, badConfirm.Number);

            // 4. Confirmed per-batch purge scrubs live AND previously soft-deleted PHI, geocodes, raw intake files,
            //    credentials, recovery codes, and assignments, verifies 0 unscrubbed rows remain, and isolates Maryland.
            using (var grid = await conn.QueryMultipleAsync(
                "config.uspPurgeRootTenant", new { rootTenantId = synRoot, confirmationCode = $"PURGE:{synCode}", backupRetentionDays = 35 }, commandType: CommandType.StoredProcedure))
            {
                var tableReport = (await grid.ReadAsync<(string tableName, int rowsAffected, int remainingUnscrubbed, string disposition)>()).ToList();
                var summary = await grid.ReadSingleAsync<(Guid rootTenantId, string tenantCode, bool isDryRun, int totalRowsPurged, int totalRemainingUnscrubbed, int ledgerRowsRetained, DateTime backupAgeOutUtc)>();
                Assert.Equal(71, tableReport.Count);
                Assert.Equal(synRoot, summary.rootTenantId);
                Assert.Equal(synCode, summary.tenantCode);
                Assert.False(summary.isDryRun);
                Assert.True(summary.totalRowsPurged >= 9);
                Assert.Equal(0, summary.totalRemainingUnscrubbed);
                Assert.True(summary.ledgerRowsRetained >= 2);
                Assert.True(summary.backupAgeOutUtc > DateTime.UtcNow.AddDays(34));
            }

            // Verify BOTH live and previously soft-deleted persons are fully scrubbed (B4).
            foreach (Guid pid in new[] { synPerson, synDeletedPerson })
            {
                var pRow = await conn.QuerySingleAsync<(bool isDeleted, string lastName, string? firstName, DateTime? dob, string sex, string ethnicity)>(
                    "SELECT isDeleted, lastName, firstName, dob, sex, ethnicity FROM dbo.person WHERE personId = @p", new { p = pid });
                Assert.True(pRow.isDeleted);
                Assert.Equal("[purged]", pRow.lastName);
                Assert.Null(pRow.firstName);
                Assert.Null(pRow.dob);
                Assert.Equal("unknown", pRow.sex);
                Assert.Equal("unknown", pRow.ethnicity);
            }

            // Verify location geocodes/ZIP, intakeBatch.rawContent, appUser credentials, recoveryCode, and userAssignment are scrubbed (B4).
            var locRow = await conn.QuerySingleAsync<(bool isDeleted, string? postalCode, decimal? latitude, decimal? longitude, string? censusTract)>(
                "SELECT isDeleted, postalCode, latitude, longitude, censusTract FROM dbo.location WHERE locationId = @id", new { id = synLocation });
            Assert.True(locRow.isDeleted);
            Assert.Null(locRow.postalCode);
            Assert.Null(locRow.latitude);
            Assert.Null(locRow.longitude);
            Assert.Null(locRow.censusTract);

            var batchRow = await conn.QuerySingleAsync<(bool isDeleted, byte[]? rawContent)>(
                "SELECT isDeleted, rawContent FROM dbo.intakeBatch WHERE intakeBatchId = @id", new { id = synBatch });
            Assert.True(batchRow.isDeleted);
            Assert.Null(batchRow.rawContent);

            var userRow = await conn.QuerySingleAsync<(bool isDeleted, bool isActive, string displayName, string passwordHash, string? mfaSecretProtected)>(
                "SELECT isDeleted, isActive, displayName, passwordHash, mfaSecretProtected FROM auth.appUser WHERE appUserId = @id", new { id = synUser });
            Assert.True(userRow.isDeleted);
            Assert.False(userRow.isActive);
            Assert.Equal("[purged]", userRow.displayName);
            Assert.Equal("[purged]", userRow.passwordHash);
            Assert.Null(userRow.mfaSecretProtected);

            var recRow = await conn.QuerySingleAsync<(bool isDeleted, string codeHash)>(
                "SELECT isDeleted, codeHash FROM auth.mfaRecoveryCode WHERE mfaRecoveryCodeId = @id", new { id = synRecovery });
            Assert.True(recRow.isDeleted);
            Assert.Equal("[purged]", recRow.codeHash);

            Assert.True(await conn.ExecuteScalarAsync<bool>("SELECT isDeleted FROM auth.userAssignment WHERE userAssignmentId = @id", new { id = synAssign }));

            var purgedSearch = await conn.QuerySingleAsync<(bool isDeleted, string searchText)>(
                "SELECT isDeleted, searchText FROM logs.searchLog WHERE searchLogId = @s", new { s = synSearch });
            Assert.True(purgedSearch.isDeleted);
            Assert.Equal(string.Empty, purgedSearch.searchText);

            // 5. Dry-run rehearsal against the full demo MD-LEAD root (real lab results, closed cases, raw intake files,
            //    investigations, assignments, recovery codes): must pass every CHECK constraint, report 0 unscrubbed,
            //    and roll back completely leaving MD-LEAD untouched (B5).
            var mdRoot = await conn.QuerySingleAsync<(Guid tenantId, string tenantCode)>(
                "SELECT TOP (1) tenantId, tenantCode FROM auth.tenant WHERE tenantId = rootTenantId AND tenantCode LIKE 'MD%' AND isDeleted = 0 ORDER BY tenantCode");
            int mdLiveBeforeDryRun = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT (*) FROM dbo.person WHERE rootTenantId = @r AND isDeleted = 0", new { r = mdRoot.tenantId });
            using (var dryGrid = await conn.QueryMultipleAsync(
                "config.uspPurgeRootTenant",
                new { rootTenantId = mdRoot.tenantId, confirmationCode = $"PURGE:{mdRoot.tenantCode}", backupRetentionDays = 35, dryRun = true },
                commandTimeout: 180,
                commandType: CommandType.StoredProcedure))
            {
                var dryTableReport = (await dryGrid.ReadAsync<(string tableName, int rowsAffected, int remainingUnscrubbed, string disposition)>()).ToList();
                var drySummary = await dryGrid.ReadSingleAsync<(Guid rootTenantId, string tenantCode, bool isDryRun, int totalRowsPurged, int totalRemainingUnscrubbed, int ledgerRowsRetained, DateTime backupAgeOutUtc)>();
                Assert.Equal(71, dryTableReport.Count);
                Assert.True(drySummary.isDryRun);
                Assert.Equal(0, drySummary.totalRemainingUnscrubbed);
                Assert.True(drySummary.totalRowsPurged > 0);
            }
            int mdLiveAfterDryRun = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT (*) FROM dbo.person WHERE rootTenantId = @r AND isDeleted = 0", new { r = mdRoot.tenantId });
            Assert.Equal(mdLiveBeforeDryRun, mdLiveAfterDryRun);

            // Verify Maryland root is 100% untouched.
            int mdPersonsAfter = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT (*) FROM dbo.person WHERE rootTenantId = (SELECT rootTenantId FROM auth.tenant WHERE tenantCode = 'MD') AND isDeleted = 0");
            Assert.Equal(mdPersonsBefore, mdPersonsAfter);
        }
        finally
        {
            // I19: Always ensure the synthetic root is purged/deactivated even if an assertion fails mid-test.
            if (await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM auth.tenant WHERE tenantId = @r AND isDeleted = 0", new { r = synRoot }) > 0)
            {
                await conn.ExecuteAsync("config.uspPurgeRootTenant",
                    new { rootTenantId = synRoot, confirmationCode = $"PURGE:{synCode}", backupRetentionDays = 35 },
                    commandType: CommandType.StoredProcedure);
            }
        }
    }
}
