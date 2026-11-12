using System.Net;
using Microsoft.Data.SqlClient;

namespace Merit.IntegrationTests;

public sealed partial class PatientCareTests
{
    [SqlFact]
    public async Task ConfiguredReferenceHistoryControlsConfirmationDischargeAndCaseClosure()
    {
        await using var c = new SqlConnection(TestEnvironment.AdminConnectionString()); await c.OpenAsync();
        var person = await TestingPatient(c); var caseId = Guid.NewGuid(); var reference = Guid.NewGuid();
        try
        {
            await Sql(c, """
                INSERT config.threshold(thresholdId,rootTenantId,analyteSpecimenId,population,thresholdType,lowerValue,unit,comparator,effectiveFrom,isActive)
                SELECT @id,rootTenantId,analyteSpecimenId,'child','case',5,N'ug/dL','>=','2026-02-01',1
                  FROM config.threshold WHERE rootTenantId=(SELECT rootTenantId FROM dbo.person WHERE personId=@p)
                   AND population='child' AND thresholdType='case' AND isActive=1 AND sampleType IS NULL AND effectiveFrom='2026-01-01';
                """, ("@id", reference), ("@p", person));
            // A later 4 is below the new reference; a 4 on the earlier draw date remains elevated at 3.5.
            var old = await TestingResult(c, person, "2026-01-01", 4);
            Assert.Equal(3.5m, await Sql(c, "SELECT config.udfTestingReferenceLevel(rootTenantId,'2026-01-01',dob) FROM dbo.person WHERE personId=@p", ("@p", person)));
            var below = await TestingResult(c, person, "2026-02-01", 4);
            using (var refused = await Plan(person, Guid.NewGuid(), "confirmation", below))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            // Review history remains anchored to the earlier elevated source, even after the reference changes.
            var confirmation = Guid.NewGuid(); await Saved(await Plan(person, confirmation, "confirmation", old));
            Assert.Equal("3.500000", await Sql(c, "SELECT JSON_VALUE(testingPlanJson,'$.referenceLevel') FROM dbo.workTask WHERE workTaskId=@id", ("@id", confirmation)));
            await Sql(c, "UPDATE dbo.workTask SET taskStatus='dismissed',resolutionNote=N'Synthetic clinician review',completedUtc=SYSUTCDATETIME() WHERE workTaskId=@id", ("@id", confirmation));
            await Sql(c, """
                INSERT dbo.exposureCase(exposureCaseId,rootTenantId,owningTenantId,caseNumber,personId,analyteId,population,classification,computedClassification,caseStatus,qualifyingLabResultId,workflowTemplate,openedDate)
                SELECT @case,rootTenantId,owningTenantId,CONCAT('REF-',LEFT(CONVERT(VARCHAR(36),@case),8)),@p,analyteId,population,classification,computedClassification,'open',@r,workflowTemplate,'2026-01-01'
                FROM dbo.exposureCase WHERE exposureCaseId=@original;
                """, ("@case", caseId), ("@p", person), ("@r", old), ("@original", fixture.Ids.MiaCaseId));
            var last = await TestingResult(c, person, "2026-05-01", 4, "venous");
            // Both lows (4) are below 5 at their draw dates, and would have failed the previous literal 3.5 check.
            await Saved(await Plan(person, Guid.NewGuid(), "discharge", last, caseId, true));
            var user = await fixture.SignedInAsync("supervisor"); await user.Http.GetStringAsync($"/cases/{caseId}");
            var version = (byte[])(await Sql(c, "SELECT rowVersion FROM dbo.exposureCase WHERE exposureCaseId=@id", ("@id", caseId)))!;
            using var closed = await user.NoRedirect.PostAsync($"/cases/{caseId}/close", Form(("closureReason", "belowLevelOnRetest"),
                ("closedDate", "2026-10-01"), ("note", "Synthetic remediation and testing evidence reviewed"), ("rowVersion", "0x" + Convert.ToHexString(version))));
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            Assert.Equal("closed", await Sql(c, "SELECT caseStatus FROM dbo.exposureCase WHERE exposureCaseId=@id", ("@id", caseId)));
        }
        finally
        {
            await CleanTesting(c, person);
            await Sql(c, "DELETE config.threshold WHERE thresholdId=@id", ("@id", reference));
        }
    }
}
