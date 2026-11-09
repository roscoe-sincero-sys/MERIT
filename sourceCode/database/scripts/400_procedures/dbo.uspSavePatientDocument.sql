-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSavePatientDocument
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Idempotent private original-file upload or new revision; retained originals are never overwritten.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission document.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspSavePatientDocument ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSavePatientDocument
      @personDocumentId UNIQUEIDENTIFIER
      , @personId UNIQUEIDENTIFIER
      , @title NVARCHAR (200)
      , @fileName NVARCHAR (200)
      , @contentType VARCHAR (100)
      , @category VARCHAR (30)
      , @documentDate DATE
      , @source NVARCHAR (200)
      , @contentHash BINARY (32)
      , @content VARBINARY (MAX)
      , @description NVARCHAR (1000) = NULL
      , @exposureCaseId UNIQUEIDENTIFIER = NULL
      , @supersedesDocumentId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSavePatientDocument]')
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

        IF auth.udfHasPermission (N'document.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (document.edit).', 1;
        END;

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
        IF @exposureCaseId IS NOT NULL AND (auth.udfHasPermission (N'case.view') = 0 OR NOT EXISTS
            (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId AND personId = @personId AND isDeleted = 0))
        BEGIN
            ;THROW 51404, N'The case was not found for this patient, or is outside your access.', 1;
        END;
        IF @exposureCaseId IS NOT NULL AND auth.udfHasPermission (N'case.edit') = 0
        BEGIN
            ;THROW 51403, N'Case edit permission is required for case-associated records.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER;
        SELECT @root = rootTenantId, @owner = owningTenantId FROM dbo.person WHERE personId = @personId;
        IF @exposureCaseId IS NOT NULL SELECT @owner = owningTenantId FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId;

        IF DATALENGTH (@content) NOT BETWEEN 1 AND 10485760 OR @contentHash <> HASHBYTES ('SHA2_256', @content)
           OR @contentType NOT IN ('application/pdf','application/vnd.openxmlformats-officedocument.spreadsheetml.sheet','image/png','image/jpeg','image/webp')
        BEGIN
            ;THROW 51422, N'Choose a supported original file up to 10 MB.', 1;
        END;
        DECLARE @lock INT, @resource NVARCHAR (100) = CONCAT (N'MERIT.document.', @personDocumentId);
        EXEC @lock = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 1000;
        IF @lock < 0
        BEGIN
            ;THROW 51409, N'This upload is busy. Check the document list before retrying.', 1;
        END;
        IF EXISTS (SELECT 1 FROM dbo.personDocument WHERE personDocumentId = @personDocumentId)
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.personDocument WHERE personDocumentId = @personDocumentId AND personId = @personId
                            AND contentHash = @contentHash AND isDeleted = 0
                            AND title = @title AND fileName = @fileName AND contentType = @contentType AND category = @category
                            AND documentDate = @documentDate AND source = @source
                            AND description IS NOT DISTINCT FROM @description AND supersedesDocumentId IS NOT DISTINCT FROM @supersedesDocumentId
                            AND ((exposureCaseId IS NULL AND @exposureCaseId IS NULL) OR exposureCaseId = @exposureCaseId))
            BEGIN
                ;THROW 51409, N'This upload was already saved with different content or details. Refresh to check the original before starting another upload.', 1;
            END;
        END
        ELSE
        BEGIN
            IF @supersedesDocumentId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.personDocument
                WHERE personDocumentId = @supersedesDocumentId AND personId = @personId AND isDeleted = 0
                  AND ((exposureCaseId IS NULL AND @exposureCaseId IS NULL) OR exposureCaseId = @exposureCaseId))
            BEGIN
                ;THROW 51422, N'Choose an earlier revision of this patient and case document.', 1;
            END;
            INSERT dbo.personDocument (personDocumentId, rootTenantId, owningTenantId, personId, exposureCaseId, supersedesDocumentId,
                title, fileName, contentType, category, documentDate, source, description, uploadedByUserId, contentHash, content)
            VALUES (@personDocumentId, @root, @owner, @personId, @exposureCaseId, @supersedesDocumentId, @title, @fileName,
                @contentType, @category, @documentDate, @source, @description, auth.udfSessionUserId (), @contentHash, @content);
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
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspSavePatientDocument'
    , @Description = N'Idempotent private original-file upload or new revision; retained originals are never overwritten.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSavePatientDocument TO meritWebRole;
END;
GO
