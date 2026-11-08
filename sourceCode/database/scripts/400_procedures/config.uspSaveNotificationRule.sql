-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspSaveNotificationRule
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Creates an approved receipt-based policy version; existing snapshots cannot be edited or silently recalculated.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission config.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec config.uspSaveNotificationRule ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspSaveNotificationRule
      @notificationRuleId UNIQUEIDENTIFIER
      , @ruleName NVARCHAR (120)
      , @analyteSpecimenId UNIQUEIDENTIFIER
      , @population VARCHAR (10)
      , @minimumValue DECIMAL (18,6)
      , @recipientKind VARCHAR (12)
      , @dueInDays INT
      , @dayBasis VARCHAR (12)
      , @timeZoneId VARCHAR (100)
      , @calendarName NVARCHAR (120)
      , @holidayDatesJson NVARCHAR (MAX)
      , @effectiveFrom DATE
      , @approvalReference NVARCHAR (300)
      , @minimumAgeMonths INT = NULL
      , @maximumAgeMonths INT = NULL
      , @maximumValue DECIMAL (18,6) = NULL
      , @effectiveTo DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspSaveNotificationRule]')
          , @StartTimeUtc   DATETIME2 (3)  = SYSUTCDATETIME ()
          , @EndTimeUtc     DATETIME2 (3)  = NULL
          , @ExecutionId    BIGINT         = NULL
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @Comments       NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never a credential, a payload, or patient data.
    SET @KeyParameters = N'patient care metadata; no text or file content';

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'config.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (config.edit).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF @population NOT IN ('all','child','adult','pregnant','unknown') OR @recipientKind NOT IN ('patient','caregiver')
           OR @dueInDays NOT BETWEEN 1 AND 365 OR @dayBasis NOT IN ('calendar','business')
           OR @minimumValue < 0 OR @maximumValue <= @minimumValue OR @minimumAgeMonths < 0 OR @maximumAgeMonths < @minimumAgeMonths
           OR @effectiveTo < @effectiveFrom OR NULLIF (LTRIM (RTRIM (@ruleName)), N'') IS NULL
           OR NULLIF (LTRIM (RTRIM (@approvalReference)), N'') IS NULL OR NULLIF (LTRIM (RTRIM (@calendarName)), N'') IS NULL
           OR ISJSON (@holidayDatesJson) <> 1
        BEGIN
            ;THROW 51422, N'Provide an approved rule, valid thresholds and dates, and an explicit calendar policy.', 1;
        END;
        IF EXISTS (SELECT 1 FROM OPENJSON (@holidayDatesJson) WHERE [type] <> 1 OR TRY_CONVERT (DATE, [value]) IS NULL)
           OR (SELECT COUNT (*) FROM OPENJSON (@holidayDatesJson)) > 2000
           OR NOT EXISTS (SELECT 1 FROM sys.time_zone_info WHERE name = @timeZoneId)
        BEGIN
            ;THROW 51422, N'Use valid explicit holiday dates and a supported policy time zone.', 1;
        END;
        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId (), @owner UNIQUEIDENTIFIER = auth.udfSessionActingTenantId ();
        IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @owner AND rootTenantId = @root AND isActive = 1 AND isDeleted = 0)
           OR NOT EXISTS (SELECT 1 FROM config.analyteSpecimen WHERE analyteSpecimenId = @analyteSpecimenId AND isDeleted = 0)
        BEGIN
            ;THROW 51422, N'Choose a working agency and an available analyte/specimen.', 1;
        END;
        IF EXISTS (SELECT 1 FROM config.notificationRule WITH (UPDLOCK, HOLDLOCK) WHERE owningTenantId = @owner AND ruleName = @ruleName
                   AND isActive = 1 AND isDeleted = 0 AND effectiveFrom <= COALESCE (@effectiveTo, '9999-12-31')
                   AND COALESCE (effectiveTo, '9999-12-31') >= @effectiveFrom)
        BEGIN
            ;THROW 51422, N'An active version with this name overlaps the proposed period. Retire it or use a non-overlapping period.', 1;
        END;
        INSERT config.notificationRule (notificationRuleId, rootTenantId, owningTenantId, ruleName, analyteSpecimenId,
            population, minimumAgeMonths, maximumAgeMonths, minimumValue, maximumValue, recipientKind, triggerEvent, dueInDays,
            dayBasis, timeZoneId, calendarName, holidayDatesJson, effectiveFrom, effectiveTo, approvalReference, approvedByUserId)
        VALUES (@notificationRuleId, @root, @owner, @ruleName, @analyteSpecimenId, @population, @minimumAgeMonths, @maximumAgeMonths,
            @minimumValue, @maximumValue, @recipientKind, 'receipt', @dueInDays, @dayBasis, @timeZoneId, @calendarName,
            @holidayDatesJson, @effectiveFrom, @effectiveTo, @approvalReference, auth.udfSessionUserId ());

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc          = @EndTimeUtc
                 , ElapsedMilliseconds = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                    , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful          = 1
                 , Comments            = @Comments
                 , modifiedBy          = ORIGINAL_LOGIN ()
                 , modifiedDateUtc     = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        BEGIN TRY
            IF @ExecutionId IS NULL
               OR NOT EXISTS (SELECT 1
                                FROM logs.ExecutionLog
                               WHERE ExecutionLogId = @ExecutionId)
            BEGIN
                EXEC logs.uspStartExecutionLogging
                      @ProcedureName          = @ProcName
                    , @KeyParameters          = @KeyParameters
                    , @StartDateUtc           = @StartTimeUtc
                    , @ReCreatedAfterRollback = 1
                    , @ExecutionLogId         = @ExecutionId OUTPUT;
            END;
        END TRY
        BEGIN CATCH
            SET @ExecutionId = NULL;
        END CATCH;

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionId
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspSaveNotificationRule'
    , @Description = N'Creates an approved receipt-based policy version; existing snapshots cannot be edited or silently recalculated.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspSaveNotificationRule TO meritWebRole;
END;
GO
