SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName:   config.tvfSettingCatalogue
Author:       Claude (Anthropic coding agent) for roscoe-sincero-sys
CreateDate:   2026-11-12
========================================================================================================================
Description:

The jurisdiction settings MERIT knows: one row per key with its value kind, numeric range or allowed codes, whether
NOTSET is accepted, and whether any code reads the setting. config.uspSaveSetting refuses a key that is not listed,
a value outside its definition, and any change to a setting that is not in use.

========================================================================================================================
Requirements and Key Dependencies:

None. Mirrors Merit.Application.Reference.SettingCatalogue; an integration test compares the two.

========================================================================================================================
Notes:

valueKind: integer, decimal, boolean, code (one of allowedCodes), codeList (comma-separated allowedCodes),
postalCode (two capital letters), fips (two digits), tenantCode (an active tenant of the data root), text.
inUse = 0 marks a setting that is stored for a feature not yet built: it is shown read-only and cannot be changed.

========================================================================================================================
Example Usage and Performance:

SELECT * FROM config.tvfSettingCatalogue ();

========================================================================================================================
Modification History:

Date:		2026-11-12
Author:		Claude (Anthropic coding agent)
Ticket:		289
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION config.tvfSettingCatalogue ()
RETURNS TABLE
AS
RETURN
    SELECT c.settingKey, c.valueKind, c.minValue, c.maxValue, c.allowedCodes, c.allowNotSet, c.inUse
      FROM (VALUES
          ('statePostalCode',             'postalCode', NULL, NULL, NULL, 0, 1)
        , ('stateFips',                   'fips',       NULL, NULL, NULL, 0, 1)
        , ('agencyName',                  'text',       NULL, NULL, NULL, 0, 0)
        , ('adultAgeCutoffYears',         'integer',    12,   21,   NULL, 0, 1)
        , ('pregnancyMaxWeeks',           'integer',    20,   52,   NULL, 0, 1)
        , ('primaryResidencePolicy',      'code',       NULL, NULL, 'review,move', 0, 1)
        , ('investigationLookbackDays',   'integer',    1,    3650, NULL, 1, 1)
        , ('siteInvestigationMinHours',   'decimal',    0.5,  168,  NULL, 1, 1)
        , ('contactAttemptsBeforeLetter', 'integer',    1,    20,   NULL, 1, 1)
        , ('localNotification',           'boolean',    NULL, NULL, NULL, 0, 0)
        , ('arsenicHandling',             'code',       NULL, NULL, 'task,case', 0, 1)
        , ('occupationalReferralAgency',  'text',       NULL, NULL, NULL, 0, 0)
        , ('confirmingMethods',           'codeList',   NULL, NULL, 'icpMs,graphiteFurnaceAa,anodicStrippingVoltammetry,pointOfCare,other,unknown', 0, 1)
        , ('unreportedMethodConfirms',    'boolean',    NULL, NULL, NULL, 0, 1)
        , ('matchingPreset',              'code',       NULL, NULL, 'strict,balanced,lenient', 0, 0)
        , ('cblsEnabled',                 'boolean',    NULL, NULL, NULL, 0, 1)
        , ('ablesEnabled',                'boolean',    NULL, NULL, NULL, 0, 1)
        , ('providerLookups',             'code',       NULL, NULL, 'live,file', 0, 0)
        , ('emailCodesEnabled',           'boolean',    NULL, NULL, NULL, 0, 1)
        , ('smsCodesEnabled',             'boolean',    NULL, NULL, NULL, 0, 0)
        , ('adultCaseOwnerTenantCode',    'tenantCode', NULL, NULL, NULL, 0, 1)
        , ('clusterWindowDays',           'integer',    1,    3650, NULL, 1, 1)
        , ('clusterMinCount',             'integer',    2,    1000, NULL, 1, 1)
        , ('passwordMinLength',           'integer',    15,   128,  NULL, 0, 1)
        , ('sessionTimeoutMinutes',       'integer',    5,    60,   NULL, 0, 1)
        , ('testingPolicyVersion',        'code',       NULL, NULL, 'none,MDH-2025-03', 0, 1)
      ) AS c (settingKey, valueKind, minValue, maxValue, allowedCodes, allowNotSet, inUse);
GO
EXEC util.uspSetObjectDescription @SchemaName = N'config', @ObjectType = N'FUNCTION', @ObjectName = N'tvfSettingCatalogue',
     @Description = N'The jurisdiction settings MERIT knows, one row per key: value kind, numeric range or allowed codes, whether NOTSET is accepted, and whether any code reads the setting (inUse). config.uspSaveSetting validates against it.';
