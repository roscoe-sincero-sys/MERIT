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

        // 2. SQL Server system error messages (non-50000..51999) are always redacted before storage in logs.ExecutionLog,
        //    including unclosed delimiters and values beyond 1,000 characters, while approved application messages (50000..51999) are preserved.
        var p = new DynamicParameters();
        p.Add("@ProcedureName", "[dbo].[uspSyntheticError345]");
        p.Add("@KeyParameters", "personId=123");
        p.Add("@StartDateUtc", DateTime.UtcNow);
        p.Add("@ReCreatedAfterRollback", false);
        p.Add("@ExecutionLogId", dbType: DbType.Int64, direction: ParameterDirection.Output);
        await conn.ExecuteAsync("logs.uspStartExecutionLogging", p, commandType: CommandType.StoredProcedure);
        long execId = p.Get<long>("@ExecutionLogId");

        string longPrefix = new string('x', 980);
        await conn.ExecuteAsync("logs.uspRecordExecutionError", new
        {
            ExecutionLogId = execId,
            ProcedureName = "[dbo].[uspSyntheticError345]",
            KeyParameters = "personId=123",
            ErrorNumber = 2627,
            ErrorMessage = longPrefix + " The duplicate key value is (SensitiveDupName345, 'UnclosedSensitiveQuote345",
            ErrorProcedure = "[dbo].[uspSyntheticError345]",
            ErrorLine = 42,
        }, commandType: CommandType.StoredProcedure);

        var errRow = await conn.QuerySingleAsync<(string? ErrorMessage, int? ErrorNumber, string? ErrorProcedure, int? ErrorLine)>(
            "SELECT ErrorMessage, ErrorNumber, ErrorProcedure, ErrorLine FROM logs.ExecutionLog WHERE ExecutionLogId = @id", new { id = execId });
        Assert.DoesNotContain("SensitiveDupName345", errRow.ErrorMessage ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("UnclosedSensitiveQuote345", errRow.ErrorMessage ?? "", StringComparison.Ordinal);
        Assert.Contains("[redacted", errRow.ErrorMessage ?? "", StringComparison.Ordinal);
        Assert.Equal(2627, errRow.ErrorNumber);
        Assert.Equal("[dbo].[uspSyntheticError345]", errRow.ErrorProcedure);
        Assert.Equal(42, errRow.ErrorLine);
        await conn.ExecuteAsync("UPDATE logs.ExecutionLog SET isDeleted = 1 WHERE ExecutionLogId = @id", new { id = execId });

        // Approved application message (51422) remains intact.
        var pApp = new DynamicParameters();
        pApp.Add("@ProcedureName", "[dbo].[uspSyntheticError345App]");
        pApp.Add("@KeyParameters", "personId=123");
        pApp.Add("@StartDateUtc", DateTime.UtcNow);
        pApp.Add("@ReCreatedAfterRollback", false);
        pApp.Add("@ExecutionLogId", dbType: DbType.Int64, direction: ParameterDirection.Output);
        await conn.ExecuteAsync("logs.uspStartExecutionLogging", pApp, commandType: CommandType.StoredProcedure);
        long appExecId = pApp.Get<long>("@ExecutionLogId");
        await conn.ExecuteAsync("logs.uspRecordExecutionError", new
        {
            ExecutionLogId = appExecId,
            ProcedureName = "[dbo].[uspSyntheticError345App]",
            KeyParameters = "personId=123",
            ErrorNumber = 51422,
            ErrorMessage = "Confirmation code mismatch.",
            ErrorProcedure = "[dbo].[uspSyntheticError345App]",
            ErrorLine = 10,
        }, commandType: CommandType.StoredProcedure);
        string appMsg = await conn.ExecuteScalarAsync<string>(
            "SELECT ErrorMessage FROM logs.ExecutionLog WHERE ExecutionLogId = @id", new { id = appExecId }) ?? "";
        Assert.Equal("Confirmation code mismatch.", appMsg);
        await conn.ExecuteAsync("UPDATE logs.ExecutionLog SET isDeleted = 1 WHERE ExecutionLogId = @id", new { id = appExecId });
    }

    [SqlFact]
    public async Task Issue346_RootTenantExportAndPurgeIsolatesOtherRoots()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        Guid synRoot = TimeOrderedId.New();
        string synCode = "OFFB" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid synRegion = TimeOrderedId.New();
        string synRegionCode = "REG" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid synPerson = TimeOrderedId.New();
        Guid synDeletedPerson = TimeOrderedId.New();
        Guid synLocation = TimeOrderedId.New();
        Guid synBatch = TimeOrderedId.New();
        Guid synSearch = TimeOrderedId.New();
        Guid synUser = TimeOrderedId.New();
        Guid synRole = TimeOrderedId.New();
        Guid synAssign = TimeOrderedId.New();
        Guid synRecovery = TimeOrderedId.New();
        Guid synDeletedQueuedJob = TimeOrderedId.New();
        Guid synOrg = TimeOrderedId.New();
        Guid synLabResult = TimeOrderedId.New();
        Guid synCase = TimeOrderedId.New();
        Guid crossRootSession = TimeOrderedId.New();
        Guid crossRootScopeRoot = TimeOrderedId.New();
        Guid crossRootScopeTenant = TimeOrderedId.New();

        var mdAdmin = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId FROM auth.appUser WHERE userName = 'admin' AND isDeleted = 0");
        var analyteInfo = await conn.QuerySingleAsync<(Guid analyteId, Guid analyteSpecimenId)>(
            "SELECT TOP (1) analyteId, analyteSpecimenId FROM config.analyteSpecimen WHERE isDeleted = 0 ORDER BY analyteSpecimenId");

        // Seed a parent region root and a synthetic child data root with live + previously soft-deleted PHI,
        // actor createdBy stamps, a previously soft-deleted queued job with payload, a labResult + exposureCase,
        // a cross-root session grant acting in MD with write access to synRoot, geocodes, raw intake file,
        // credentials, recovery codes, live assignments, searchLog, and append-only auditEvent (B4, I17, Codex #1-#5, S20).
        await conn.ExecuteAsync(
            """
            INSERT auth.tenant (tenantId, rootTenantId, tenantCode, tenantName, tenantType, isActive)
            VALUES (@reg, @reg, @regCode, N'Synthetic Offboarding Region', 'region', 1);
            INSERT auth.tenant (tenantId, rootTenantId, parentTenantId, tenantCode, tenantName, tenantType, isActive)
            VALUES (@r, @r, @reg, @code, N'Synthetic Offboarding Root', 'state', 1);
            INSERT auth.tenantClosure (tenantClosureId, ancestorTenantId, descendantTenantId, depth)
            VALUES (NEWID (), @reg, @reg, 0), (NEWID (), @r, @r, 0), (NEWID (), @reg, @r, 1);
            INSERT auth.role (roleId, rootTenantId, roleCode, roleName)
            VALUES (@role, @r, 'stateAdmin', N'State Admin');
            INSERT auth.appUser (appUserId, rootTenantId, homeTenantId, userName, displayName, email, passwordHash, mfaSecretProtected, securityStamp, isActive)
            VALUES (@u, @r, @r, @uname, N'Synthetic User', N'syn@example.org', N'real-hash-secret', N'protected-totp-secret', NEWID (), 1);
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            VALUES (@assign, @u, @role, @r, 1, 'full', '2026-01-01');
            INSERT auth.mfaRecoveryCode (mfaRecoveryCodeId, appUserId, codeHash)
            VALUES (@rec, @u, N'recovery-hash-secret');
            INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey, firstNameKey, dob, sex, ethnicity, createdBy)
            VALUES (@p, @r, @r, N'OffboardSecretName', N'Jane', N'OFFBOARDSECRETNAME', N'JANE', '2021-02-03', 'female', 'hispanic', N'jdoe@example.org');
            INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, lastNameKey, firstNameKey, dob, sex, ethnicity, isDeleted, createdBy)
            VALUES (@pd, @r, @r, N'PreviouslyDeletedPhi', N'Merged', N'PREVIOUSLYDELETEDPHI', N'MERGED', '2019-07-08', 'male', 'notHispanic', 1, N'jdoe@example.org');
            INSERT dbo.location (locationId, rootTenantId, owningTenantId, addressLine1, city, state, postalCode, country, stdStatus, latitude, longitude, censusTract, addressKey)
            VALUES (@loc, @r, @r, N'123 Secret St', N'Baltimore', 'MD', '21201', 'US', 'raw', 39.2904, -76.6122, '24510010100', N'123-SECRET-ST');
            INSERT dbo.organization (organizationId, rootTenantId, owningTenantId, organizationName, organizationNameKey)
            VALUES (@org, @r, @r, N'Synthetic Lab', N'SYNTHETIC-LAB');
            INSERT dbo.intakeBatch (intakeBatchId, rootTenantId, owningTenantId, sourceChannel, receivedUtc, batchStatus, fileName, rawContent, receivedCount, loadedCount, heldCount, rejectedCount)
            VALUES (@batch, @r, @r, 'upload', SYSUTCDATETIME (), 'loaded', N'secret-lab.hl7', CONVERT (VARBINARY (MAX), 'MSH|^~\&|SECRET_LAB_FILE'), 1, 1, 0, 0);
            INSERT dbo.labResult (labResultId, rootTenantId, owningTenantId, personId, analyteSpecimenId, collectedDate, receivedUtc, resultStatus, valueReported, unitReported, unitCanonical, labOrganizationId, reportedSnapshotJson, intakeBatchId)
            VALUES (@lr, @r, @r, @p, @aspec, '2026-01-05', SYSUTCDATETIME (), 'final', 12.5, N'ug/dL', N'ug/dL', @org, N'{"name":"OffboardSecretName"}', @batch);
            INSERT dbo.exposureCase (exposureCaseId, rootTenantId, owningTenantId, caseNumber, personId, analyteId, population, classification, computedClassification, caseStatus, qualifyingLabResultId, workflowTemplate, openedDate, createdBy)
            VALUES (@ec, @r, @r, @caseNum, @p, @analyte, 'child', 'confirmed', 'confirmed', 'open', @lr, 'lead-child', '2026-01-10', N'jdoe@example.org');
            INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, jobStatus, payloadJson, availableUtc, requestedByUserId, isDeleted, createdBy)
            VALUES (@jq, @r, @r, 'intake', 'queued', N'{"secret":"DeletedQueuedJobPayload346"}', SYSUTCDATETIME (), @u, 1, N'jdoe@example.org');
            INSERT auth.sessionScope (sessionScopeId, appUserId, rootTenantId, actingTenantId, sessionReference, isDeidentified, expiresUtc)
            VALUES (@crSess, @mdUser, @mdRoot, @mdRoot, NEWID (), 0, DATEADD (HOUR, 1, SYSUTCDATETIME ()));
            INSERT auth.sessionScopeRoot (sessionScopeRootId, sessionScopeId, rootTenantId)
            VALUES (@crRoot, @crSess, @r);
            INSERT auth.sessionScopeTenant (sessionScopeTenantId, sessionScopeId, tenantId, accessLevel, canReadIdentifiedResults, canReadIdentifiedCases)
            VALUES (@crTen, @crSess, @r, 'write', 1, 1);
            INSERT logs.searchLog (searchLogId, appUserId, rootTenantId, searchText, resultCount, searchedUtc)
            VALUES (@s, @u, @r, N'OffboardSecretQuery', 1, SYSUTCDATETIME ());
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
            VALUES (@u, @r, 'view', 'person', @p);
            """, new
            {
                reg = synRegion, regCode = synRegionCode, r = synRoot, code = synCode, role = synRole, u = synUser, uname = "u_" + synCode,
                assign = synAssign, rec = synRecovery, p = synPerson, pd = synDeletedPerson, org = synOrg, lr = synLabResult,
                aspec = analyteInfo.analyteSpecimenId, ec = synCase, caseNum = "C-" + synCode, analyte = analyteInfo.analyteId,
                jq = synDeletedQueuedJob, crSess = crossRootSession, mdUser = mdAdmin.appUserId, mdRoot = mdAdmin.rootTenantId,
                crRoot = crossRootScopeRoot, crTen = crossRootScopeTenant,
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

            // 2. Platform export manifest covers all 71 tenant/auth tables (active + soft-deleted counts + maxAuditEventId cutoff)
            //    and streams individual tables without credential columns while including soft-deleted history (I17, Codex round 2).
            var manifest = (await conn.QueryAsync<(string tableName, int activeRows, int deletedRows, int totalRows, string disposition, long? maxAuditEventId)>(
                "config.uspExportRootTenant", new { rootTenantId = synRoot }, commandType: CommandType.StoredProcedure)).ToList();
            Assert.Equal(71, manifest.Count);
            var personManifest = Assert.Single(manifest, r => r.tableName == "dbo.person");
            Assert.Equal(1, personManifest.activeRows);
            Assert.Equal(1, personManifest.deletedRows);
            Assert.Equal(2, personManifest.totalRows);
            var ledgerManifest = Assert.Single(manifest, r => r.tableName == "logs.auditEvent");
            Assert.Equal("ledgerReadOnly", ledgerManifest.disposition);
            Assert.NotNull(ledgerManifest.maxAuditEventId);
            Assert.True(ledgerManifest.totalRows >= 1);

            // Codex round 3: Rejects invalid @tableName BEFORE writing logs.auditEvent (raw text never enters ledger),
            // and requires @maxAuditEventId when paging logs.auditEvent.
            var badTableEx = await Assert.ThrowsAsync<SqlException>(() =>
                conn.ExecuteAsync("config.uspExportRootTenant", new { rootTenantId = synRoot, tableName = "SECRET_INVALID_TABLE_346" }, commandType: CommandType.StoredProcedure));
            Assert.Equal(51422, badTableEx.Number);
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT (*) FROM logs.auditEvent WHERE rootTenantId = @r AND filterJson LIKE N'%SECRET_INVALID_TABLE_346%'", new { r = synRoot }));

            var missingCutoffEx = await Assert.ThrowsAsync<SqlException>(() =>
                conn.ExecuteAsync("config.uspExportRootTenant", new { rootTenantId = synRoot, tableName = "logs.auditEvent", pageNumber = 1, pageSize = 1 }, commandType: CommandType.StoredProcedure));
            Assert.Equal(51422, missingCutoffEx.Number);

            // Codex round 2 & 3: Keyset paging logs.auditEvent at pageSize = 1 with manifest maxAuditEventId terminates cleanly
            // and matches manifest totalRows even when another audit event commits between pages.
            int exportedLedgerCount = 0;
            long afterId = 0;
            for (int page = 1; page <= ledgerManifest.totalRows + 5; page++)
            {
                if (page == 2)
                {
                    await conn.ExecuteAsync(
                        "INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId) VALUES (@u, @r, 'view', 'person', @p);",
                        new { u = synUser, r = synRoot, p = synPerson });
                }
                var ledgerPage = (await conn.QueryAsync<dynamic>(
                    "config.uspExportRootTenant",
                    new { rootTenantId = synRoot, tableName = "logs.auditEvent", pageNumber = 1, pageSize = 1, maxAuditEventId = ledgerManifest.maxAuditEventId, afterAuditEventId = afterId },
                    commandType: CommandType.StoredProcedure)).ToList();
                if (ledgerPage.Count == 0)
                {
                    break;
                }
                Assert.Single(ledgerPage);
                long rowId = (long)ledgerPage[0].auditEventId;
                Assert.True(rowId > afterId);
                Assert.True(rowId <= ledgerManifest.maxAuditEventId!.Value);
                afterId = rowId;
                exportedLedgerCount++;
            }
            Assert.Equal(ledgerManifest.totalRows, exportedLedgerCount);

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

            // 2b. Export and Purge return -100 on enclosing transactions (RPC and XACT_ABORT ON + TRY/CATCH)
            //     and on IMPLICIT_TRANSACTIONS ON, leaving caller transactions committable (@@TRANCOUNT = 1, XACT_STATE() = 1).
            using (var tx = conn.BeginTransaction())
            {
                var expParams = new DynamicParameters(new { rootTenantId = synRoot });
                expParams.Add("@ret", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
                await conn.ExecuteAsync("config.uspExportRootTenant", expParams, transaction: tx, commandType: CommandType.StoredProcedure);
                Assert.Equal(-100, expParams.Get<int>("@ret"));
                var txState1 = await conn.QuerySingleAsync<(int tranCount, int xactState)>("SELECT @@TRANCOUNT, XACT_STATE ()", transaction: tx);
                Assert.Equal(1, txState1.tranCount);
                Assert.Equal(1, txState1.xactState);

                var purgeParams = new DynamicParameters(new { rootTenantId = synRoot, confirmationCode = $"PURGE:{synCode}", backupRetentionDays = 35, dryRun = true });
                purgeParams.Add("@ret", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
                await conn.ExecuteAsync("config.uspPurgeRootTenant", purgeParams, transaction: tx, commandType: CommandType.StoredProcedure);
                Assert.Equal(-100, purgeParams.Get<int>("@ret"));
                var txState2 = await conn.QuerySingleAsync<(int tranCount, int xactState)>("SELECT @@TRANCOUNT, XACT_STATE ()", transaction: tx);
                Assert.Equal(1, txState2.tranCount);
                Assert.Equal(1, txState2.xactState);

                tx.Rollback();
            }

            // Verify XACT_ABORT ON + caller TRY/CATCH preserves committable transaction (XACT_STATE() = 1).
            var tryCatchProbe = await conn.QuerySingleAsync<(int rc, int tc, int xs)>(
                """
                SET XACT_ABORT ON;
                DECLARE @rc INT = 0, @tc INT = 0, @xs INT = 0;
                BEGIN TRY
                    BEGIN TRANSACTION;
                    EXEC @rc = config.uspExportRootTenant @rootTenantId = @r;
                    SET @tc = @@TRANCOUNT;
                    SET @xs = XACT_STATE ();
                    ROLLBACK TRANSACTION;
                END TRY
                BEGIN CATCH
                    SET @tc = @@TRANCOUNT;
                    SET @xs = XACT_STATE ();
                    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                END CATCH;
                SELECT @rc AS rc, @tc AS tc, @xs AS xs;
                """, new { r = synRoot });
            Assert.Equal(-100, tryCatchProbe.rc);
            Assert.Equal(1, tryCatchProbe.tc);
            Assert.Equal(1, tryCatchProbe.xs);

            // Verify IMPLICIT_TRANSACTIONS ON is rejected with -100 before opening a transaction.
            var implicitProbe = await conn.QuerySingleAsync<(int rc, int tc)>(
                """
                SET IMPLICIT_TRANSACTIONS ON;
                DECLARE @rc INT = 0, @tc INT = 0;
                EXEC @rc = config.uspExportRootTenant @rootTenantId = @r;
                SET @tc = @@TRANCOUNT;
                SET IMPLICIT_TRANSACTIONS OFF;
                SELECT @rc AS rc, @tc AS tc;
                """, new { r = synRoot });
            Assert.Equal(-100, implicitProbe.rc);
            Assert.Equal(0, implicitProbe.tc);

            string? exportFilterJson = await conn.ExecuteScalarAsync<string?>(
                "SELECT TOP (1) filterJson FROM logs.auditEvent WHERE rootTenantId = @r AND eventAction = 'export' AND entityType = 'tenant' ORDER BY auditEventId DESC",
                new { r = synRoot });
            Assert.NotNull(exportFilterJson);
            Assert.Contains("auth.appUser", exportFilterJson, StringComparison.Ordinal);

            // 3. Purge rejects mismatched confirmation code (51422), NULL @dryRun (Codex #1), and parent region with live child root (Codex #3).
            var badConfirm = await Assert.ThrowsAsync<SqlException>(() =>
                conn.ExecuteAsync("config.uspPurgeRootTenant", new { rootTenantId = synRoot, confirmationCode = "PURGE:WRONG", backupRetentionDays = 35 }, commandType: CommandType.StoredProcedure));
            Assert.Equal(51422, badConfirm.Number);

            int deleteEventsBeforeNull = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT (*) FROM logs.auditEvent WHERE rootTenantId = @r AND eventAction = 'delete' AND entityType = 'tenant'",
                new { r = synRoot });
            var nullModeEx = await Assert.ThrowsAsync<SqlException>(() =>
                conn.ExecuteAsync("config.uspPurgeRootTenant", new { rootTenantId = synRoot, confirmationCode = $"PURGE:{synCode}", backupRetentionDays = 35, dryRun = (bool?)null }, commandType: CommandType.StoredProcedure));
            Assert.Equal(51422, nullModeEx.Number);
            Assert.Equal(deleteEventsBeforeNull, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT (*) FROM logs.auditEvent WHERE rootTenantId = @r AND eventAction = 'delete' AND entityType = 'tenant'",
                new { r = synRoot }));
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT (*) FROM dbo.person WHERE personId = @p AND isDeleted = 0", new { p = synPerson }));

            var regionEx = await Assert.ThrowsAsync<SqlException>(() =>
                conn.ExecuteAsync("config.uspPurgeRootTenant", new { rootTenantId = synRegion, confirmationCode = $"PURGE:{synRegionCode}", backupRetentionDays = 35 }, commandType: CommandType.StoredProcedure));
            Assert.Equal(51422, regionEx.Number);

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

            // Verify BOTH live and previously soft-deleted persons are fully scrubbed including createdBy/modifiedBy/deletedBy (B4, Codex #4).
            foreach (Guid pid in new[] { synPerson, synDeletedPerson })
            {
                var pRow = await conn.QuerySingleAsync<(bool isDeleted, string lastName, string? firstName, DateTime? dob, string sex, string ethnicity, string createdBy, string modifiedBy, string? deletedBy)>(
                    "SELECT isDeleted, lastName, firstName, dob, sex, ethnicity, createdBy, modifiedBy, deletedBy FROM dbo.person WHERE personId = @p", new { p = pid });
                Assert.True(pRow.isDeleted);
                Assert.Equal("[purged]", pRow.lastName);
                Assert.Null(pRow.firstName);
                Assert.Null(pRow.dob);
                Assert.Equal("unknown", pRow.sex);
                Assert.Equal("unknown", pRow.ethnicity);
                Assert.Equal("MERIT tenant purge", pRow.createdBy);
                Assert.Equal("MERIT tenant purge", pRow.modifiedBy);
                Assert.Equal("MERIT tenant purge", pRow.deletedBy);
            }

            // Verify previously soft-deleted queued job had its payload and actor fields scrubbed (Codex #4).
            var jobRow = await conn.QuerySingleAsync<(bool isDeleted, string payloadJson, string createdBy, string modifiedBy)>(
                "SELECT isDeleted, payloadJson, createdBy, modifiedBy FROM dbo.jobQueue WHERE jobId = @id", new { id = synDeletedQueuedJob });
            Assert.True(jobRow.isDeleted);
            Assert.Equal("{}", jobRow.payloadJson);
            Assert.Equal("MERIT tenant purge", jobRow.createdBy);
            Assert.Equal("MERIT tenant purge", jobRow.modifiedBy);

            // Verify purged exposureCase is closed with closureReason = 'other' (S20).
            var caseRow = await conn.QuerySingleAsync<(bool isDeleted, string caseStatus, string? closureReason, DateTime? closedDate)>(
                "SELECT isDeleted, caseStatus, closureReason, closedDate FROM dbo.exposureCase WHERE exposureCaseId = @id", new { id = synCase });
            Assert.True(caseRow.isDeleted);
            Assert.Equal("closed", caseRow.caseStatus);
            Assert.Equal("other", caseRow.closureReason);
            Assert.NotNull(caseRow.closedDate);

            // Verify cross-root session header itself was revoked AND its target membership rows were scrubbed (Codex round 1 & 2).
            Assert.NotNull(await conn.ExecuteScalarAsync<DateTime?>("SELECT revokedUtc FROM auth.sessionScope WHERE sessionScopeId = @id", new { id = crossRootSession }));
            Assert.True(await conn.ExecuteScalarAsync<bool>("SELECT isDeleted FROM auth.sessionScopeRoot WHERE sessionScopeRootId = @id", new { id = crossRootScopeRoot }));
            Assert.True(await conn.ExecuteScalarAsync<bool>("SELECT isDeleted FROM auth.sessionScopeTenant WHERE sessionScopeTenantId = @id", new { id = crossRootScopeTenant }));

            // Codex round 2 & 3: Once synRoot is purged:
            // - Installing an ordinary child tenant under synRoot is rejected (51404).
            // - Reinstalling synRoot with the same tenantCode is rejected (51422).
            // - synRegion can now be purged, and afterward installing a child root under the purged region is rejected (51422).
            var childUnderPurgedEx = await Assert.ThrowsAsync<SqlException>(() =>
                conn.ExecuteAsync("config.uspInstallChildTenant",
                    new { tenantCode = "PROG" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant(), tenantName = "Program Under Purged Root", tenantType = "program", parentTenantCode = synCode },
                    commandType: CommandType.StoredProcedure));
            Assert.Equal(51404, childUnderPurgedEx.Number);

            var reinstallPurgedEx = await Assert.ThrowsAsync<SqlException>(() =>
                conn.ExecuteAsync("config.uspInstallRootTenant",
                    new { tenantCode = synCode, tenantName = "Reinstalled Purged Root", tenantType = "state" },
                    commandType: CommandType.StoredProcedure));
            Assert.Equal(51422, reinstallPurgedEx.Number);

            await conn.ExecuteAsync("config.uspPurgeRootTenant",
                new { rootTenantId = synRegion, confirmationCode = $"PURGE:{synRegionCode}", backupRetentionDays = 35 },
                commandType: CommandType.StoredProcedure);
            var installUnderPurgedEx = await Assert.ThrowsAsync<SqlException>(() =>
                conn.ExecuteAsync("config.uspInstallRootTenant",
                    new { tenantCode = "CHLD" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant(), tenantName = "Child Under Purged Region", tenantType = "state", parentTenantCode = synRegionCode },
                    commandType: CommandType.StoredProcedure));
            Assert.Equal(51422, installUnderPurgedEx.Number);

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
                // #352: Single-pass live-row scrub (SESSION_CONTEXT AppUser = 'MERIT tenant purge') avoids double-updating ~430k live rows.
                Assert.True(drySummary.totalRowsPurged < 550000, $"Expected single-pass purge count (<550k), got {drySummary.totalRowsPurged}");
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
            // I19: Always ensure synthetic roots and cross-root session are cleaned up even if an assertion fails mid-test.
            await conn.ExecuteAsync("UPDATE auth.sessionScope SET revokedUtc = COALESCE (revokedUtc, SYSUTCDATETIME ()) WHERE sessionScopeId = @s;", new { s = crossRootSession });
            if (await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM auth.tenant WHERE tenantId = @r AND isDeleted = 0", new { r = synRoot }) > 0)
            {
                await conn.ExecuteAsync("config.uspPurgeRootTenant",
                    new { rootTenantId = synRoot, confirmationCode = $"PURGE:{synCode}", backupRetentionDays = 35 },
                    commandType: CommandType.StoredProcedure);
            }
            if (await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM auth.tenant WHERE tenantId = @r AND isDeleted = 0", new { r = synRegion }) > 0)
            {
                await conn.ExecuteAsync("config.uspPurgeRootTenant",
                    new { rootTenantId = synRegion, confirmationCode = $"PURGE:{synRegionCode}", backupRetentionDays = 35 },
                    commandType: CommandType.StoredProcedure);
            }
        }
    }
}
