-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspResolveTestingPlan
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

uspResolveTestingPlan

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspResolveTestingPlan ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspResolveTestingPlan
      @personId UNIQUEIDENTIFIER
      , @workTaskId UNIQUEIDENTIFIER
      , @evidence NVARCHAR(1000)
      , @confirmed BIT
      , @completedLabResultId UNIQUEIDENTIFIER = NULL
      , @dismiss BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspResolveTestingPlan]')
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

        IF auth.udfHasPermission (N'person.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.edit).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF auth.udfHasPermission (N'person.view') = 0
        BEGIN
            ;THROW 51403, N'Patient view permission is required.', 1;
        END;
        IF NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The patient was not found, or you do not have access to it.', 1;
        END;
        IF NOT EXISTS(SELECT 1 FROM dbo.person p CROSS APPLY auth.tvfPersonPredicate(p.rootTenantId,p.owningTenantId,p.personId) scope WHERE p.personId=@personId)
        BEGIN
            ;THROW 51403,N'Clinical patient access is required; related caregiver contact access does not include clinical work.',1;
        END;

        IF auth.udfHasPermission(N'person.edit')=0 OR auth.udfHasPermission(N'task.edit')=0 OR @confirmed<>1 OR NULLIF(TRIM(@evidence),N'') IS NULL
        BEGIN
            ;THROW 51422, N'Confirm the actual test or clinical reason and provide source evidence.', 1;
        END;
        DECLARE @case UNIQUEIDENTIFIER,@json NVARCHAR(MAX),@anchor DATE,@kind VARCHAR(20),@dob DATE=(SELECT dob FROM dbo.person WHERE personId=@personId);
        SELECT @case=exposureCaseId,@json=testingPlanJson FROM dbo.workTask WITH(UPDLOCK,HOLDLOCK)
         WHERE workTaskId=@workTaskId AND personId=@personId AND queueCode='testingFollowUp' AND taskStatus='open' AND isDeleted=0;
        IF @json IS NULL OR (@case IS NOT NULL AND (auth.udfHasPermission(N'case.edit')=0 OR NOT EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=@case AND personId=@personId AND isDeleted=0)))
        BEGIN
            ;THROW 51404, N'The open testing plan is unavailable.', 1;
        END;
        SET @kind=JSON_VALUE(@json,'$.kind');
        SET @anchor=TRY_CONVERT(DATE,JSON_VALUE(@json,'$.anchorDate'));
        IF @dismiss=0 AND NOT EXISTS(SELECT 1 FROM dbo.labResult AS r JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId
         JOIN config.analyte AS a ON a.analyteId=s.analyteId WHERE r.labResultId=@completedLabResultId AND r.personId=@personId AND r.isDeleted=0
         AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected') AND a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL'
         AND r.collectedDate<=auth.udfSessionToday() AND ((@kind IN ('screen12','screen24') AND r.sampleType IN ('venous','capillary')
         AND r.collectedDate>=DATEADD(MONTH,CASE WHEN @kind='screen12' THEN 12 ELSE 24 END,@dob)
         AND r.collectedDate<DATEADD(MONTH,72,@dob))
         OR (@kind NOT IN ('screen12','screen24') AND r.sampleType='venous' AND r.collectedDate>=@anchor AND r.labResultId<>TRY_CONVERT(UNIQUEIDENTIFIER,JSON_VALUE(@json,'$.anchorResultId')))))
        BEGIN
            ;THROW 51422, N'Choose a valid subsequent venous result for this patient, or an age-appropriate screening result. A later capillary test cannot complete confirmation.', 1;
        END;
        UPDATE dbo.workTask SET taskStatus=CASE WHEN @dismiss=1 THEN 'dismissed' ELSE 'done' END,completedUtc=SYSUTCDATETIME(),completedByUserId=auth.udfSessionUserId(),resolutionNote=@evidence,
         testingPlanJson=JSON_MODIFY(testingPlanJson,'$.completedResultId',CASE WHEN @dismiss=0 THEN CONVERT(VARCHAR(36),@completedLabResultId) END)
         WHERE workTaskId=@workTaskId;

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
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspResolveTestingPlan'
    , @Description = N'uspResolveTestingPlan';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspResolveTestingPlan TO meritWebRole;
END;
GO
