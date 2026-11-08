-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspPublishWorkflowDefinition
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

config.uspPublishWorkflowDefinition

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

exec config.uspPublishWorkflowDefinition ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspPublishWorkflowDefinition
      @workflowDefinitionId UNIQUEIDENTIFIER
      , @versionStamp VARCHAR(16)
      , @approvalReference NVARCHAR(300)
      , @approved BIT
      , @samplePersonId UNIQUEIDENTIFIER
      , @secondSamplePersonId UNIQUEIDENTIFIER
      , @sampleCaseId UNIQUEIDENTIFIER = NULL
      , @secondSampleCaseId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspPublishWorkflowDefinition]')
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

        IF @approved<>1 OR NULLIF(TRIM(@approvalReference),N'') IS NULL OR @samplePersonId=@secondSamplePersonId
         OR NOT EXISTS(SELECT 1 FROM config.tvfWorkflowEligibility(@workflowDefinitionId,@samplePersonId,@sampleCaseId,NULL))
         OR NOT EXISTS(SELECT 1 FROM config.tvfWorkflowEligibility(@workflowDefinitionId,@secondSamplePersonId,@secondSampleCaseId,NULL))
        BEGIN
            ;THROW 51422, N'Preview two accessible sample patients and explicitly acknowledge program approval before publication.', 1;
        END;
        IF EXISTS(SELECT 1 FROM config.workflowDefinition AS d WITH(UPDLOCK,HOLDLOCK) JOIN config.workflowDefinition AS n ON n.workflowDefinitionId=@workflowDefinitionId
         WHERE d.workflowDefinitionId<>n.workflowDefinitionId AND d.owningTenantId=n.owningTenantId AND d.name=n.name AND d.definitionStatus='published' AND d.isDeleted=0
         AND d.effectiveFrom<=COALESCE(n.effectiveTo,'99991231') AND COALESCE(d.effectiveTo,'99991231')>=n.effectiveFrom)
        BEGIN
            ;THROW 51422, N'Retire the overlapping published version or use nonoverlapping effective dates.', 1;
        END;
        UPDATE config.workflowDefinition SET definitionStatus='published',approvalReference=@approvalReference,publishedByUserId=auth.udfSessionUserId(),publishedUtc=SYSUTCDATETIME()
         WHERE workflowDefinitionId=@workflowDefinitionId AND definitionStatus='draft' AND owningTenantId=auth.udfSessionActingTenantId() AND isDeleted=0
         AND CONVERT(VARCHAR(16),CAST(rowVersion AS BINARY(8)),2)=@versionStamp;
        IF @@ROWCOUNT<>1
        BEGIN
            ;THROW 51409, N'The draft changed or is not owned by the working agency. Reload and preview it again.', 1;
        END;

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
    , @ObjectName  = N'uspPublishWorkflowDefinition'
    , @Description = N'config.uspPublishWorkflowDefinition';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspPublishWorkflowDefinition TO meritWebRole;
END;
GO
