SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName:   auth.udfSessionIdleMinutes
Author:       Claude (Anthropic coding agent) for roscoe-sincero-sys
CreateDate:   2026-11-13
========================================================================================================================
Description:

The idle timeout, in minutes, that a data root's sessionTimeoutMinutes setting gives a session started now. NULL when
the root has no value in force or the stored value is not a whole number from 5 to 60; the application then uses the
deployment default.

========================================================================================================================
Requirements and Key Dependencies:

config.jurisdictionSetting. The range matches config.tvfSettingCatalogue ('sessionTimeoutMinutes').

========================================================================================================================
Notes:

EXECUTE AS OWNER: auth.uspCreateSessionScope calls this while the session scope is being created, before the
connection carries a scope key, so row-level security would otherwise hide the root's settings. It returns one number
for the root passed in and nothing else. The date is the UTC date, as in auth.uspGetPasswordPolicy.

========================================================================================================================
Example Usage and Performance:

SELECT auth.udfSessionIdleMinutes (@rootTenantId);

========================================================================================================================
Modification History:

Date:		2026-11-13
Author:		Claude (Anthropic coding agent)
Ticket:		289
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER FUNCTION auth.udfSessionIdleMinutes (@rootTenantId UNIQUEIDENTIFIER)
RETURNS INT
WITH EXECUTE AS OWNER
AS
BEGIN
    DECLARE @today DATE = CAST (SYSUTCDATETIME () AS DATE), @value NVARCHAR (2000);

    SELECT TOP (1) @value = s.settingValue
      FROM config.jurisdictionSetting AS s
     WHERE s.rootTenantId = @rootTenantId AND s.settingKey = 'sessionTimeoutMinutes' AND s.isDeleted = 0
       AND s.effectiveFrom <= @today AND (s.effectiveTo IS NULL OR s.effectiveTo >= @today)
     ORDER BY s.effectiveFrom DESC;

    RETURN CASE WHEN LEN (@value) <= 2 AND @value COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^0-9]%'
                     AND TRY_CONVERT (INT, @value) BETWEEN 5 AND 60
                THEN TRY_CONVERT (INT, @value) END;
END;
GO
EXEC util.uspSetObjectDescription @SchemaName = N'auth', @ObjectType = N'FUNCTION', @ObjectName = N'udfSessionIdleMinutes',
     @Description = N'The idle timeout in minutes that a data root''s sessionTimeoutMinutes setting gives a session started now; NULL (deployment default) when unset or not a whole number from 5 to 60. Runs as owner because the caller has no session scope yet.';
GO