GO
/***********************************************************************************************************************
ObjectName:   config.udfSettingProblem
Author:       Claude (Anthropic coding agent) for roscoe-sincero-sys
CreateDate:   2026-11-12
========================================================================================================================
Description:

Why a setting key and value cannot be saved, as a message for the user, or NULL when the pair is acceptable.

========================================================================================================================
Requirements and Key Dependencies:

config.tvfSettingCatalogue. auth.tenant for a tenantCode setting.

========================================================================================================================
Notes:

Comparisons are binary so that codes are case-sensitive, as the application reads them. Integers are digits only (no
sign, no spaces). The value is compared as given; the caller trims it.

========================================================================================================================
Example Usage and Performance:

SELECT config.udfSettingProblem ('sessionTimeoutMinutes', N'abc', NULL);

========================================================================================================================
Modification History:

Date:		2026-11-12
Author:		Claude (Anthropic coding agent)
Ticket:		289
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION config.udfSettingProblem (@settingKey VARCHAR (80), @settingValue NVARCHAR (2000), @rootTenantId UNIQUEIDENTIFIER)
RETURNS NVARCHAR (400)
AS
BEGIN
    DECLARE @kind VARCHAR (20), @min DECIMAL (9, 2), @max DECIMAL (9, 2), @codes VARCHAR (200), @allowNotSet BIT, @inUse BIT;
    DECLARE @v NVARCHAR (2000) = @settingValue;

    SELECT @kind = c.valueKind, @min = c.minValue, @max = c.maxValue, @codes = c.allowedCodes, @allowNotSet = c.allowNotSet, @inUse = c.inUse
      FROM config.tvfSettingCatalogue () AS c
     WHERE c.settingKey = @settingKey COLLATE Latin1_General_100_BIN2;

    IF @kind IS NULL RETURN N'This is not a MERIT setting.';
    IF @inUse = 0 RETURN N'This setting is not in use in this version of MERIT and cannot be changed.';
    IF @v IS NULL OR DATALENGTH (@v) = 0 RETURN N'Enter a value.';
    IF @allowNotSet = 1 AND @v COLLATE Latin1_General_100_BIN2 = N'NOTSET' RETURN NULL;

    IF @kind = 'integer'
       AND (LEN (@v) > 6 OR @v COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9]%' OR TRY_CONVERT (INT, @v) < @min OR TRY_CONVERT (INT, @v) > @max)
        RETURN CONCAT (N'Enter a whole number from ', CONVERT (INT, @min), N' to ', CONVERT (INT, @max), N'.');

    IF @kind = 'decimal'
       AND (LEN (@v) > 8 OR @v COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9.]%' OR @v COLLATE Latin1_General_100_BIN2 LIKE N'%.%.%' OR @v COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[0-9]%' OR @v COLLATE Latin1_General_100_BIN2 LIKE N'%.[0-9][0-9][0-9]%'
            OR TRY_CONVERT (DECIMAL (9, 2), @v) IS NULL OR TRY_CONVERT (DECIMAL (9, 2), @v) < @min OR TRY_CONVERT (DECIMAL (9, 2), @v) > @max)
        RETURN CONCAT (N'Enter a number from ', FORMAT (@min, '0.##', 'en-US'), N' to ', FORMAT (@max, '0.##', 'en-US'), N', with at most two decimal places.');

    IF @kind = 'boolean' AND @v COLLATE Latin1_General_100_BIN2 NOT IN (N'true', N'false') RETURN N'Enter true or false.';

    IF @kind = 'code' AND NOT EXISTS (SELECT 1 FROM STRING_SPLIT (@codes, ',') AS s WHERE s.value COLLATE Latin1_General_100_BIN2 = @v COLLATE Latin1_General_100_BIN2)
        RETURN CONCAT (N'Choose one of: ', REPLACE (@codes, ',', ', '), N'.');

    IF @kind = 'codeList'
       AND (EXISTS (SELECT 1 FROM STRING_SPLIT (@v, N',') AS p
                     WHERE NOT EXISTS (SELECT 1 FROM STRING_SPLIT (@codes, ',') AS s WHERE s.value COLLATE Latin1_General_100_BIN2 = p.value COLLATE Latin1_General_100_BIN2))
            OR EXISTS (SELECT 1 FROM STRING_SPLIT (@v, N',') AS p GROUP BY p.value COLLATE Latin1_General_100_BIN2 HAVING COUNT (*) > 1))
        RETURN CONCAT (N'Enter one or more of these, separated by commas and without repeats: ', REPLACE (@codes, ',', ', '), N'.');

    IF @kind = 'postalCode' AND (LEN (@v) <> 2 OR @v COLLATE Latin1_General_100_BIN2 LIKE N'%[^A-Z]%') RETURN N'Enter the two-letter postal code in capitals.';

    IF @kind = 'fips' AND (LEN (@v) <> 2 OR @v COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9]%') RETURN N'Enter the two-digit state FIPS code.';

    IF @kind = 'tenantCode'
       AND NOT EXISTS (SELECT 1 FROM auth.tenant AS t
                        WHERE t.rootTenantId = @rootTenantId AND t.tenantCode = @settingValue AND t.isActive = 1 AND t.isDeleted = 0)
        RETURN N'Enter the code of an active jurisdiction or department in this data root.';

    RETURN NULL;
END;
GO
EXEC util.uspSetObjectDescription @SchemaName = N'config', @ObjectType = N'FUNCTION', @ObjectName = N'udfSettingProblem',
     @Description = N'Why a setting key and value cannot be saved, as a message for the user, or NULL when acceptable: unknown key, setting not in use, or a value outside the definition in config.tvfSettingCatalogue.';
GO
