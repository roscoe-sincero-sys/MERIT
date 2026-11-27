using System.Collections.Concurrent;
using System.Data;
using System.Net;
using Dapper;
using Merit.Domain.Ids;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Merit.IntegrationTests;

/// <summary>
/// End-to-end SQL + web integration tests for logging wave 3 (#327, #335, #336, #340, #341).
/// </summary>
[Collection(MeritCollection.Name)]
public sealed class LoggingWave3IntegrationTests(MeritFixture fixture)
{
    [SqlFact]
    public async Task Issue327_Authenticated404ProbingAndMidSessionUserAgentDrift()
    {
        var supClient = await fixture.SignedInAsync("supervisor");
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        var sup = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId FROM auth.appUser WHERE userName = 'supervisor' AND isDeleted = 0");

        // 1. Authenticated 404 on a record lookup writes eventAction = 'notFound' with non-PII userAgentHash.
        Guid missingPersonId = TimeOrderedId.New();
        using (var req = new HttpRequestMessage(HttpMethod.Get, $"/people/{missingPersonId}"))
        {
            req.Headers.UserAgent.ParseAdd("MeritWave3TestAgent/1.0");
            using var resp = await supClient.NoRedirect.SendAsync(req);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        var nfRow = await conn.QuerySingleOrDefaultAsync<(string eventAction, string entityType, string? userAgentHash)>(
            "SELECT TOP (1) eventAction, entityType, userAgentHash FROM logs.auditEvent WHERE entityId = @id AND eventAction = 'notFound' ORDER BY auditEventId DESC",
            new { id = missingPersonId });
        Assert.Equal("notFound", nfRow.eventAction);
        Assert.Equal("person", nfRow.entityType);
        Assert.False(string.IsNullOrWhiteSpace(nfRow.userAgentHash));
        Assert.Equal(16, nfRow.userAgentHash!.Length);

        // 2. Repeated authenticated 404s trigger probingDeniedOrOutsideScope in both live and scheduled rules.
        Guid probingUser = TimeOrderedId.New();
        for (int i = 0; i < 10; i++)
        {
            await conn.ExecuteAsync(
                "INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress) VALUES (@u, @r, 'notFound', 'person', @e, '198.51.100.77');",
                new { u = probingUser, r = sup.rootTenantId, e = TimeOrderedId.New() });
        }

        // 3. Mid-session User-Agent drift triggers midSessionUserAgentDrift (medium on 2 hashes, high on 3).
        Guid uaSession = TimeOrderedId.New();
        await conn.ExecuteAsync(
            """
            INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, clientAddress, sessionReference, userAgentHash)
            VALUES (@u, @r, 'view', 'person', '203.0.113.50', @s, 'AAAAAAAAAAAAAAAA'),
                   (@u, @r, 'view', 'person', '203.0.113.50', @s, 'BBBBBBBBBBBBBBBB');
            """, new { u = sup.appUserId, r = sup.rootTenantId, s = uaSession });

        var scan1 = (await conn.QueryAsync<dynamic>("logs.uspRunSecurityFindingScan", new { hours = 24 }, commandType: CommandType.StoredProcedure)).ToList();
        Assert.Contains(scan1, r => (string)r.ruleCode == "probingDeniedOrOutsideScope" && (Guid?)r.appUserId == probingUser);
        var uaFinding = await conn.QuerySingleAsync<(Guid securityFindingId, string severity)>(
            "SELECT securityFindingId, severity FROM logs.securityFinding WHERE ruleCode = 'midSessionUserAgentDrift' AND sessionReference = @s AND isDeleted = 0",
            new { s = uaSession });
        Assert.Equal("medium", uaFinding.severity);

        // Adding a 3rd User-Agent hash escalates to high.
        await conn.ExecuteAsync(
            "INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, clientAddress, sessionReference, userAgentHash) VALUES (@u, @r, 'view', 'person', '203.0.113.50', @s, 'CCCCCCCCCCCCCCCC');",
            new { u = sup.appUserId, r = sup.rootTenantId, s = uaSession });
        var scan2 = (await conn.QueryAsync<dynamic>("logs.uspRunSecurityFindingScan", new { hours = 24 }, commandType: CommandType.StoredProcedure)).ToList();
        Assert.Contains(scan2, r => (Guid)r.securityFindingId == uaFinding.securityFindingId && (string)r.severity == "high");
    }

    [SqlFact]
    public async Task Issue335_InTransactionWriteAuditRollsBackOnPostWriteFailureAndSucceedsOnRetry()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        var admin = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId, Guid homeTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId, homeTenantId FROM auth.appUser WHERE userName = 'admin' AND isDeleted = 0");

        // Open a session scope so dbo.uspSavePerson and dbo.uspSavePersonContactMethod can run with full session context.
        Guid scopeKey = TimeOrderedId.New();
        Guid sessionRef = TimeOrderedId.New();
        await conn.ExecuteAsync(
            """
            INSERT auth.sessionScope (sessionScopeId, appUserId, rootTenantId, actingTenantId, sessionReference, expiresUtc)
            VALUES (@sk, @u, @r, @t, @sr, DATEADD (HOUR, 1, SYSUTCDATETIME ()));
            INSERT auth.sessionScopeTenant (sessionScopeTenantId, sessionScopeId, tenantId, accessLevel)
            VALUES (NEWID (), @sk, @r, 'full');
            IF @t <> @r
                INSERT auth.sessionScopeTenant (sessionScopeTenantId, sessionScopeId, tenantId, accessLevel)
                VALUES (NEWID (), @sk, @t, 'full');
            """, new { sk = scopeKey, u = admin.appUserId, r = admin.rootTenantId, t = admin.homeTenantId, sr = sessionRef });

        // 1. Force a post-write failure during the audit-snapshot insert inside dbo.uspSavePerson, verify BOTH
        //    the dbo.person row AND logs.auditEvent row roll back completely, then retry cleanly.
        Guid personId = TimeOrderedId.New();
        const string triggerName = "logs.trg_test_fail_audit_change_335";
        await conn.ExecuteAsync($"""
            CREATE OR ALTER TRIGGER {triggerName} ON logs.auditEventChange AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted WHERE entityId = '{personId}')
                    THROW 51999, N'Synthetic post-write audit failure (#335 test).', 1;
            END;
            """);

        try
        {
            await using (var failConn = new SqlConnection(TestEnvironment.AdminConnectionString()))
            {
                await failConn.OpenAsync();
                await SetContextAsync(failConn, scopeKey, admin.appUserId, "admin", admin.homeTenantId);
                var ex = await Assert.ThrowsAsync<SqlException>(() => SaveSyntheticPersonAsync(failConn, personId, admin.homeTenantId));
                Assert.Equal(51999, ex.Number);
            }

            // Neither the person nor the audit event may have committed.
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM dbo.person WHERE personId = @id", new { id = personId }));
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE entityId = @id", new { id = personId }));
        }
        finally
        {
            await conn.ExecuteAsync($"DROP TRIGGER IF EXISTS {triggerName};");
        }

        // 2. Retry the exact same write after the transient audit failure clears: commits person + auditEvent + auditEventChange atomically.
        await using (var retryConn = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            await retryConn.OpenAsync();
            await SetContextAsync(retryConn, scopeKey, admin.appUserId, "admin", admin.homeTenantId);
            await SaveSyntheticPersonAsync(retryConn, personId, admin.homeTenantId);

        // Also verify a newly migrated secondary write path (dbo.uspSavePersonContactMethod) writes an in-tx auditEvent,
            // and Codex #3: uspSavePersonRelationship preserves personRelationship + @personRelationshipId on verification
            // while using person + @personId on normal saves.
            Guid contactId = TimeOrderedId.New();
            await retryConn.ExecuteAsync("dbo.uspSavePersonContactMethod",
                new { personContactMethodId = contactId, personId, contactType = "phone", contactValue = "4105550199", isPreferred = true, isDeleted = false },
                commandType: CommandType.StoredProcedure);

            Guid relId = TimeOrderedId.New();
            await retryConn.ExecuteAsync("dbo.uspSavePersonRelationship",
                new { personRelationshipId = relId, personId, relatedPersonId = fixture.Ids.MiaPersonId, role = "parent", isPrimaryContact = true, hasLegalCustody = true, source = "interview", isDeleted = false, isVerification = false },
                commandType: CommandType.StoredProcedure);
            byte[] relVersion = await retryConn.ExecuteScalarAsync<byte[]>(
                "SELECT rowVersion FROM dbo.personRelationship WHERE personRelationshipId = @rid", new { rid = relId }) ?? [];
            await retryConn.ExecuteAsync("dbo.uspSavePersonRelationship",
                new { personRelationshipId = relId, personId, relatedPersonId = fixture.Ids.MiaPersonId, role = "parent", isPrimaryContact = true, hasLegalCustody = true, source = "interview", verifiedDate = DateTime.UtcNow.Date, isDeleted = false, rowVersion = relVersion, isVerification = true },
                commandType: CommandType.StoredProcedure);
        }

        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM dbo.person WHERE personId = @id AND isDeleted = 0", new { id = personId }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE entityId = @id AND eventAction = 'create' AND entityType = 'person'", new { id = personId }));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT COUNT (*) FROM logs.auditEvent WHERE entityId = @id AND eventAction = 'update' AND entityType = 'person'", new { id = personId }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT (*) FROM logs.auditEvent WHERE eventAction = 'update' AND entityType = 'personRelationship' AND entityId IN (SELECT personRelationshipId FROM dbo.personRelationship WHERE personId = @id)",
            new { id = personId }));

        // Codex #1: Always seed a split-owner case (case.owningTenantId <> dbo.udfPatientWorkOwner(personId)) so the
        // task-owner audit regression runs deterministically on every database state.
        Guid splitCaseId = TimeOrderedId.New();
        Guid patientWorkOwner = await conn.ExecuteScalarAsync<Guid>("SELECT dbo.udfPatientWorkOwner (@p)", new { p = personId });
        Guid distinctCaseOwner = patientWorkOwner == fixture.Ids.BaltimoreCityTenantId
            ? fixture.Ids.BaltimoreCountyTenantId
            : fixture.Ids.BaltimoreCityTenantId;
        await conn.ExecuteAsync(
            """
            INSERT dbo.exposureCase (exposureCaseId, rootTenantId, owningTenantId, personId, analyteId, population, classification, computedClassification, qualifyingLabResultId, workflowTemplate, caseNumber, caseStatus, openedDate)
            SELECT @cid, @r, @owner, @p, analyteId, population, classification, computedClassification, qualifyingLabResultId, workflowTemplate, @num, 'open', CAST (SYSUTCDATETIME () AS DATE)
              FROM dbo.exposureCase WHERE exposureCaseId = @srcCase;
            """, new { cid = splitCaseId, r = admin.rootTenantId, owner = distinctCaseOwner, p = personId, srcCase = fixture.Ids.MiaCaseId, num = "W3-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant() });

        Guid taskId = TimeOrderedId.New();
        await using (var taskConn = new SqlConnection(TestEnvironment.AdminConnectionString()))
        {
            await taskConn.OpenAsync();
            await SetContextAsync(taskConn, scopeKey, admin.appUserId, "admin", admin.homeTenantId);
            await taskConn.ExecuteAsync("dbo.uspCreateAdHocWork",
                new { workTaskId = taskId, personId, title = "Wave3 owner test", description = "Verify task audit owner", completionKind = "internal", exposureCaseId = splitCaseId, dueDate = DateTime.UtcNow.Date.AddDays(7) },
                commandType: CommandType.StoredProcedure);
        }
        var auditedOwner = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT TOP (1) owningTenantId FROM logs.auditEvent WHERE entityType = 'workTask' AND entityId = @tid ORDER BY auditEventId DESC",
            new { tid = taskId });
        Assert.Equal(distinctCaseOwner, auditedOwner);
        await conn.ExecuteAsync("UPDATE dbo.workTask SET isDeleted = 1 WHERE workTaskId = @tid; UPDATE dbo.exposureCase SET isDeleted = 1 WHERE exposureCaseId = @cid;",
            new { tid = taskId, cid = splitCaseId });

        // Clean up synthetic person and scope.
        await conn.ExecuteAsync("UPDATE dbo.personRelationship SET isDeleted = 1 WHERE personId = @id; UPDATE dbo.personContactMethod SET isDeleted = 1 WHERE personId = @id; UPDATE dbo.person SET isDeleted = 1 WHERE personId = @id; UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE sessionScopeId = @sk;",
            new { id = personId, sk = scopeKey });
    }

    [SqlFact]
    public async Task Issue336_SignInEventPurgeScrubsDetailsAndPreservesAggregateCounts()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        var sup = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId FROM auth.appUser WHERE userName = 'supervisor' AND isDeleted = 0");

        Guid expiredId = TimeOrderedId.New();
        DateTime oldUtc = DateTime.UtcNow.AddDays(-400);
        await conn.ExecuteAsync(
            """
            INSERT logs.signInEvent (signInEventId, eventUtc, eventType, appUserId, rootTenantId, clientAddress, forwardedFor, userAgent, attemptedNameHash, sessionReference, traceId)
            VALUES (@id, @dt, 'passwordFailed', @u, @r, '198.51.100.88', '198.51.100.88, 10.0.0.1', 'Mozilla/5.0 Test', 0x01020304, @sr, 'trace-336-test');
            """, new { id = expiredId, dt = oldUtc, u = sup.appUserId, r = sup.rootTenantId, sr = TimeOrderedId.New() });

        // Also verify a legacy soft-deleted row (isDeleted = 1 from an earlier purge) has sessionReference/traceId scrubbed idempotently (#336, Codex #4).
        Guid legacyPurgedId = TimeOrderedId.New();
        await conn.ExecuteAsync(
            """
            INSERT logs.signInEvent (signInEventId, eventUtc, eventType, appUserId, rootTenantId, sessionReference, traceId, isDeleted)
            VALUES (@id, @dt, 'passwordFailed', @u, @r, @sr, 'trace-336-legacy', 1);
            """, new { id = legacyPurgedId, dt = oldUtc, u = sup.appUserId, r = sup.rootTenantId, sr = TimeOrderedId.New() });

        int purged = await conn.ExecuteScalarAsync<int>("logs.uspPurgeSignInEvents", new { retentionDays = 365 }, commandType: CommandType.StoredProcedure);
        Assert.True(purged >= 2);
        // Second run is idempotent (0 rows remaining for these test IDs).
        int secondPurge = await conn.ExecuteScalarAsync<int>("logs.uspPurgeSignInEvents", new { retentionDays = 365 }, commandType: CommandType.StoredProcedure);
        Assert.Equal(0, secondPurge);

        var row = await conn.QuerySingleAsync<dynamic>(
            "SELECT isDeleted, eventType, rootTenantId, clientAddress, forwardedFor, userAgent, attemptedNameHash, attemptedNameProtected, sessionReference, traceId FROM logs.signInEvent WHERE signInEventId = @id",
            new { id = expiredId });
        Assert.True((bool)row.isDeleted);
        Assert.Equal("passwordFailed", (string)row.eventType);
        Assert.Equal(sup.rootTenantId, (Guid)row.rootTenantId);
        Assert.Null((object?)row.clientAddress);
        Assert.Null((object?)row.forwardedFor);
        Assert.Null((object?)row.userAgent);
        Assert.Null((object?)row.attemptedNameHash);
        Assert.Null((object?)row.attemptedNameProtected);
        Assert.Null((object?)row.sessionReference);
        Assert.Null((object?)row.traceId);

        var legacyRow = await conn.QuerySingleAsync<dynamic>(
            "SELECT isDeleted, eventType, rootTenantId, sessionReference, traceId FROM logs.signInEvent WHERE signInEventId = @id",
            new { id = legacyPurgedId });
        Assert.True((bool)legacyRow.isDeleted);
        Assert.Equal("passwordFailed", (string)legacyRow.eventType);
        Assert.Equal(sup.rootTenantId, (Guid)legacyRow.rootTenantId);
        Assert.Null((object?)legacyRow.sessionReference);
        Assert.Null((object?)legacyRow.traceId);
    }

    [SqlFact]
    public async Task Issue340_AuditManifestPurgeCommitsPerBatchAndAllowsConcurrentWrites()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        var sup = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId FROM auth.appUser WHERE userName = 'supervisor' AND isDeleted = 0");

        long anchorEventId = await conn.ExecuteScalarAsync<long>(
            "INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, recordCount) OUTPUT INSERTED.auditEventId VALUES (@u, @r, 'view', 'person', 5050);",
            new { u = sup.appUserId, r = sup.rootTenantId });

        // Seed 5,050 expired manifest rows (> 1 batch of 5,000) dated 400 days ago.
        await conn.ExecuteAsync(
            """
            DECLARE @old DATETIME2 (3) = DATEADD (DAY, -400, SYSUTCDATETIME ());
            ;WITH n AS (
                SELECT TOP (5050) ROW_NUMBER () OVER (ORDER BY (SELECT NULL)) AS rn
                  FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            )
            INSERT logs.auditEventRecord (auditEventRecordId, auditEventId, rootTenantId, entityType, entityId, createdDateUtc, modifiedDateUtc)
            SELECT NEWID (), @eid, @r, 'person', NEWID (), @old, @old FROM n;
            """, new { eid = anchorEventId, r = sup.rootTenantId });

        Guid liveRecordId = TimeOrderedId.New();

        // Run the multi-batch purger concurrently with a live manifest write.
        var purgeTask = Task.Run(async () =>
        {
            await using var pConn = new SqlConnection(TestEnvironment.AdminConnectionString());
            await pConn.OpenAsync();
            return await pConn.ExecuteScalarAsync<int>("logs.uspPurgeAuditEventRecords", new { retentionDays = 365 }, commandType: CommandType.StoredProcedure);
        });

        var writeTask = Task.Run(async () =>
        {
            await using var wConn = new SqlConnection(TestEnvironment.AdminConnectionString());
            await wConn.OpenAsync();
            var p = new DynamicParameters();
            p.Add("@eventAction", "view");
            p.Add("@entityType", "person");
            p.Add("@recordCount", 1);
            p.Add("@recordIdsJson", $"[{{\"entityType\":\"person\",\"entityId\":\"{liveRecordId}\"}}]");
            // Execute directly via admin connection with a valid root session scope is not needed if we insert via a live HTTP or admin INSERT;
            // here we invoke logs.uspRecordAuditEvent under a real session scope.
            var sk = TimeOrderedId.New();
            await wConn.ExecuteAsync(
                "INSERT auth.sessionScope (sessionScopeId, appUserId, rootTenantId, actingTenantId, sessionReference, expiresUtc) VALUES (@sk, @u, @r, @r, NEWID (), DATEADD (HOUR, 1, SYSUTCDATETIME ()));",
                new { sk, u = sup.appUserId, r = sup.rootTenantId });
            await SetContextAsync(wConn, sk, sup.appUserId, "supervisor", sup.rootTenantId);
            long newEventId = await wConn.ExecuteScalarAsync<long>("logs.uspRecordAuditEvent", p, commandType: CommandType.StoredProcedure);
            await wConn.ExecuteAsync("UPDATE auth.sessionScope SET revokedUtc = SYSUTCDATETIME () WHERE sessionScopeId = @sk;", new { sk });
            return newEventId;
        });

        int purged = await purgeTask;
        long concurrentEventId = await writeTask;

        Assert.True(purged >= 5050);
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT (*) FROM logs.auditEventRecord WHERE auditEventId = @eid AND isDeleted = 0", new { eid = anchorEventId }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT (*) FROM logs.auditEventRecord WHERE auditEventId = @eid AND entityId = @rid AND isDeleted = 0",
            new { eid = concurrentEventId, rid = liveRecordId }));

        // Verify the rewritten UNION record-id filter in logs.uspSearchAuditEvents finds the concurrent manifest event.
        var adminClient = await fixture.SignedInAsync("admin");
        string auditCsv = await adminClient.Http.GetStringAsync($"/admin/audit.csv?entityId={liveRecordId}");
        Assert.Contains("Viewed", auditCsv, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task Issue341_PerRecordManifestScopingAndEndToEndAntiforgeryTelemetry()
    {
        await using var conn = new SqlConnection(TestEnvironment.AdminConnectionString());
        await conn.OpenAsync();

        // 1. Per-record manifest scoping: create an audit event by a Baltimore City user containing one Baltimore City
        //    exposureCase and one Baltimore County exposureCase; a Baltimore City-only security officer must see ONLY
        //    the city case plus "1 additional record(s) in this event are outside your review scope."
        Guid cityCaseId = await conn.ExecuteScalarAsync<Guid>(
            "SELECT TOP (1) exposureCaseId FROM dbo.exposureCase WHERE owningTenantId = @t AND isDeleted = 0",
            new { t = fixture.Ids.BaltimoreCityTenantId });
        Guid countyCaseId = TimeOrderedId.New();
        var cityUser = await conn.QuerySingleAsync<(Guid appUserId, Guid rootTenantId)>(
            "SELECT TOP (1) appUserId, rootTenantId FROM auth.appUser WHERE userName = 'cmbalt' AND isDeleted = 0");

        long mixedEventId = await conn.ExecuteScalarAsync<long>(
            "INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, recordCount, owningTenantId) OUTPUT INSERTED.auditEventId VALUES (@u, @r, 'view', 'exposureCase', 2, NULL);",
            new { u = cityUser.appUserId, r = cityUser.rootTenantId });
        await conn.ExecuteAsync(
            """
            INSERT logs.auditEventRecord (auditEventRecordId, auditEventId, rootTenantId, entityType, entityId, owningTenantId)
            VALUES (NEWID (), @eid, @r, 'exposureCase', @cityCase, @cityTenant),
                   (NEWID (), @eid, @r, 'exposureCase', @countyCase, @countyTenant);
            """, new { eid = mixedEventId, r = cityUser.rootTenantId, cityCase = cityCaseId, cityTenant = fixture.Ids.BaltimoreCityTenantId, countyCase = countyCaseId, countyTenant = fixture.Ids.BaltimoreCountyTenantId });

        var officer = await SyntheticAccount.CreateAsync(fixture.Factory, "manifest341");
        await officer.ScalarAsync<int>("""
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, t.tenantId, 1, 'full', '2026-01-01'
              FROM auth.tenant t JOIN auth.role r ON r.rootTenantId = t.rootTenantId AND r.roleCode = 'securityOfficer' AND r.isDeleted = 0
             WHERE t.tenantCode = 'MD-BCHD';
            UPDATE auth.appUser SET homeTenantId = (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-BCHD') WHERE appUserId = @id;
            SELECT COUNT (*) FROM auth.userAssignment WHERE appUserId = @id AND isDeleted = 0;
            """);

        await using (officer)
        using (var officerClient = fixture.Anonymous())
        {
            await officer.SignInAsync(officerClient, MeritFixture.Password);
            string html = await officerClient.Http.GetStringAsync($"/admin/audit/{mixedEventId}/records");
            Assert.Contains(cityCaseId.ToString(), html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(countyCaseId.ToString(), html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1 additional record(s) in this event are outside your review scope.", html, StringComparison.Ordinal);

            // Codex #2: Entity-ID search on logs.uspSearchAuditEvents must also apply per-record manifest visibility.
            string citySearchCsv = await officerClient.Http.GetStringAsync($"/admin/audit.csv?entityId={cityCaseId}");
            Assert.Contains("Viewed", citySearchCsv, StringComparison.Ordinal);
            string countySearchCsv = await officerClient.Http.GetStringAsync($"/admin/audit.csv?entityId={countyCaseId}");
            Assert.DoesNotContain("Viewed", countySearchCsv, StringComparison.Ordinal);
        }

        // 2. ExecutionLog completion rows attribute modifiedBy to the app user (not meritWeb).
        var execRow = await conn.QuerySingleOrDefaultAsync<(string createdBy, string modifiedBy)>(
            "SELECT TOP (1) createdBy, modifiedBy FROM logs.ExecutionLog WHERE ProcedureName = N'[logs].[uspRecordAuditEvent]' AND Successful = 1 ORDER BY ExecutionLogId DESC");
        Assert.False(string.IsNullOrWhiteSpace(execRow.createdBy));
        Assert.Equal(execRow.createdBy, execRow.modifiedBy);
        Assert.NotEqual("meritWeb", execRow.modifiedBy, StringComparer.OrdinalIgnoreCase);

        // 3. End-to-end antiforgery telemetry: POST to a form endpoint without an antiforgery token -> HTTP 400 and
        //    EXACTLY ONE "Security HTTP antiforgery status 400" telemetry log entry.
        var capture = new CapturingLoggerProvider();
        using var instrumentedFactory = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureLogging(logging => logging.AddProvider(capture)));
        using var client = new MeritClient(instrumentedFactory);
        var acct = await SyntheticAccount.CreateAsync(instrumentedFactory, "af341");
        await acct.ScalarAsync<int>("""
            INSERT auth.userAssignment (userAssignmentId, appUserId, roleId, tenantId, includeDescendants, accessLevel, startDate)
            SELECT NEWID (), @id, r.roleId, t.tenantId, 1, 'full', '2026-01-01'
              FROM auth.tenant t JOIN auth.role r ON r.rootTenantId = t.rootTenantId AND r.roleCode = 'securityOfficer' AND r.isDeleted = 0
             WHERE t.tenantCode = 'MD-BCHD';
            UPDATE auth.appUser SET homeTenantId = (SELECT tenantId FROM auth.tenant WHERE tenantCode = 'MD-BCHD') WHERE appUserId = @id;
            SELECT COUNT (*) FROM auth.userAssignment WHERE appUserId = @id AND isDeleted = 0;
            """);

        await using (acct)
        {
            await acct.SignInAsync(client, MeritFixture.Password);
            capture.Messages.Clear();

            using var content = new FormUrlEncodedContent([]);
            using var resp = await client.NoRedirect.PostAsync("/account/signout", content);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

            var afLogs = capture.Messages.Where(m => m.Contains("Security HTTP antiforgery status 400", StringComparison.Ordinal)).ToList();
            Assert.Single(afLogs);
        }
    }

    private static Task SetContextAsync(SqlConnection conn, Guid scopeKey, Guid appUserId, string appUser, Guid actingTenantId)
        => conn.ExecuteAsync(
            """
            EXEC sys.sp_set_session_context @key = N'scopeKey', @value = @sk, @read_only = 1;
            EXEC sys.sp_set_session_context @key = N'appUserId', @value = @u, @read_only = 1;
            EXEC sys.sp_set_session_context @key = N'AppUser', @value = @name, @read_only = 1;
            EXEC sys.sp_set_session_context @key = N'actingTenantId', @value = @t, @read_only = 1;
            """, new { sk = scopeKey, u = appUserId, name = appUser, t = actingTenantId });

    private static Task SaveSyntheticPersonAsync(SqlConnection conn, Guid personId, Guid _)
        => conn.ExecuteAsync("dbo.uspSavePerson", new
        {
            personId,
            lastName = "RollbackTest",
            firstName = "WaveThree",
            lastNameKey = "ROLLBACKTEST",
            firstNameKey = "WAVETHREE",
            dob = new DateTime(2020, 5, 1),
            sex = "female",
            ethnicity = "unknown",
        }, commandType: CommandType.StoredProcedure);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception));
        }
    }
}
