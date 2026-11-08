-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReconcileWorkflowTasks
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

uspReconcileWorkflowTasks

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspReconcileWorkflowTasks ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReconcileWorkflowTasks
      @personId UNIQUEIDENTIFIER
      , @labResultId UNIQUEIDENTIFIER = NULL
      , @exposureCaseId UNIQUEIDENTIFIER = NULL
      , @workflowDefinitionId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspReconcileWorkflowTasks]')
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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF auth.udfHasPermission(N'person.edit')=0 AND auth.udfHasPermission(N'intake.upload')=0 AND auth.udfHasPermission(N'config.edit')=0
        BEGIN
            ;THROW 51403, N'Patient editing, intake or program configuration is required.', 1;
        END;
        IF NOT EXISTS(SELECT 1 FROM dbo.person WHERE personId=@personId AND isDeleted=0)
         OR (@labResultId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.labResult WHERE labResultId=@labResultId AND personId=@personId AND isDeleted=0))
         OR (@exposureCaseId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=@exposureCaseId AND personId=@personId AND caseStatus<>'closed' AND isDeleted=0))
        BEGIN
            ;THROW 51404, N'The patient, source result or active case is not accessible.', 1;
        END;
        DECLARE @lock INT,@resource NVARCHAR(255)=CONCAT(N'MERIT.workflow.',@personId);
        EXEC @lock=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction';
        IF @lock<0
        BEGIN
            ;THROW 51409, N'Workflow evaluation is busy. Try again.', 1;
        END;
        INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,labResultId,queueCode,title,detail,dueDate,workflowDefinitionId,workflowSnapshotJson,workflowOccurrenceKey)
        SELECT NEWID(),d.rootTenantId,e.targetOwner,@personId,@exposureCaseId,e.labResultId,
         CASE WHEN e.evaluationState='needsReview' THEN 'workflowReview' ELSE 'programTask' END,d.name,
         CONCAT(d.description,N' Eligibility: ',e.explanation,N' Suggested role: ',COALESCE(d.assignmentRoleCode,N'assign within the team')),
         CASE WHEN d.dueInDays IS NULL THEN NULL WHEN d.dueInDays=0 THEN e.anchorDate ELSE config.udfNotificationDueDate(e.anchorDate,d.dueInDays,d.dayBasis,d.holidayDatesJson) END,
         d.workflowDefinitionId,(SELECT d.*,e.anchorDate,e.evaluationState FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),
         CONCAT(d.workflowDefinitionId,N'/',COALESCE(@exposureCaseId,@personId),N'/',CASE WHEN d.triggerEvent='receivedResult' THEN CONVERT(VARCHAR(36),e.labResultId) ELSE 'once' END)
        FROM config.workflowDefinition AS d CROSS APPLY config.tvfWorkflowEligibility(d.workflowDefinitionId,@personId,@exposureCaseId,@labResultId) AS e
        WHERE d.definitionStatus='published' AND d.isDeleted=0 AND e.evaluationState IN ('eligible','needsReview')
         AND EXISTS(SELECT 1 FROM auth.tvfTenantWritePredicate(d.rootTenantId,e.targetOwner,0))
         AND (@workflowDefinitionId IS NULL OR d.workflowDefinitionId=@workflowDefinitionId)
         AND (@exposureCaseId IS NULL OR ((auth.udfHasPermission(N'case.view')=1 OR auth.udfHasPermission(N'config.edit')=1) AND EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=@exposureCaseId)))
         AND ((d.triggerEvent='receivedResult' AND @labResultId IS NOT NULL) OR (d.triggerEvent='patientReview' AND @exposureCaseId IS NULL AND @labResultId IS NULL) OR (d.triggerEvent='caseOpened' AND @exposureCaseId IS NOT NULL))
         AND NOT EXISTS(SELECT 1 FROM dbo.workTask AS w WHERE w.isDeleted=0 AND w.workflowOccurrenceKey=
         CONCAT(d.workflowDefinitionId,N'/',COALESCE(@exposureCaseId,@personId),N'/',CASE WHEN d.triggerEvent='receivedResult' THEN CONVERT(VARCHAR(36),e.labResultId) ELSE 'once' END));
        SELECT @@ROWCOUNT AS createdCount;

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
    , @ObjectName  = N'uspReconcileWorkflowTasks'
    , @Description = N'uspReconcileWorkflowTasks';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReconcileWorkflowTasks TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReconcileWorkflowTasks TO meritWorkerRole;
END;
GO
