using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Security;
using Merit.Data.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Merit.IntegrationTests;

public sealed partial class PatientCareTests
{
    private async Task CloneReviewResult(SqlConnection c, Guid id, string sample = "venous", string snapshot = "{}", decimal value = 1001, Guid? personOverride = null)
        => await Sql(c, """
            INSERT dbo.labResult (labResultId,rootTenantId,owningTenantId,personId,analyteSpecimenId,collectedDate,receivedUtc,resultStatus,
              valueReported,unitReported,valueCanonical,unitCanonical,labOrganizationId,reportedSnapshotJson,intakeBatchId,population,ageAtCollectionMonths,sampleType)
            SELECT @id,rootTenantId,owningTenantId,COALESCE(@person,personId),analyteSpecimenId,'2026-09-01','2026-10-01T16:00:00','final',
              @value,unitReported,@value,unitCanonical,labOrganizationId,@snapshot,intakeBatchId,'child',48,@sample
            FROM dbo.labResult WHERE labResultId=@original;
            """, ("@id", id), ("@original", fixture.Ids.MiaLabResultId), ("@sample", sample), ("@snapshot", snapshot), ("@value", value), ("@person", personOverride));

    private async Task AddReviewRule(SqlConnection c, Guid id, string mode, string recipient, string purpose = "notification", string? required = null)
    {
        var admin = await fixture.SignedInAsync("admin"); await admin.Http.GetStringAsync("/config/notification-rules");
        var specimen = await Sql(c, "SELECT analyteSpecimenId FROM dbo.labResult WHERE labResultId=@id", ("@id", fixture.Ids.MiaLabResultId));
        await Saved(await admin.NoRedirect.PostAsync("/config/notification-rules", Form(("notificationRuleId", id), ("ruleName", "Review test " + id),
            ("analyteSpecimenId", specimen), ("population", "all"), ("minimumValue", "1000"), ("recipientKind", recipient),
            ("dueInDays", purpose == "labReporting" ? "1" : "10"), ("dayBasis", "business"), ("timeZoneId", "Eastern Standard Time"),
            ("calendarName", "Synthetic explicit weekends-only test calendar"), ("effectiveFrom", "2024-01-01"), ("maximumAgeMonths", purpose == "labReporting" ? "227" : "71"),
            ("approvalReference", "Synthetic test policy"), ("approved", "true"), ("rulePurpose", purpose), ("eligibilityMode", mode), ("requiredFieldsCsv", required))));
    }

    private static async Task AsCareData(Func<ICareData, Task> action)
    {
        var session = new JobSession(); var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<ICurrentSession>(session); services.AddMeritApplication(); services.AddMeritSqlServer(o => o.ConnectionString = TestEnvironment.WebConnectionString);
        await using var provider = services.BuildServiceProvider(); var accounts = provider.GetRequiredService<IAccountData>();
        var user = await accounts.GetUserForSignInAsync("supervisor");
        session.Current = await accounts.CreateSessionScopeAsync(Guid.NewGuid(), user!.AppUserId, DateTime.UtcNow.AddMinutes(10));
        try { await action(provider.GetRequiredService<ICareData>()); }
        finally { await accounts.RevokeSessionScopeAsync(session.Current.Summary.SessionScopeId); }
    }

    private static Task CleanReview(SqlConnection c, Guid[] results, Guid[] rules, Guid? staging = null, Guid? caseId = null, Guid? patientId = null)
        => Sql(c, """
            DECLARE @tasks TABLE(id UNIQUEIDENTIFIER);
            INSERT @tasks SELECT workTaskId FROM dbo.notificationObligation WHERE notificationRuleId IN (SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@rules));
            DELETE dbo.notificationObligation WHERE notificationRuleId IN (SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@rules));
            DELETE dbo.workTask WHERE workTaskId IN (SELECT id FROM @tasks) OR labResultId IN (SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@results)) OR stagingRowId=@staging;
            DELETE dbo.personActivity WHERE labResultId IN (SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@results));
            DELETE dbo.exposureCase WHERE exposureCaseId=@case;
            DELETE dbo.stagingHoldReason WHERE stagingRowId=@staging; DELETE dbo.stagingRow WHERE stagingRowId=@staging;
            DELETE config.notificationRule WHERE notificationRuleId IN (SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@rules));
            DELETE dbo.labResult WHERE labResultId IN (SELECT CONVERT(UNIQUEIDENTIFIER,[value]) FROM OPENJSON(@results));
            DELETE dbo.personRelationship WHERE personId=@patient; DELETE dbo.personAccess WHERE personId=@patient; DELETE dbo.person WHERE personId=@patient;
            """, ("@results", JsonSerializer.Serialize(results)), ("@rules", JsonSerializer.Serialize(rules)), ("@staging", staging), ("@case", caseId), ("@patient", patientId));

