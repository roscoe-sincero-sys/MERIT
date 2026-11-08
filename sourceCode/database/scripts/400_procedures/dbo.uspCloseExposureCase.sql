-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspCloseExposureCase
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Closes a case with a required reason and date; open tasks on it are dismissed and person access is refreshed.

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

exec dbo.uspCloseExposureCase ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T080
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspCloseExposureCase
      @exposureCaseId UNIQUEIDENTIFIER
      , @closureReason  VARCHAR (30)
      , @closedDate     DATE
      , @note           NVARCHAR (2000) = NULL
      , @rowVersion     BINARY (8)
      , @closureReviewToken VARCHAR(64) = NULL
      , @closureReviewConfirmed BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspCloseExposureCase]')
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
    SET @KeyParameters = CONCAT (N'exposureCaseId=', @exposureCaseId, N', closureReason=', @closureReason);

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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @personId UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER, @opened DATE;
        SELECT @personId = personId, @root = rootTenantId, @owner = owningTenantId, @opened = openedDate
          FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId;

        DECLARE @workflowLock INT,@workflowResource NVARCHAR(255)=CONCAT(N'MERIT.workflow.',@personId);
        EXEC @workflowLock=sys.sp_getapplock @Resource=@workflowResource,@LockMode='Exclusive',@LockOwner='Transaction';
        IF @workflowLock<0
        BEGIN
            ;THROW 51409, N'Program work changed during closure review. Reload and try again.', 1;
        END;
        DECLARE @openTasks INT=(SELECT COUNT(*) FROM dbo.workTask WITH(UPDLOCK,HOLDLOCK) WHERE exposureCaseId=@exposureCaseId AND taskStatus='open' AND isDeleted=0);
        DECLARE @liveAttempts INT=(SELECT COUNT(*) FROM dbo.vwPatientActivityStream WHERE exposureCaseId=@exposureCaseId
         AND activityType='contactAttempt' AND isDeleted=0 AND isVoided=0 AND activityDate<=@closedDate AND activityDate>=@opened
         AND outcome IN ('noAnswer','leftMessage','wrongNumber') AND (contactPersonId IS NOT NULL OR NULLIF(TRIM(recipientLabel),N'') IS NOT NULL));
        IF EXISTS(SELECT 1 FROM dbo.workTask AS w LEFT JOIN dbo.personActivity AS a ON a.personActivityId=w.completionActivityId
         WHERE (w.exposureCaseId=@exposureCaseId OR (w.personId=@personId AND w.exposureCaseId IS NULL)) AND w.isDeleted=0
         AND JSON_VALUE(w.workflowSnapshotJson,'$.blocksClosure')='true' AND JSON_VALUE(w.workflowSnapshotJson,'$.closureReason') IN ('all',@closureReason)
         AND w.resolutionKind='completed' AND (TRY_CONVERT(INT,JSON_VALUE(w.workflowSnapshotJson,'$.minimumContactAttempts'))>@liveAttempts OR a.activityDate>@closedDate OR a.isVoided=1))
        BEGIN
            ;THROW 51422, N'The recorded program completion no longer supports this closure date: check corrected contact evidence, the actual attempt count and the completion event date.', 1;
        END;
        IF @openTasks>0 AND (@closureReviewConfirmed<>1 OR @closureReviewToken IS NULL OR @closureReviewToken<>dbo.udfCaseClosureReviewStamp(@exposureCaseId))
        BEGIN
            ;THROW 51422, N'Review the current outstanding task names and dispositions, then confirm closure review. Work may have changed since this page was opened.', 1;
        END;
        IF EXISTS(SELECT 1 FROM dbo.workTask WHERE (exposureCaseId=@exposureCaseId OR (personId=@personId AND exposureCaseId IS NULL)) AND taskStatus='open' AND isDeleted=0
         AND JSON_VALUE(workflowSnapshotJson,'$.blocksClosure')='true' AND JSON_VALUE(workflowSnapshotJson,'$.closureReason') IN ('all',@closureReason))
        BEGIN
            ;THROW 51422, N'Required program work remains open. Complete it with evidence, record a permitted authorized exception, or transfer it to an active case before this closure.', 1;
        END;
        IF EXISTS(SELECT 1 FROM config.workflowDefinition AS d CROSS APPLY config.tvfWorkflowEligibility(d.workflowDefinitionId,@personId,@exposureCaseId,NULL) AS e
         WHERE d.definitionStatus='published' AND d.isDeleted=0 AND d.blocksClosure=1 AND d.closureReason IN ('all',@closureReason)
         AND e.evaluationState IN ('eligible','needsReview') AND NOT EXISTS
         (SELECT 1 FROM dbo.workTask AS w WHERE w.workflowDefinitionId=d.workflowDefinitionId AND w.isDeleted=0 AND w.taskStatus<>'open'
          AND (w.exposureCaseId=@exposureCaseId OR (w.personId=@personId AND w.exposureCaseId IS NULL))
          AND w.resolutionKind IN ('completed','waived','transferred','notApplicable')))
        BEGIN
            ;THROW 51422, N'An applicable program closure requirement has not been resolved. Evaluate the case program checklist and record completion, applicability or an authorized exception.', 1;
        END;

        IF EXISTS (SELECT 1 FROM dbo.notificationObligation WITH (UPDLOCK, HOLDLOCK)
                    WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0 AND completedActivityId IS NULL AND evaluationState <> 'notApplicable')
        BEGIN
            ;THROW 51422, N'Complete required notifications or record a justified applicability decision in the patient notification workspace before closing this case.', 1;
        END;

        DECLARE @mdClosure BIT = CASE WHEN @closureReason='lostToFollowUp' AND EXISTS
         (SELECT 1 FROM dbo.workTask WHERE exposureCaseId=@exposureCaseId AND queueCode='assessmentContact' AND isDeleted=0) THEN 1 ELSE 0 END;
        IF @mdClosure=1 AND NULLIF(TRIM(@note),N'') IS NULL
        BEGIN
            ;THROW 51422, N'Document the Maryland scheduling contact attempts, first-class final letter, response date and discretionary closure decision before closing without an investigation.', 1;
        END;
        IF @closureReason='belowLevelOnRetest' AND EXISTS
         (SELECT 1 FROM dbo.exposureCase AS c JOIN config.analyte AS a ON a.analyteId=c.analyteId
          JOIN dbo.labResult AS q ON q.labResultId=c.qualifyingLabResultId
          WHERE c.exposureCaseId=@exposureCaseId AND a.code='PB' AND q.ageAtCollectionMonths BETWEEN 0 AND 71
          AND (UPPER(config.udfReceiptField(q.reportedSnapshotJson,NULL,'state'))='MD'
            OR EXISTS(SELECT 1 FROM dbo.workTask WHERE personId=@personId AND isDeleted=0 AND testingPlanJson IS NOT NULL)))
        BEGIN
            DECLARE @low TABLE(id UNIQUEIDENTIFIER,draw DATE,value DECIMAL(18,6),op VARCHAR(4));
            INSERT @low SELECT TOP(2) r.labResultId,r.collectedDate,r.valueCanonical,r.operator FROM dbo.labResult AS r
             JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId JOIN config.analyte AS a ON a.analyteId=s.analyteId
             WHERE r.personId=@personId AND r.isDeleted=0 AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected')
              AND a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL'
              AND r.collectedDate>=(SELECT q.collectedDate FROM dbo.exposureCase AS c JOIN dbo.labResult AS q ON q.labResultId=c.qualifyingLabResultId WHERE c.exposureCaseId=@exposureCaseId)
             ORDER BY r.collectedDate DESC,r.labResultId;
            IF (SELECT COUNT(*) FROM @low)<>2 OR EXISTS(SELECT 1 FROM @low WHERE value IS NULL OR op IS NULL OR NOT ((op='=' AND value<3.5) OR (op='<' AND value<=3.5)))
              OR (SELECT MAX(draw) FROM @low)<DATEADD(MONTH,3,(SELECT MIN(draw) FROM @low)) OR (SELECT MAX(draw) FROM @low)>@closedDate
              OR NOT EXISTS(SELECT 1 FROM dbo.workTask AS w WHERE w.personId=@personId AND w.exposureCaseId=@exposureCaseId
               AND w.queueCode='testingDischarge' AND w.taskStatus='done' AND w.isDeleted=0 AND JSON_VALUE(w.testingPlanJson,'$.remediationConfirmed')='true'
               AND TRY_CONVERT(UNIQUEIDENTIFIER,JSON_VALUE(w.testingPlanJson,'$.anchorResultId')) IN (SELECT id FROM @low)
               AND TRY_CONVERT(UNIQUEIDENTIFIER,JSON_VALUE(w.testingPlanJson,'$.previousLowResultId')) IN (SELECT id FROM @low))
              OR EXISTS(SELECT 1 FROM dbo.workTask WHERE personId=@personId AND queueCode='testingFollowUp' AND taskStatus='open' AND isDeleted=0
               AND JSON_VALUE(testingPlanJson,'$.kind') IN ('confirmation','early','later'))
            BEGIN
                ;THROW 51422, N'Maryland below-level closure requires reviewed completed remediation, the latest two consecutive low tests at least three calendar months apart, and resolved clinical testing plans. Record readiness in the patient testing-plan workspace.', 1;
            END;
        END;
        IF @closedDate < @opened
        BEGIN
            ;THROW 51422, N'The closed date cannot be before the opened date.', 1;
        END;

        UPDATE dbo.exposureCase
           SET caseStatus = 'closed', closureReason = @closureReason, closedDate = @closedDate
         WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0 AND caseStatus <> 'closed' AND rowVersion = @rowVersion;
        IF @@ROWCOUNT = 0
        BEGIN
            ;THROW 51409, N'This record was changed by someone else, or is no longer available. Reload it and try again.', 1;
        END;

        INSERT dbo.caseActivity (caseActivityId, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate,
                                 workerUserId, note)
        VALUES (CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, @owner, @exposureCaseId, 'statusChange', @closedDate, auth.udfSessionUserId (),
                CONCAT (N'Case closed: ', @closureReason, IIF (@note IS NULL, N'', N'. ' + @note)));

        UPDATE dbo.workTask
           SET taskStatus = 'dismissed', completedUtc = SYSUTCDATETIME (), completedByUserId = auth.udfSessionUserId (),
               resolutionNote = CONCAT(N'Automatically dismissed after reviewed case closure: ',@closureReason,N'. ',COALESCE(@note,N'')),resolutionKind='autoDismissed'
         WHERE exposureCaseId = @exposureCaseId AND taskStatus = 'open' AND isDeleted = 0 AND queueCode NOT IN ('notificationDue','assessmentOwnerClosure');

        IF @mdClosure=1 AND NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE exposureCaseId=@exposureCaseId AND queueCode='assessmentOwnerClosure' AND taskStatus='open' AND isDeleted=0)
            INSERT dbo.workTask (workTaskId,rootTenantId,owningTenantId,queueCode,title,detail,personId,exposureCaseId)
            VALUES (NEWID (),@root,@owner,'assessmentOwnerClosure',N'Review/send property-owner case-closure letter',
              N'COMAR 26.16.08.05D: verify primary-residence ownership. If not owned by caregiver/pregnant patient, send the owner a first-class letter explaining inability to contact and closure without an investigation. Complete with recipient, address and letter evidence; otherwise dismiss with the source-based ownership reason. No numeric deadline is inferred.',@personId,@exposureCaseId);

        EXEC dbo.uspRefreshPersonAccess @personId = @personId;

        SET @Comments = N'Case closed.';

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
    , @ObjectName  = N'uspCloseExposureCase'
    , @Description = N'Closes a case with a required reason and date; open tasks on it are dismissed and person access is refreshed.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspCloseExposureCase TO meritWebRole;
END;
GO
