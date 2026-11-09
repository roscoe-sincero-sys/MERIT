SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName: config.tvfWorkflowEligibility
Author: MERIT build
CreateDate: 2026-11-08
Description:
Explains agency, effective-date, population, age and result eligibility under the caller row-level security.
Requirements and Key Dependencies:
Scoped patient/case/task tables and auth.udfSessionToday; no dynamic SQL or elevated execution context.
Modification History:
2026-11-08 MERIT build - Program task definitions and explicit case-closure review (#205-207).
***********************************************************************************************************************/
CREATE OR ALTER FUNCTION config.tvfWorkflowEligibility
(@workflowDefinitionId UNIQUEIDENTIFIER, @personId UNIQUEIDENTIFIER, @exposureCaseId UNIQUEIDENTIFIER, @labResultId UNIQUEIDENTIFIER)
RETURNS TABLE
AS RETURN
WITH source AS
(
 SELECT d.*,p.personId,p.dob,c.exposureCaseId,c.analyteId AS caseAnalyteId,c.population AS casePopulation,r.labResultId,r.collectedDate,r.operator,r.valueCanonical,r.population AS reportedPopulation,
  COALESCE(c.owningTenantId,CASE d.routingPolicy WHEN 'definitionAgency' THEN d.owningTenantId
   WHEN 'servingJurisdiction' THEN CASE WHEN EXISTS(SELECT 1 FROM auth.tvfTenantWritePredicate(p.rootTenantId,dbo.udfPatientWorkOwner(p.personId),0))
    THEN dbo.udfPatientWorkOwner(p.personId) ELSE auth.udfSessionActingTenantId() END ELSE p.owningTenantId END) AS targetOwner,
  CASE d.triggerEvent WHEN 'receivedResult' THEN CAST(r.receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE d.timeZoneId AS DATE)
   WHEN 'caseOpened' THEN c.openedDate ELSE auth.udfSessionToday() END AS anchorDate,
  CASE WHEN d.triggerEvent='receivedResult' THEN r.ageAtCollectionMonths
   ELSE DATEDIFF(MONTH,p.dob,COALESCE(CASE WHEN d.triggerEvent='caseOpened' THEN c.openedDate END,auth.udfSessionToday()))
    - CASE WHEN DAY(COALESCE(CASE WHEN d.triggerEvent='caseOpened' THEN c.openedDate END,auth.udfSessionToday()))<DAY(p.dob) THEN 1 ELSE 0 END END AS ageMonths
 FROM config.workflowDefinition AS d JOIN dbo.person AS p ON p.personId=@personId AND p.isDeleted=0 AND p.mergedIntoPersonId IS NULL
 CROSS APPLY auth.tvfPersonPredicate(p.rootTenantId,p.owningTenantId,p.personId) clinicalAccess
 LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId=@exposureCaseId AND c.personId=p.personId AND c.isDeleted=0
 OUTER APPLY(SELECT TOP(1) r.* FROM dbo.labResult AS r WHERE r.personId=p.personId AND r.isDeleted=0
  AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected')
  AND (d.analyteSpecimenId IS NULL OR r.analyteSpecimenId=d.analyteSpecimenId)
  AND (@labResultId IS NULL OR r.labResultId=@labResultId) ORDER BY r.collectedDate DESC,r.createdDateUtc DESC,r.labResultId) AS r
 WHERE d.workflowDefinitionId=@workflowDefinitionId AND d.isDeleted=0
  AND (@exposureCaseId IS NULL OR c.exposureCaseId IS NOT NULL)
), grouped AS
(
 SELECT *,CASE WHEN triggerEvent='receivedResult' THEN reportedPopulation WHEN triggerEvent='caseOpened' THEN casePopulation
  WHEN dob IS NULL THEN 'unknown'
  WHEN EXISTS(SELECT 1 FROM dbo.pregnancyEpisode AS pe WHERE pe.personId=source.personId AND pe.isDeleted=0 AND pe.startDate<=anchorDate
   AND anchorDate<=COALESCE(pe.actualEndDate,pe.expectedEndDate,DATEADD(WEEK,COALESCE((SELECT TOP(1) TRY_CONVERT(INT,settingValue) FROM config.jurisdictionSetting
    WHERE rootTenantId=source.rootTenantId AND settingKey='pregnancyMaxWeeks' AND isDeleted=0 AND effectiveFrom<=anchorDate AND (effectiveTo IS NULL OR effectiveTo>=anchorDate) ORDER BY effectiveFrom DESC),42),pe.startDate))) THEN 'pregnant'
  WHEN ageMonths<12*COALESCE((SELECT TOP(1) TRY_CONVERT(INT,settingValue) FROM config.jurisdictionSetting WHERE rootTenantId=source.rootTenantId
   AND settingKey='adultAgeCutoffYears' AND isDeleted=0 AND effectiveFrom<=anchorDate AND (effectiveTo IS NULL OR effectiveTo>=anchorDate) ORDER BY effectiveFrom DESC),16) THEN 'child' ELSE 'adult' END AS resultPopulation
 FROM source
), decision AS
(
 SELECT *,CASE
  WHEN USER_NAME()<>N'dbo' AND NOT EXISTS(SELECT 1 FROM auth.tvfSessionWritePermission('config.edit') w WHERE w.tenantId=targetOwner)
   AND NOT EXISTS(SELECT 1 FROM auth.tvfSessionWritePermission('person.edit') w WHERE w.tenantId=targetOwner
    AND (w.tenantId=(SELECT owningTenantId FROM dbo.person WHERE personId=grouped.personId) OR EXISTS(SELECT 1 FROM dbo.personAccess pa WHERE pa.personId=grouped.personId AND pa.tenantId=w.tenantId AND pa.isDeleted=0)))
   THEN N'Outside this profile''s write authority for the responsible agency.'
  WHEN NOT EXISTS(SELECT 1 FROM auth.tenantClosure WHERE ancestorTenantId=owningTenantId AND descendantTenantId=targetOwner AND isDeleted=0) THEN N'Outside the definition agency and descendants.'
  WHEN @labResultId IS NOT NULL AND labResultId IS NULL THEN N'The source result does not match this substance/sample definition.'
  WHEN triggerEvent='caseOpened' AND analyteSpecimenId IS NOT NULL AND caseAnalyteId IS NOT NULL AND NOT EXISTS
   (SELECT 1 FROM config.analyteSpecimen WHERE analyteSpecimenId=grouped.analyteSpecimenId AND analyteId=grouped.caseAnalyteId) THEN N'The case substance does not match this definition.'
  WHEN triggerEvent='caseOpened' AND (exposureCaseId IS NULL OR NOT EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=grouped.exposureCaseId AND caseStatus<>'closed' AND isDeleted=0)) THEN N'An active case is required.'
  WHEN anchorDate IS NULL THEN N'Review: the source event is not available.'
  WHEN anchorDate<effectiveFrom OR (effectiveTo IS NOT NULL AND anchorDate>effectiveTo) THEN N'Outside the approved effective dates.'
  WHEN (minimumAgeMonths IS NOT NULL AND ageMonths<minimumAgeMonths) OR (maximumAgeMonths IS NOT NULL AND ageMonths>maximumAgeMonths) THEN N'Outside the approved age range.'
  WHEN (minimumAgeMonths IS NOT NULL OR maximumAgeMonths IS NOT NULL) AND ageMonths IS NULL THEN N'Review: birth date or source age is unknown.'
  WHEN analyteSpecimenId IS NOT NULL AND labResultId IS NULL THEN N'Review: no qualifying substance/sample result is available.'
  WHEN population<>'all' AND resultPopulation IS NULL THEN N'Review: population at the source event is unknown.'
  WHEN population<>'all' AND resultPopulation='unknown' THEN N'Review: source population is unknown.'
  WHEN population<>'all' AND resultPopulation<>population THEN N'The approved population does not match.'
  WHEN (minimumValue IS NOT NULL OR maximumValue IS NOT NULL) AND (valueCanonical IS NULL OR operator<>'=') THEN N'Review: a censored or missing result cannot establish the threshold.'
  WHEN (minimumValue IS NOT NULL AND valueCanonical<minimumValue) OR (maximumValue IS NOT NULL AND valueCanonical>=maximumValue) THEN N'Outside the approved threshold range.'
  WHEN requiresEligibilityReview=1 THEN N'Review: verify the definition-specific eligibility facts in the instructions.'
  ELSE N'The source event, agency, effective dates and configured conditions match.' END AS explanation
 FROM grouped
)
SELECT *,CASE WHEN explanation LIKE N'Review:%' THEN 'needsReview'
 WHEN explanation=N'The source event, agency, effective dates and configured conditions match.' THEN 'eligible' ELSE 'ineligible' END AS evaluationState
FROM decision;
GO
