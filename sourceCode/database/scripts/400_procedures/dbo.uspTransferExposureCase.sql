-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspTransferExposureCase
Author:       MERIT build
CreateDate:   2026-11-11
========================================================================================================================
Description:

Transfers an open or monitoring case to another jurisdiction (or program, or the data root) of the same data root,
with everything that belongs to it: its history, result links, tasks, notification obligations, exposure sources,
patient activities and documents recorded on the case, cluster links, investigation links, and each investigation
that serves only this case. Records the transfer in the case history and completes any "Case opened outside the
residence jurisdiction" task.

========================================================================================================================
Requirements and Key Dependencies:

auth.tvfSessionWritePermission, auth.tvfAssignableUserTenants, dbo.uspRefreshPersonAccess.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission case.edit (auth.udfHasPermission); refuses with 51403 otherwise. The caller must hold case.edit
with write access at BOTH the current owner and the receiving tenant (a state user, or someone working in both
jurisdictions); every table's row-level-security block predicate would refuse the move otherwise.

Rows the caller cannot see (row-level security) are not moved; the count moved is in the history note. Assignees
who cannot work the case, task or investigation in the receiving tenant are cleared, so nothing stays assigned to
someone who can no longer open it. An investigation linked to other cases stays where it is; only this case's link
moves. Lab results stay owned by the data root, as every result is.

Rule 8 full instrumentation. Row-level security applies to every table read or written here.

========================================================================================================================
Example Usage and Performance:

EXEC dbo.uspTransferExposureCase @exposureCaseId = @id, @toTenantId = @tenant, @reason = N'Family lives in Harford County', @rowVersion = @rv;

========================================================================================================================
Modification History:

