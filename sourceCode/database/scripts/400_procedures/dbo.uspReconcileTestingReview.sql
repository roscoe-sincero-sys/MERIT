-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReconcileTestingReview
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

uspReconcileTestingReview

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

exec dbo.uspReconcileTestingReview ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReconcileTestingReview
      @labResultId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspReconcileTestingReview]')
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

        IF auth.udfHasPermission(N'result.edit')=0 AND auth.udfHasPermission(N'intake.upload')=0 AND auth.udfHasPermission(N'config.edit')=0
        BEGIN
            ;THROW 51403, N'Result editing, intake or configuration permission is required.', 1;
        END;
        DECLARE @person UNIQUEIDENTIFIER,@root UNIQUEIDENTIFIER,@owner UNIQUEIDENTIFIER,@value DECIMAL(18,6),@md BIT;
        SELECT @person=r.personId,@root=r.rootTenantId,@owner=r.owningTenantId,@value=r.valueCanonical,
         @md=CASE WHEN UPPER(config.udfReceiptField(r.reportedSnapshotJson,NULL,'state'))='MD'
         OR EXISTS(SELECT 1 FROM auth.tenant t WHERE t.tenantId=r.rootTenantId AND t.statePostalCode='MD' AND t.isDeleted=0) THEN 1 ELSE 0 END
        FROM dbo.labResult AS r JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId JOIN config.analyte AS a ON a.analyteId=s.analyteId
        WHERE r.labResultId=@labResultId AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected') AND r.isDeleted=0
         AND a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL' AND r.ageAtCollectionMonths BETWEEN 6 AND 71;
        SET @owner=dbo.udfWritableWorkOwner(@root,dbo.udfPatientWorkOwner(@person)); -- issue 268
        IF @person IS NOT NULL AND config.udfActiveTestingPolicy(@root,auth.udfSessionToday())='MDH-2025-03'
        BEGIN
            DECLARE @lock INT;
            DECLARE @resource NVARCHAR(255)=CONCAT(N'MERIT.testing.',@person);
            EXEC @lock=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction';
            IF @lock<0
            BEGIN
                ;THROW 51409, N'Testing review is busy. Try again.', 1;
            END;
            UPDATE dbo.workTask SET taskStatus='dismissed',completedUtc=SYSUTCDATETIME(),completedByUserId=auth.udfSessionUserId(),
             resolutionNote=N'Maryland clinical timing requires the reviewed testing-plan workflow; generic confirmatory intervals are not used.'
             WHERE personId=@person AND queueCode='confirmatoryTest' AND taskStatus='open' AND isDeleted=0;
            UPDATE dbo.workTask SET isUrgent=CASE WHEN @value>=45 THEN 1 ELSE isUrgent END
             WHERE personId=@person AND queueCode='testingReview' AND taskStatus='open' AND isDeleted=0;
            IF @@ROWCOUNT=0 INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,labResultId,queueCode,title,detail,isUrgent)
            VALUES(NEWID(),@root,@owner,@person,@labResultId,'testingReview',N'Review Maryland blood lead testing plan',
             N'New result: verify Maryland applicability and original history. Review screening, unresolved initial capillary confirmation, clinician-selected early/later venous retesting and remediation/closure readiness. Existing plan anchors are retained. Values at least 45 require urgent clinician coordination; do not wait for routine follow-up.',CASE WHEN @value>=45 THEN 1 ELSE 0 END);
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
    , @ObjectName  = N'uspReconcileTestingReview'
    , @Description = N'uspReconcileTestingReview';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReconcileTestingReview TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReconcileTestingReview TO meritWorkerRole;
END;
GO
