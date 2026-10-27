-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSetCaseClusterMember
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

A case joins a cluster (by case number) or leaves it. Leaving keeps the row with its left date, so the cluster's history shows who was in it and when.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission case.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspSetCaseClusterMember ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T109
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSetCaseClusterMember
      @clusterId  UNIQUEIDENTIFIER
      , @caseNumber VARCHAR (20)
      , @isMember   BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSetCaseClusterMember]')
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
    SET @KeyParameters = CONCAT (N'clusterId=', @clusterId, N', caseNumber=', @caseNumber, N', isMember=', @isMember);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'case.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.edit).', 1;
        END;

        IF NOT EXISTS (SELECT 1 FROM dbo.cluster WHERE clusterId = @clusterId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The cluster was not found, or you do not have access to it.', 1;
        END;
        IF NOT EXISTS (SELECT 1 FROM dbo.exposureCase WHERE caseNumber = @caseNumber AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'No case with that number was found, or you do not have access to it.', 1;
        END;
        IF EXISTS (SELECT 1 FROM dbo.cluster WHERE clusterId = @clusterId AND clusterStatus = 'closed')
        BEGIN
            ;THROW 51409, N'The cluster is closed. Reopen it before changing its cases.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @caseId UNIQUEIDENTIFIER = (SELECT exposureCaseId FROM dbo.exposureCase WHERE caseNumber = @caseNumber
                                                                                          AND isDeleted = 0);
        DECLARE @today DATE = auth.udfSessionToday ();

        IF @isMember = 1
        BEGIN
            UPDATE dbo.caseCluster SET leftDate = NULL
             WHERE clusterId = @clusterId AND exposureCaseId = @caseId AND isDeleted = 0 AND leftDate IS NOT NULL;

            INSERT dbo.caseCluster (caseClusterId, rootTenantId, owningTenantId, clusterId, exposureCaseId, joinedDate)
            SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), cl.rootTenantId, cl.owningTenantId, cl.clusterId, @caseId, @today
              FROM dbo.cluster AS cl
             WHERE cl.clusterId = @clusterId
               AND NOT EXISTS (SELECT 1 FROM dbo.caseCluster AS cc WHERE cc.clusterId = @clusterId
                                                                     AND cc.exposureCaseId = @caseId AND cc.isDeleted = 0);
        END
        ELSE
        BEGIN
            UPDATE dbo.caseCluster SET leftDate = @today
             WHERE clusterId = @clusterId AND exposureCaseId = @caseId AND isDeleted = 0 AND leftDate IS NULL;
        END;

        SET @Comments = CONCAT (N'Case ', @caseNumber, CASE @isMember WHEN 1 THEN N' joined.' ELSE N' left.' END);

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
    , @ObjectName  = N'uspSetCaseClusterMember'
    , @Description = N'A case joins a cluster (by case number) or leaves it. Leaving keeps the row with its left date, so the cluster''s history shows who was in it and when.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSetCaseClusterMember TO meritWebRole;
END;
GO
