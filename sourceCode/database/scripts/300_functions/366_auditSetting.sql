SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/* Immutable audit history must not copy arbitrary setting keys or free-text values. */
CREATE OR ALTER FUNCTION config.udfAuditSettingKey(@key VARCHAR(80))
RETURNS VARCHAR(80)
AS
BEGIN
    RETURN CASE WHEN @key COLLATE Latin1_General_100_BIN2 IN
      ('agencyName','statePostalCode','stateFips','adultAgeCutoffYears','pregnancyMaxWeeks','primaryResidencePolicy',
       'investigationLookbackDays','siteInvestigationMinHours','contactAttemptsBeforeLetter','localNotification',
       'arsenicHandling','occupationalReferralAgency','confirmingMethods','unreportedMethodConfirms','matchingPreset',
       'cblsEnabled','ablesEnabled','providerLookups','emailCodesEnabled','smsCodesEnabled','adultCaseOwnerTenantCode',
       'clusterWindowDays','clusterMinCount','passwordMinLength','sessionTimeoutMinutes','testingPolicyVersion')
      THEN @key END;
END;
GO
CREATE OR ALTER FUNCTION config.udfAuditSettingValue(@key VARCHAR(80),@value NVARCHAR(2000))
RETURNS NVARCHAR(2000)
AS
BEGIN
    DECLARE @safe NVARCHAR(2000);
    IF @key COLLATE Latin1_General_100_BIN2 IN ('adultAgeCutoffYears','pregnancyMaxWeeks','investigationLookbackDays',
       'siteInvestigationMinHours','contactAttemptsBeforeLetter','clusterWindowDays','clusterMinCount','passwordMinLength','sessionTimeoutMinutes')
      SET @safe=CASE WHEN @value=N'NOTSET' THEN N'NOTSET' ELSE CONVERT(NVARCHAR(40),TRY_CONVERT(INT,@value)) END;
    ELSE IF @key COLLATE Latin1_General_100_BIN2 IN ('localNotification','unreportedMethodConfirms','cblsEnabled','ablesEnabled','emailCodesEnabled','smsCodesEnabled')
      SET @safe=CASE WHEN LOWER(TRIM(@value)) IN (N'true',N'false') THEN LOWER(TRIM(@value)) END;
    ELSE IF @key COLLATE Latin1_General_100_BIN2='testingPolicyVersion'
      SET @safe=CASE WHEN @value COLLATE Latin1_General_100_BIN2 IN (N'none',N'MDH-2025-03') THEN @value END;
    ELSE IF @key COLLATE Latin1_General_100_BIN2='matchingPreset'
      SET @safe=CASE WHEN @value COLLATE Latin1_General_100_BIN2 IN (N'strict',N'balanced',N'lenient',N'standard') THEN @value END;
    ELSE IF @key COLLATE Latin1_General_100_BIN2='primaryResidencePolicy'
      SET @safe=CASE WHEN @value COLLATE Latin1_General_100_BIN2 IN (N'review',N'move') THEN @value END;
    ELSE IF @key COLLATE Latin1_General_100_BIN2='arsenicHandling'
      SET @safe=CASE WHEN @value COLLATE Latin1_General_100_BIN2 IN (N'task',N'case') THEN @value END;
    ELSE IF @key COLLATE Latin1_General_100_BIN2='providerLookups'
      SET @safe=CASE WHEN @value COLLATE Latin1_General_100_BIN2 IN (N'live',N'file') THEN @value END;
    ELSE IF @key COLLATE Latin1_General_100_BIN2='statePostalCode'
      SET @safe=CASE WHEN DATALENGTH(@value)=4 AND @value COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^A-Z]%' THEN @value END;
    ELSE IF @key COLLATE Latin1_General_100_BIN2='stateFips'
      SET @safe=CASE WHEN DATALENGTH(@value)=4 AND @value COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^0-9]%' THEN @value END;
    RETURN @safe;
END;
GO
EXEC util.uspSetObjectDescription @SchemaName=N'config',@ObjectType=N'FUNCTION',@ObjectName=N'udfAuditSettingKey',
 @Description=N'Known configuration keys safe for immutable audit history. Unknown extension keys are redacted, not copied as arbitrary text.';
EXEC util.uspSetObjectDescription @SchemaName=N'config',@ObjectType=N'FUNCTION',@ObjectName=N'udfAuditSettingValue',
 @Description=N'Canonical numeric values and allowlisted configuration codes for audit snapshots. Free text, unknown values and extension settings return NULL; application setting behavior is unchanged.';
GO
