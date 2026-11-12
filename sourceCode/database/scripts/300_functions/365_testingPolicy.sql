SET QUOTED_IDENTIFIER ON;
GO
/* Issue 285. Policy adoption is explicit and belongs to the patient's data root. Reported state cannot activate it. */
CREATE OR ALTER FUNCTION config.udfActiveTestingPolicy(@root UNIQUEIDENTIFIER,@asOf DATE)
RETURNS VARCHAR(40)
AS
BEGIN
    RETURN (SELECT TOP(1) CASE WHEN s.settingValue=N'MDH-2025-03' THEN CONVERT(VARCHAR(40),s.settingValue) END
      FROM config.jurisdictionSetting s JOIN auth.tenant t ON t.tenantId=s.rootTenantId
     WHERE s.rootTenantId=@root AND t.statePostalCode='MD' AND t.isDeleted=0
       AND s.settingKey='testingPolicyVersion' AND s.isDeleted=0
       AND s.effectiveFrom<=@asOf AND (s.effectiveTo IS NULL OR s.effectiveTo>=@asOf)
     ORDER BY s.effectiveFrom DESC);
END;
GO
EXEC util.uspSetObjectDescription @SchemaName=N'config',@ObjectType=N'FUNCTION',@ObjectName=N'udfActiveTestingPolicy',
 @Description=N'The explicitly adopted testing policy effective on the agency date for this patient data root. The MDH policy is supported only for Maryland; no policy is the default.';
GO
/* Reference history is evaluated at each source draw. Missing or conflicting effective levels fail closed. */
CREATE OR ALTER FUNCTION config.udfTestingReferenceLevel(@root UNIQUEIDENTIFIER,@draw DATE,@dob DATE)
RETURNS DECIMAL(18,6)
AS
BEGIN
    DECLARE @from DATE,@level DECIMAL(18,6);
    SELECT @from=MAX(t.effectiveFrom)
      FROM config.threshold t JOIN config.analyteSpecimen s ON s.analyteSpecimenId=t.analyteSpecimenId
      JOIN config.analyte a ON a.analyteId=s.analyteId
     WHERE t.rootTenantId=@root AND t.thresholdType='case' AND t.population='child' AND t.sampleType IS NULL
       AND t.comparator='>=' AND t.upperValue IS NULL AND t.unit=N'ug/dL'
       AND a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL'
       AND t.isActive=1 AND t.isDeleted=0 AND t.effectiveFrom<=@draw AND (t.effectiveTo IS NULL OR t.effectiveTo>=@draw)
       AND (t.ageMinMonths IS NULL OR @draw>=DATEADD(MONTH,t.ageMinMonths,@dob))
       AND (t.ageMaxMonths IS NULL OR @draw<DATEADD(MONTH,t.ageMaxMonths+1,@dob));
    SELECT @level=CASE WHEN COUNT(DISTINCT t.lowerValue)=1 THEN MIN(t.lowerValue) END
      FROM config.threshold t JOIN config.analyteSpecimen s ON s.analyteSpecimenId=t.analyteSpecimenId
      JOIN config.analyte a ON a.analyteId=s.analyteId
     WHERE t.rootTenantId=@root AND t.thresholdType='case' AND t.population='child' AND t.sampleType IS NULL
       AND t.comparator='>=' AND t.upperValue IS NULL AND t.unit=N'ug/dL'
       AND a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL'
       AND t.isActive=1 AND t.isDeleted=0 AND t.effectiveFrom=@from AND (t.effectiveTo IS NULL OR t.effectiveTo>=@draw)
       AND (t.ageMinMonths IS NULL OR @draw>=DATEADD(MONTH,t.ageMinMonths,@dob))
       AND (t.ageMaxMonths IS NULL OR @draw<DATEADD(MONTH,t.ageMaxMonths+1,@dob));
    RETURN CASE WHEN @level>0 THEN @level END;
END;
GO
EXEC util.uspSetObjectDescription @SchemaName=N'config',@ObjectType=N'FUNCTION',@ObjectName=N'udfTestingReferenceLevel',
 @Description=N'The unambiguous active child whole-blood lead reference level from effective-dated case thresholds at the source draw and patient age. Missing or conflicting levels return NULL.';
GO
