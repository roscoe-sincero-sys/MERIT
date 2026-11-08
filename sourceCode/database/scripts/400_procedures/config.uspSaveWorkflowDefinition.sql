-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspSaveWorkflowDefinition
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

config.uspSaveWorkflowDefinition

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

exec config.uspSaveWorkflowDefinition ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspSaveWorkflowDefinition
      @workflowDefinitionId UNIQUEIDENTIFIER
      , @name NVARCHAR(120)
      , @version INT
      , @description NVARCHAR(1000)
      , @triggerEvent VARCHAR(20)
      , @population VARCHAR(10)
      , @completionKind VARCHAR(12)
      , @effectiveFrom DATE
      , @timeZoneId VARCHAR(100)
      , @evidenceFieldsJson NVARCHAR(MAX) = N'[]'
      , @analyteSpecimenId UNIQUEIDENTIFIER = NULL
      , @minimumAgeMonths INT = NULL
      , @maximumAgeMonths INT = NULL
      , @minimumValue DECIMAL(18,6) = NULL
      , @maximumValue DECIMAL(18,6) = NULL
      , @requiresEligibilityReview BIT = 0
      , @assignmentRoleCode VARCHAR(40) = NULL
      , @dueInDays INT = NULL
      , @recurrenceDays INT = NULL
      , @dayBasis VARCHAR(12) = 'calendar'
      , @holidayDatesJson NVARCHAR(MAX) = N'[]'
      , @blocksClosure BIT = 0
      , @closureReason VARCHAR(30) = 'all'
      , @minimumContactAttempts INT = 0
      , @allowWaiver BIT = 0
      , @effectiveTo DATE = NULL
      , @versionStamp VARCHAR(16) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspSaveWorkflowDefinition]')
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

        IF NULLIF(TRIM(@name),N'') IS NULL OR NULLIF(TRIM(@description),N'') IS NULL OR @version<1
         OR @triggerEvent NOT IN ('patientReview','receivedResult','caseOpened') OR @population NOT IN ('all','child','adult','pregnant')
         OR @completionKind NOT IN ('internal','contact','delivery') OR @dayBasis NOT IN ('calendar','business')
         OR @minimumContactAttempts NOT BETWEEN 0 AND 20 OR @closureReason NOT IN ('all','belowLevelOnRetest','lostToFollowUp','movedOutOfState','declined','deceased','duplicate','other')
         OR (@dueInDays IS NOT NULL AND @dueInDays NOT BETWEEN 0 AND 365) OR (@recurrenceDays IS NOT NULL AND @recurrenceDays NOT BETWEEN 1 AND 365)
         OR @minimumAgeMonths<0 OR @maximumAgeMonths<0 OR @maximumAgeMonths<@minimumAgeMonths OR @minimumValue<0 OR @maximumValue<=@minimumValue
         OR @effectiveTo<@effectiveFrom OR NOT EXISTS(SELECT 1 FROM sys.time_zone_info WHERE name=@timeZoneId)
         OR ((@minimumValue IS NOT NULL OR @maximumValue IS NOT NULL) AND @analyteSpecimenId IS NULL)
         OR (@analyteSpecimenId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM config.analyteSpecimen WHERE analyteSpecimenId=@analyteSpecimenId AND isDeleted=0))
         OR (@assignmentRoleCode IS NOT NULL AND NOT EXISTS(SELECT 1 FROM auth.role WHERE roleCode=@assignmentRoleCode AND isDeleted=0))
        BEGIN
            ;THROW 51422, N'Check the task name, version, conditions, role, dates, timing and completion rules.', 1;
        END;
        IF ISJSON(@evidenceFieldsJson)<>1 OR LEFT(TRIM(@evidenceFieldsJson),1)<>'[' OR LEN(@evidenceFieldsJson)>4000
         OR ISJSON(@holidayDatesJson)<>1 OR LEFT(TRIM(@holidayDatesJson),1)<>'['
        BEGIN
            ;THROW 51422, N'Evidence fields and holidays must be bounded JSON arrays.', 1;
        END;
        IF (SELECT COUNT(*) FROM OPENJSON(@evidenceFieldsJson))>10 OR EXISTS
         (SELECT 1 FROM OPENJSON(@evidenceFieldsJson) WITH([key] NVARCHAR(100),label NVARCHAR(200),type VARCHAR(20),required BIT)
         WHERE NULLIF(TRIM([key]),N'') IS NULL OR LEN([key])>30 OR [key] COLLATE Latin1_General_100_BIN2 LIKE N'%[^a-zA-Z0-9_]%'
         OR NULLIF(TRIM(label),N'') IS NULL OR LEN(label)>80 OR type IS NULL OR type NOT IN ('text','date','number','yesNo') OR required IS NULL)
         OR EXISTS(SELECT 1 FROM OPENJSON(@evidenceFieldsJson) WITH([key] NVARCHAR(100)) GROUP BY [key] HAVING COUNT(*)>1)
         OR (SELECT COUNT(*) FROM OPENJSON(@holidayDatesJson))>2000 OR EXISTS(SELECT 1 FROM OPENJSON(@holidayDatesJson) WHERE type<>1 OR TRY_CONVERT(DATE,[value],23) IS NULL)
        BEGIN
            ;THROW 51422, N'Use up to ten unique evidence fields (text/date/number/yesNo) and valid explicit holiday dates.', 1;
        END;
        IF EXISTS(SELECT 1 FROM config.workflowDefinition WHERE workflowDefinitionId=@workflowDefinitionId AND isDeleted=0)
        BEGIN
            UPDATE config.workflowDefinition SET name=@name,version=@version,description=@description,triggerEvent=@triggerEvent,population=@population,
             completionKind=@completionKind,effectiveFrom=@effectiveFrom,effectiveTo=@effectiveTo,timeZoneId=@timeZoneId,evidenceFieldsJson=@evidenceFieldsJson,
             analyteSpecimenId=@analyteSpecimenId,minimumAgeMonths=@minimumAgeMonths,maximumAgeMonths=@maximumAgeMonths,minimumValue=@minimumValue,maximumValue=@maximumValue,
             requiresEligibilityReview=@requiresEligibilityReview,assignmentRoleCode=@assignmentRoleCode,dueInDays=@dueInDays,recurrenceDays=@recurrenceDays,dayBasis=@dayBasis,
             holidayDatesJson=@holidayDatesJson,blocksClosure=@blocksClosure,closureReason=@closureReason,minimumContactAttempts=@minimumContactAttempts,allowWaiver=@allowWaiver
            WHERE workflowDefinitionId=@workflowDefinitionId AND definitionStatus='draft' AND owningTenantId=auth.udfSessionActingTenantId()
             AND CONVERT(VARCHAR(16),CAST(rowVersion AS BINARY(8)),2)=@versionStamp;
            IF @@ROWCOUNT<>1
            BEGIN
                ;THROW 51409, N'Only an unchanged draft owned by the working agency can be edited. Published versions are immutable.', 1;
            END;
        END
        ELSE
            INSERT config.workflowDefinition(workflowDefinitionId,rootTenantId,owningTenantId,name,version,description,triggerEvent,population,completionKind,effectiveFrom,effectiveTo,
             timeZoneId,evidenceFieldsJson,analyteSpecimenId,minimumAgeMonths,maximumAgeMonths,minimumValue,maximumValue,requiresEligibilityReview,assignmentRoleCode,
             dueInDays,recurrenceDays,dayBasis,holidayDatesJson,blocksClosure,closureReason,minimumContactAttempts,allowWaiver)
            VALUES(@workflowDefinitionId,auth.udfSessionRootTenantId(),auth.udfSessionActingTenantId(),@name,@version,@description,@triggerEvent,@population,@completionKind,@effectiveFrom,@effectiveTo,
             @timeZoneId,@evidenceFieldsJson,@analyteSpecimenId,@minimumAgeMonths,@maximumAgeMonths,@minimumValue,@maximumValue,@requiresEligibilityReview,@assignmentRoleCode,
             @dueInDays,@recurrenceDays,@dayBasis,@holidayDatesJson,@blocksClosure,@closureReason,@minimumContactAttempts,@allowWaiver);

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
    , @ObjectName  = N'uspSaveWorkflowDefinition'
    , @Description = N'config.uspSaveWorkflowDefinition';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspSaveWorkflowDefinition TO meritWebRole;
END;
GO