    [SqlFact]
    public async Task MarylandConfirmationChecks84DaysDiscordanceLegalRecipientAndNoticeForms()
    {
        var rule = Guid.NewGuid(); var result = Guid.NewGuid(); var prior = Guid.NewGuid(); var discordant = Guid.NewGuid();
        var primaryOnly = Guid.NewGuid(); var relationship = Guid.NewGuid();
        var person = Guid.NewGuid(); var caseId = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await Sql(c, """
                INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
                SELECT @person,rootTenantId,owningTenantId,N'Maryland confirmation test',N'MDCONFIRMATIONTEST','2022-09-01' FROM dbo.person WHERE personId=@original;
                INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,isPrimaryContact,hasLegalCustody,source)
                SELECT TOP(1) NEWID(),rootTenantId,owningTenantId,@person,relatedPersonId,'parent',1,1,'caseManager' FROM dbo.personRelationship WHERE personId=@original AND role='parent' AND isDeleted=0;
                """, ("@person", person), ("@original", fixture.Ids.MiaPersonId));
            await CloneReviewResult(c, result, "capillary", personOverride: person); await CloneReviewResult(c, prior, "capillary", personOverride: person);
            await Sql(c, """
                INSERT dbo.exposureCase(exposureCaseId,rootTenantId,owningTenantId,caseNumber,personId,analyteId,population,classification,computedClassification,caseStatus,qualifyingLabResultId,workflowTemplate,openedDate)
                SELECT @case,rootTenantId,owningTenantId,CONCAT('MDTEST-',LEFT(CONVERT(VARCHAR(36),@case),8)),@person,analyteId,population,classification,computedClassification,'open',@result,workflowTemplate,'2026-09-01'
                FROM dbo.exposureCase WHERE exposureCaseId=@original;
                """, ("@case", caseId), ("@person", person), ("@result", result), ("@original", fixture.Ids.MiaCaseId));
            await Sql(c, "UPDATE dbo.labResult SET collectedDate=DATEADD(DAY,-85,'2026-09-01') WHERE labResultId=@id", ("@id", prior));
            await AddReviewRule(c, rule, "mdEbl", "caregiver");
            var user = await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/people/{person}/care");
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/evaluate", Form()));
            var obligation = (Guid)(await Sql(c, "SELECT notificationObligationId FROM dbo.notificationObligation WHERE labResultId=@id AND notificationRuleId=@rule", ("@id", result), ("@rule", rule)))!;
            async Task<HttpResponseMessage> Review(bool confirm) => await user.NoRedirect.PostAsync($"/people/{person}/care/review", Form(("notificationObligationId", obligation), ("applies", "true"), ("reason", "Verified confirmation and legal caregiver in source reports"), ("criteriaConfirmed", confirm ? "true" : "false")));
            using (var unconfirmed = await Review(false)) Assert.Equal(HttpStatusCode.UnprocessableEntity, unconfirmed.StatusCode);
            using (var tooFarApart = await Review(true)) Assert.Equal(HttpStatusCode.UnprocessableEntity, tooFarApart.StatusCode);
            await Sql(c, "UPDATE dbo.labResult SET collectedDate=DATEADD(DAY,-84,'2026-09-01') WHERE labResultId=@id", ("@id", prior));
            await Saved(await Review(true));
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@id AND queueCode='assessmentContact' AND dueDate IS NULL", ("@id", result)));
            var recipient = (Guid)(await Sql(c, "SELECT TOP(1) relatedPersonId FROM dbo.personRelationship WHERE personId=@id AND isDeleted=0 AND role='parent'", ("@id", person)))!;
            await Sql(c, """
                INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey)
                SELECT @contact,rootTenantId,owningTenantId,N'Primary contact test',N'PRIMARYCONTACTTEST' FROM dbo.person WHERE personId=@person;
                INSERT dbo.personRelationship(personRelationshipId,rootTenantId,owningTenantId,personId,relatedPersonId,role,isPrimaryContact,hasLegalCustody,source)
                SELECT @relation,rootTenantId,owningTenantId,@person,@contact,'caregiver',1,0,'caseManager' FROM dbo.person WHERE personId=@person;
                """, ("@contact", primaryOnly), ("@relation", relationship), ("@person", person));
            async Task<HttpResponseMessage> Complete(string? form, Guid? overrideRecipient = null) => await user.NoRedirect.PostAsync($"/people/{person}/care/activity", Form(("personActivityId", Guid.NewGuid()), ("caseId", caseId),
                ("activityType", "notificationCompleted"), ("activityDate", "2026-10-02"), ("audience", "caregiver"), ("channel", "letter"), ("outcome", "completed"),
                ("contactPersonId", overrideRecipient ?? recipient), ("labResultId", result), ("notificationObligationId", obligation), ("note", "Department notice delivered to the recorded parent"), ("confirmed", "true"), ("noticeFormReference", form)));
            using (var missingForm = await Complete(null)) Assert.Equal(HttpStatusCode.UnprocessableEntity, missingForm.StatusCode);
            using (var notLegal = await Complete("Department form TEST-1", primaryOnly)) Assert.Equal(HttpStatusCode.UnprocessableEntity, notLegal.StatusCode);
            await CloneReviewResult(c, discordant, "capillary", value: 1, personOverride: person);
            using (var changedEvidence = await Complete("Department form TEST-1")) Assert.Equal(HttpStatusCode.UnprocessableEntity, changedEvidence.StatusCode);
            await Sql(c, "UPDATE dbo.labResult SET sampleType='venous' WHERE labResultId=@id", ("@id", result));
            await Saved(await Complete("Department form TEST-1 / attached original"));
            Assert.Equal("Department form TEST-1 / attached original", await Sql(c, "SELECT noticeFormReference FROM dbo.personActivity WHERE labResultId=@id AND activityType='notificationCompleted'", ("@id", result)));
        }
        finally
        {
            await CleanReview(c, [result, prior, discordant], [rule], caseId: caseId, patientId: person);
            await Sql(c, "DELETE dbo.personRelationship WHERE personRelationshipId=@relation; DELETE dbo.person WHERE personId=@contact", ("@relation", relationship), ("@contact", primaryOnly));
        }
    }

    [SqlFact]
    public async Task MarylandOwnerNoticeRequiresReviewedScopeEveryOwnerAndDepartmentForm()
    {
        var rule = Guid.NewGuid(); var result = Guid.NewGuid(); var person = fixture.Ids.MiaPersonId;
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await CloneReviewResult(c, result); await AddReviewRule(c, rule, "mdOwner", "owner");
            var user = await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/people/{person}/care");
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/evaluate", Form()));
            var obligation = (Guid)(await Sql(c, "SELECT notificationObligationId FROM dbo.notificationObligation WHERE labResultId=@id AND notificationRuleId=@rule", ("@id", result), ("@rule", rule)))!;
            Assert.Equal("needsReview", await Sql(c, "SELECT evaluationState FROM dbo.notificationObligation WHERE notificationObligationId=@id", ("@id", obligation)));
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/review", Form(("notificationObligationId", obligation), ("applies", "true"), ("criteriaConfirmed", "true"), ("reason", "Verified non-owned primary residence, current owner and source confirmation report"))));
            async Task<HttpResponseMessage> Complete(bool all) => await user.NoRedirect.PostAsync($"/people/{person}/care/activity", Form(("personActivityId", Guid.NewGuid()), ("caseId", fixture.Ids.MiaCaseId),
                ("activityType", "notificationCompleted"), ("activityDate", "2026-10-02"), ("audience", "owner"), ("channel", "letter"), ("outcome", "completed"),
                ("recipientLabel", "Example Property LLC"), ("labResultId", result), ("notificationObligationId", obligation), ("note", "Owner Example Property LLC, 100 Test Street; first-class approved notice delivered October 2"),
                ("confirmed", "true"), ("ownerScopeConfirmed", all ? "true" : "false"), ("noticeFormReference", "Department owner notice TEST-2")));
            using (var incomplete = await Complete(false)) Assert.Equal(HttpStatusCode.UnprocessableEntity, incomplete.StatusCode);
            await Saved(await Complete(true));
            Assert.Equal(true, await Sql(c, "SELECT ownerScopeConfirmed FROM dbo.personActivity WHERE labResultId=@id AND activityType='notificationCompleted'", ("@id", result)));
            Assert.Contains("Example Property LLC", await user.Http.GetStringAsync("/notifications"), StringComparison.Ordinal);
        }
        finally { await CleanReview(c, [result], [rule]); }
    }

    [SqlFact]
    public async Task LabLatenessUsesExplicitFinalDateAndDetectionTimeWhileMissingReceiptStaysPending()
    {
        var rule = Guid.NewGuid(); var result = Guid.NewGuid(); var unknown = Guid.NewGuid(); var person = fixture.Ids.MiaPersonId;
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await AddReviewRule(c, rule, "mdReporting", "department", "labReporting", "phone");
            await CloneReviewResult(c, result, "capillary", "{\"finalResultDate\":\"2026-09-01\",\"state\":\"MD\"}");
            await CloneReviewResult(c, unknown, "capillary", "{\"state\":\"MD\",\"verifiedFinalResultDate\":\"2026-09-01\",\"labReportingEligibilityVerified\":\"true\",\"labReportingExcluded\":\"true\"}");
            await Sql(c, "UPDATE dbo.labResult SET reportedDate='2026-09-01',analyzedDate='2026-09-01' WHERE labResultId=@id", ("@id", unknown));
            var started = DateTime.UtcNow.AddSeconds(-2);
            var user = await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/people/{person}/care");
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/evaluate", Form()));
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/evaluate", Form()));
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@id AND title=N'Late laboratory report detected' AND taskStatus='done'", ("@id", result)));
            Assert.True((DateTime)(await Sql(c, "SELECT completedUtc FROM dbo.workTask WHERE labResultId=@id AND title=N'Late laboratory report detected'", ("@id", result)))! >= started);
            Assert.Equal(0, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@id AND title=N'Late laboratory report detected'", ("@id", unknown)));
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@id AND queueCode='labReportingReview' AND taskStatus='open'", ("@id", unknown)));
            var task = (Guid)(await Sql(c, "SELECT workTaskId FROM dbo.workTask WHERE labResultId=@id AND queueCode='missingInformation'", ("@id", result)))!;
            using (var bulk = await user.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { task }, action = "complete" })) Assert.Equal(HttpStatusCode.UnprocessableEntity, bulk.StatusCode);
            await Saved(await user.NoRedirect.PostAsync("/receipt-quality/received", Form(("workTaskId", task), ("personId", person), ("receivedDate", "2026-10-02"), ("evidence", "Laboratory addendum with phone received October 2, all requested fields verified"), ("confirmed", "true"))));
            Assert.Equal("done", await Sql(c, "SELECT taskStatus FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            Assert.Equal(new DateTime(2026,10,2), await Sql(c, "SELECT CAST(completedUtc AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' AS DATE) FROM dbo.workTask WHERE workTaskId=@id", ("@id", task)));
            await Saved(await user.NoRedirect.PostAsync("/receipt-quality/timeline", Form(("personId", person), ("labResultId", unknown), ("finalResultDate", "2026-09-01"), ("evidence", "Verified original final report and Maryland child eligibility"), ("confirmed", "true"), ("applies", "true"))));
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@id AND title=N'Late laboratory report detected'", ("@id", unknown)));
        }
        finally { await CleanReview(c, [result, unknown], [rule]); }
    }

    [SqlFact]
    public async Task DueDateReceiptNeedsAnExplicitCloseOfBusinessReview()
    {
        var rule = Guid.NewGuid(); var result = Guid.NewGuid(); var person = fixture.Ids.MiaPersonId;
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await AddReviewRule(c, rule, "mdReporting", "department", "labReporting");
            await CloneReviewResult(c, result, snapshot: "{\"finalResultDate\":\"2026-09-01\",\"state\":\"MD\"}");
            await Sql(c, "UPDATE dbo.labResult SET receivedUtc='2026-09-02T22:00:00' WHERE labResultId=@id", ("@id", result));
            var user = await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/people/{person}/care");
            await Saved(await user.NoRedirect.PostAsync($"/people/{person}/care/evaluate", Form()));
            Assert.Equal(0, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@id AND title=N'Late laboratory report detected'", ("@id", result)));
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@id AND queueCode='labReportingReview' AND taskStatus='open'", ("@id", result)));
            await Saved(await user.NoRedirect.PostAsync("/receipt-quality/timeline", Form(("personId", person), ("labResultId", result), ("finalResultDate", "2026-09-01"),
                ("confirmed", "true"), ("applies", "true"), ("sameDayChecked", "true"), ("lateConfirmed", "true"),
                ("evidence", "Synthetic approved cutoff 17:00 Eastern; Department receipt confirmed 18:00 Eastern on due date; original final report verified"))));
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE labResultId=@id AND title=N'Late laboratory report detected' AND taskStatus='done'", ("@id", result)));
            Assert.Equal(rule, await Sql(c, "SELECT reportingVerifiedPolicyId FROM dbo.labResult WHERE labResultId=@id", ("@id", result)));
            Assert.Equal(true, await Sql(c, "SELECT reportingLateConfirmed FROM dbo.labResult WHERE labResultId=@id AND reportingVerifiedByUserId IS NOT NULL", ("@id", result)));
        }
        finally { await CleanReview(c, [result], [rule]); }
    }

    [SqlFact]
    public async Task MarylandDiscretionaryClosureRetainsAndThenReleasesOwnerLetterFollowupAccess()
    {
        var person = Guid.NewGuid(); var caseId = Guid.NewGuid(); var result = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await Sql(c, """
                INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,lastNameKey,dob)
                SELECT @person,rootTenantId,rootTenantId,N'Closure followup test',N'CLOSUREFOLLOWUPTEST','2022-01-01' FROM dbo.person WHERE personId=@originalPerson;
                """, ("@person", person), ("@originalPerson", fixture.Ids.MiaPersonId));
            await CloneReviewResult(c, result, personOverride: person);
            await Sql(c, """
                INSERT dbo.exposureCase(exposureCaseId,rootTenantId,owningTenantId,caseNumber,personId,analyteId,population,classification,computedClassification,caseStatus,qualifyingLabResultId,workflowTemplate,openedDate)
                SELECT @case,rootTenantId,owningTenantId,CONCAT('MDCLOSE-',LEFT(CONVERT(VARCHAR(36),@case),8)),@person,analyteId,population,classification,computedClassification,'open',@result,workflowTemplate,'2026-09-01'
                FROM dbo.exposureCase WHERE exposureCaseId=@originalCase;
                INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,labResultId,queueCode,title)
                SELECT NEWID(),rootTenantId,owningTenantId,@person,@case,@result,'assessmentContact',N'Schedule environmental assessment' FROM dbo.exposureCase WHERE exposureCaseId=@case;
                """, ("@case", caseId), ("@person", person), ("@result", result), ("@originalCase", fixture.Ids.MiaCaseId));
            var user = await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/cases/{caseId}");
            var version = (byte[])(await Sql(c, "SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id", ("@id", caseId)))!;
            using var closed = await user.NoRedirect.PostAsync($"/cases/{caseId}/close", Form(("closureReason", "lostToFollowUp"), ("closedDate", "2026-10-03"),
                ("note", "Two telephone attempts recorded; first-class final scheduling letter sent with October 2 response date, required rights and case closure warning; no response. Discretionary closure approved."), ("rowVersion", "0x" + Convert.ToHexString(version))));
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            var task = (Guid)(await Sql(c, "SELECT workTaskId FROM dbo.workTask WHERE exposureCaseId=@id AND queueCode='assessmentOwnerClosure' AND taskStatus='open'", ("@id", caseId)))!;
            Assert.Equal(1, await Sql(c, "SELECT COUNT(*) FROM dbo.personAccess WHERE personId=@person AND tenantId=@owner AND accessReason='case' AND isDeleted=0", ("@person", person), ("@owner", fixture.Ids.BaltimoreCityTenantId)));
            var city = await fixture.SignedInAsync("cmbalt"); await city.Http.GetStringAsync($"/people/{person}/care?caseId={caseId}");
            using var completed = await city.NoRedirect.PostAsJsonAsync("/api/v1/tasks/bulk", new { taskIds = new[] { task }, action = "complete", note = "Primary residence not caregiver-owned; first-class closure letter mailed to the verified property owner. Department letter copy retained with recipient/address." });
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            Assert.Equal(0, await Sql(c, "SELECT COUNT(*) FROM dbo.personAccess WHERE personId=@person AND tenantId=@owner AND accessReason='case' AND isDeleted=0", ("@person", person), ("@owner", fixture.Ids.BaltimoreCityTenantId)));
        }
        finally
        {
            await Sql(c, "DELETE dbo.workTask WHERE exposureCaseId=@case; DELETE dbo.caseActivity WHERE exposureCaseId=@case;", ("@case", caseId));
            await CleanReview(c, [result], [], caseId: caseId, patientId: person);
        }
    }

    [SqlFact]
    public async Task UnmatchedMissingInformationFindingsRetainDateWhenLinkedToPatient()
    {
        var stage = Guid.NewGuid(); var result = Guid.NewGuid();
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        try
        {
            await Sql(c, """
                INSERT dbo.stagingRow(stagingRowId,rootTenantId,owningTenantId,intakeBatchId,rowNumber,parsedJson,rowStatus)
                SELECT @stage,rootTenantId,owningTenantId,intakeBatchId,999999,N'{"lastName":"Receipt test","dob":null}', 'held' FROM dbo.labResult WHERE labResultId=@original;
                INSERT dbo.stagingHoldReason(stagingHoldReasonId,rootTenantId,owningTenantId,stagingRowId,reasonCode,fieldName,reasonMessage)
                SELECT NEWID(),rootTenantId,owningTenantId,@stage,'missingDob','dob',N'Date of birth missing' FROM dbo.stagingRow WHERE stagingRowId=@stage;
                """, ("@stage", stage), ("@original", fixture.Ids.MiaLabResultId));
            await AsCareData(async d => { await d.ReconcileReceiptAsync(null, stage, default); await d.ReconcileReceiptAsync(null, stage, default); });
            await AsCareData(async d => await Assert.ThrowsAsync<Merit.Application.Errors.NotFoundException>(
                () => d.ReconcileReceiptAsync(fixture.Ids.MiaLabResultId, Guid.NewGuid(), default)));
            Assert.Equal(2, await Sql(c, "SELECT COUNT(*) FROM dbo.workTask WHERE stagingRowId=@id", ("@id", stage)));
            var detected = await Sql(c, "SELECT completedUtc FROM dbo.workTask WHERE stagingRowId=@id AND queueCode='receiptFinding'", ("@id", stage));
            await CloneReviewResult(c, result);
            await Sql(c, "UPDATE dbo.stagingRow SET labResultId=@result,rowStatus='loaded',correctedJson=N'{\"dob\":\"2022-01-01\"}' WHERE stagingRowId=@stage; UPDATE dbo.stagingHoldReason SET isResolved=1 WHERE stagingRowId=@stage", ("@result", result), ("@stage", stage));
            await AsCareData(d => d.ReconcileReceiptAsync(null, stage, default));
            Assert.Equal(detected, await Sql(c, "SELECT completedUtc FROM dbo.workTask WHERE stagingRowId=@id AND queueCode='receiptFinding'", ("@id", stage)));
            Assert.Equal(fixture.Ids.MiaPersonId, await Sql(c, "SELECT personId FROM dbo.workTask WHERE stagingRowId=@id AND queueCode='receiptFinding'", ("@id", stage)));
            Assert.Equal("open", await Sql(c, "SELECT taskStatus FROM dbo.workTask WHERE stagingRowId=@id AND queueCode='missingInformation'", ("@id", stage)));
        }
        finally { await CleanReview(c, [result], [], stage); }
    }
}