Date:		2026-11-11
Author:		MERIT build
Ticket:		#274
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspTransferExposureCase
      @exposureCaseId UNIQUEIDENTIFIER
      , @toTenantId     UNIQUEIDENTIFIER
      , @reason         NVARCHAR (500)
      , @rowVersion     BINARY (8)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspTransferExposureCase]')
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
    SET @KeyParameters = CONCAT (N'exposureCaseId=', @exposureCaseId, N', toTenantId=', @toTenantId);

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

        IF NOT EXISTS (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The case was not found, or you do not have access to it.', 1;
        END;

        IF NULLIF (TRIM (@reason), N'') IS NULL
        BEGIN
            ;THROW 51422, N'Give the reason for the transfer.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER, @from UNIQUEIDENTIFIER, @person UNIQUEIDENTIFIER, @status VARCHAR (12);
        SELECT @root = rootTenantId, @from = owningTenantId, @person = personId, @status = caseStatus
          FROM dbo.exposureCase WITH (UPDLOCK, HOLDLOCK)
         WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0;

        IF @status = 'closed'
        BEGIN
            ;THROW 51422, N'Reopen the case before transferring it; a closed case keeps the jurisdiction that closed it.', 1;
        END;
        IF @toTenantId = @from
        BEGIN
            ;THROW 51422, N'The case is already owned there. Choose another jurisdiction.', 1;
        END;
        IF NOT EXISTS (SELECT 1 FROM auth.tenant WHERE tenantId = @toTenantId AND rootTenantId = @root AND isActive = 1 AND isDeleted = 0
                          AND (tenantType IN ('jurisdiction', 'program') OR tenantId = rootTenantId))
        BEGIN
            ;THROW 51422, N'Choose an active jurisdiction or program of this case''s state or tribe.', 1;
        END;
        IF NOT EXISTS (SELECT 1 FROM auth.tvfSessionWritePermission ('case.edit') WHERE tenantId = @from)
           OR NOT EXISTS (SELECT 1 FROM auth.tvfSessionWritePermission ('case.edit') WHERE tenantId = @toTenantId)
        BEGIN
            ;THROW 51403, N'Transferring a case needs case editing with write access in both the current and the receiving jurisdiction. Ask a state user or someone who works in both.', 1;
        END;

        UPDATE dbo.exposureCase
           SET owningTenantId   = @toTenantId
             , assignedToUserId = CASE WHEN EXISTS (SELECT 1 FROM auth.tvfAssignableUserTenants ('case.edit') AS e
                                                     WHERE e.appUserId = assignedToUserId AND e.tenantId = @toTenantId)
                                       THEN assignedToUserId END
         WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0 AND caseStatus <> 'closed' AND rowVersion = @rowVersion;
        IF @@ROWCOUNT = 0
        BEGIN
            ;THROW 51409, N'This record was changed by someone else, or is no longer available. Reload it and try again.', 1;
        END;

        DECLARE @moved INT = 0;
        UPDATE dbo.caseActivity SET owningTenantId = @toTenantId WHERE exposureCaseId = @exposureCaseId AND owningTenantId <> @toTenantId;
        SET @moved += @@ROWCOUNT;
        UPDATE dbo.caseResultLink SET owningTenantId = @toTenantId WHERE exposureCaseId = @exposureCaseId AND owningTenantId <> @toTenantId;
        SET @moved += @@ROWCOUNT;
        UPDATE dbo.workTask
           SET owningTenantId   = @toTenantId
             , assignedToUserId = CASE WHEN EXISTS (SELECT 1 FROM auth.tvfAssignableUserTenants ('task.edit') AS e
                                                     WHERE e.appUserId = assignedToUserId AND e.tenantId = @toTenantId)
                                       THEN assignedToUserId END
         WHERE exposureCaseId = @exposureCaseId AND owningTenantId <> @toTenantId;
        SET @moved += @@ROWCOUNT;
        UPDATE dbo.notificationObligation SET owningTenantId = @toTenantId WHERE exposureCaseId = @exposureCaseId AND owningTenantId <> @toTenantId;
        SET @moved += @@ROWCOUNT;
        UPDATE dbo.exposureSource SET owningTenantId = @toTenantId WHERE exposureCaseId = @exposureCaseId AND owningTenantId <> @toTenantId;
        SET @moved += @@ROWCOUNT;
        UPDATE dbo.personActivity SET owningTenantId = @toTenantId WHERE exposureCaseId = @exposureCaseId AND owningTenantId <> @toTenantId;
        SET @moved += @@ROWCOUNT;
        UPDATE dbo.personDocument SET owningTenantId = @toTenantId WHERE exposureCaseId = @exposureCaseId AND owningTenantId <> @toTenantId;
        SET @moved += @@ROWCOUNT;
        UPDATE dbo.caseCluster SET owningTenantId = @toTenantId WHERE exposureCaseId = @exposureCaseId AND owningTenantId <> @toTenantId;
        SET @moved += @@ROWCOUNT;

        -- Investigations: the link always moves; the investigation itself only when no other case uses it.
        DECLARE @own TABLE (investigationId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @own SELECT DISTINCT ic.investigationId FROM dbo.investigationCase AS ic
         WHERE ic.exposureCaseId = @exposureCaseId AND ic.isDeleted = 0
           AND NOT EXISTS (SELECT 1 FROM dbo.investigationCase AS o
                            WHERE o.investigationId = ic.investigationId AND o.isDeleted = 0 AND o.exposureCaseId <> @exposureCaseId);
        UPDATE dbo.investigationCase SET owningTenantId = @toTenantId WHERE exposureCaseId = @exposureCaseId AND owningTenantId <> @toTenantId;
        SET @moved += @@ROWCOUNT;
        UPDATE i
           SET owningTenantId   = @toTenantId
             , assignedToUserId = CASE WHEN EXISTS (SELECT 1 FROM auth.tvfAssignableUserTenants ('investigation.edit') AS e
                                                     WHERE e.appUserId = i.assignedToUserId AND e.tenantId = @toTenantId)
                                       THEN i.assignedToUserId END
          FROM dbo.investigation AS i JOIN @own AS o ON o.investigationId = i.investigationId
         WHERE i.owningTenantId <> @toTenantId;
        DECLARE @investigations INT = @@ROWCOUNT;
        DECLARE @shared INT = (SELECT COUNT (DISTINCT ic.investigationId) FROM dbo.investigationCase AS ic
                                WHERE ic.exposureCaseId = @exposureCaseId AND ic.isDeleted = 0
                                  AND NOT EXISTS (SELECT 1 FROM @own AS o WHERE o.investigationId = ic.investigationId));

        DECLARE @fromName NVARCHAR (120) = (SELECT tenantName FROM auth.tenant WHERE tenantId = @from),
                @toName   NVARCHAR (120) = (SELECT tenantName FROM auth.tenant WHERE tenantId = @toTenantId);

        -- The routing task from issue 268 is answered by this transfer.
        UPDATE dbo.workTask
           SET taskStatus = 'done', completedUtc = SYSUTCDATETIME (), completedByUserId = auth.udfSessionUserId (),
               resolutionKind = 'completed', resolutionNote = LEFT (CONCAT (N'Case transferred from ', @fromName, N' to ', @toName, N': ', TRIM (@reason)), 1000)
         WHERE exposureCaseId = @exposureCaseId AND taskStatus = 'open' AND isDeleted = 0
           AND title = N'Case opened outside the residence jurisdiction';

        INSERT dbo.caseActivity (caseActivityId, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate, workerUserId, note)
        VALUES (CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER),
                @root, @toTenantId, @exposureCaseId, 'statusChange', auth.udfSessionToday (), auth.udfSessionUserId (),
                LEFT (CONCAT (N'Case transferred from ', @fromName, N' to ', @toName, N'. Reason: ', TRIM (@reason),
                              N' Moved ', @moved, N' related records and ', @investigations, N' investigations',
                              CASE WHEN @shared > 0 THEN CONCAT (N'; ', @shared, N' investigations shared with other cases stay with their owner') ELSE N'' END, N'.'), 2000));

        EXEC dbo.uspRefreshPersonAccess @personId = @person;

        SET @Comments = CONCAT (N'Case transferred; related rows moved=', @moved, N', investigations moved=', @investigations, N', shared kept=', @shared, N'.');

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
    , @ObjectName  = N'uspTransferExposureCase'
    , @Description = N'Transfers an open case, with its history, tasks, obligations, sources, documents and its own investigations, to another jurisdiction of the same data root. Needs case.edit with write access at both ends (#274).';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspTransferExposureCase TO meritWebRole;
END;
GO
