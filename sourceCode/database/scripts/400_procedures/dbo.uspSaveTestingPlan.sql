-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSaveTestingPlan
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

uspSaveTestingPlan

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

exec dbo.uspSaveTestingPlan ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSaveTestingPlan
      @workTaskId UNIQUEIDENTIFIER
      , @personId UNIQUEIDENTIFIER
      , @planKind VARCHAR (20)
      , @evidence NVARCHAR (1000)
      , @clinicianConfirmed BIT
      , @exposureCaseId UNIQUEIDENTIFIER = NULL
      , @labResultId UNIQUEIDENTIFIER = NULL
      , @targetDate DATE = NULL
      , @remediationConfirmed BIT = 0
      , @currentGuidanceConfirmed BIT = 0
      , @currentGuidanceReason NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSaveTestingPlan]')
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
        END;IF @exposureCaseId IS NOT NULL AND (auth.udfHasPermission (N'case.view') = 0 OR NOT EXISTS
            (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId AND personId = @personId AND isDeleted = 0))
        BEGIN
            ;THROW 51404, N'The case was not found for this patient, or is outside your access.', 1;
        END;
        IF @exposureCaseId IS NOT NULL AND auth.udfHasPermission (N'case.edit') = 0
        BEGIN
            ;THROW 51403, N'Case edit permission is required for case-associated records.', 1;
        END;DECLARE @root UNIQUEIDENTIFIER, @owner UNIQUEIDENTIFIER;
        SELECT @root = rootTenantId, @owner = owningTenantId FROM dbo.person WHERE personId = @personId;
        IF @exposureCaseId IS NOT NULL SELECT @owner = owningTenantId FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId;

        IF auth.udfHasPermission(N'task.edit')=0 OR @clinicianConfirmed<>1 OR NULLIF(TRIM(@evidence),N'') IS NULL
        BEGIN
            ;THROW 51422, N'Confirm Maryland applicability, source history and the clinician-approved plan; include its evidence.', 1;
        END;
        IF @planKind NOT IN ('screen12','screen24','confirmation','early','later','discharge')
        BEGIN
            ;THROW 51422, N'Choose screening, confirmation, follow-up or discharge review.', 1;
        END;
        IF EXISTS(SELECT 1 FROM dbo.workTask WHERE workTaskId=@workTaskId)
        BEGIN
            ;THROW 51409, N'This plan was already recorded. Review the existing history before submitting again.', 1;
        END;
        DECLARE @lock INT;
        DECLARE @resource NVARCHAR(255)=CONCAT(N'MERIT.testing.',@personId);
        EXEC @lock=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction';
        IF @lock<0
        BEGIN
            ;THROW 51409, N'Testing plan is busy. Try again.', 1;
        END;
        DECLARE @today DATE=auth.udfSessionToday (),@dob DATE=(SELECT dob FROM dbo.person WHERE personId=@personId),
         @draw DATE,@sample VARCHAR(20),@value DECIMAL(18,6),@op VARCHAR(4),@latest DATE,@due DATE,@urgent BIT=0,@advice NVARCHAR(600),@second UNIQUEIDENTIFIER;
        IF @dob IS NULL OR @dob> @today
        BEGIN
            ;THROW 51422, N'Verify the patient birth date before creating a childhood testing plan.', 1;
        END;
        DECLARE @r TABLE(labResultId UNIQUEIDENTIFIER,collectedDate DATE,sampleType VARCHAR(20),operator VARCHAR(4),valueCanonical DECIMAL(18,6),resultRowStatus VARCHAR(20),resultStatus VARCHAR(20));
        INSERT @r
        SELECT r.labResultId,r.collectedDate,r.sampleType,r.operator,r.valueCanonical,r.resultRowStatus,r.resultStatus
        FROM dbo.labResult AS r JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId=r.analyteSpecimenId
        JOIN config.analyte AS a ON a.analyteId=s.analyteId
        WHERE r.personId=@personId AND r.isDeleted=0 AND a.code='PB' AND s.specimen='wholeBlood' AND s.canonicalUnit=N'ug/dL'
         AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected')
        ;
        IF @planKind IN ('screen12','screen24')
        BEGIN
            IF @today>=DATEADD(MONTH,72,@dob)
            BEGIN
                ;THROW 51422, N'The childhood catch-up screening window ends before 72 months. Obtain an individual clinical plan.', 1;
            END;
            SET @labResultId=NULL;
            SET @latest=DATEADD(MONTH,CASE WHEN @planKind='screen12' THEN 12 ELSE 24 END,@dob);
            IF EXISTS(SELECT 1 FROM @r WHERE sampleType IN ('venous','capillary') AND collectedDate>=@latest
              AND collectedDate<DATEADD(MONTH,72,@dob) AND collectedDate<=@today)
            BEGIN
                ;THROW 51422, N'An age-appropriate screening result is already recorded. Verify the history instead of creating a duplicate missed-screening plan.', 1;
            END;
            SET @advice=N'Provider screening at the 12- or 24-month visit; if missed, arrange promptly. Verify outside records, residence, exemptions and missed-screening history. Certification is not tracked by MDE.';
        END
        ELSE
        BEGIN
            SELECT @draw=collectedDate,@sample=sampleType,@value=valueCanonical,@op=operator FROM @r WHERE labResultId=@labResultId;
            IF @draw IS NULL OR @draw>@today OR @draw>=DATEADD(MONTH,72,@dob) OR @draw<DATEADD(MONTH,6,@dob)
            BEGIN
                ;THROW 51422, N'Choose a valid final blood lead result collected at age 6 to under 72 months. Other ages require an individual clinical plan.', 1;
            END;
            IF @planKind='confirmation'
            BEGIN
                IF @sample<>'capillary' OR @value<3.5 OR @op<>'='
                BEGIN
                    ;THROW 51422, N'Confirmation starts at an elevated capillary draw; uncertain results need source clarification.', 1;
                END;
                IF EXISTS(SELECT 1 FROM @r WHERE sampleType='venous' AND collectedDate>=@draw)
                BEGIN
                    ;THROW 51422, N'A venous result is already recorded on or after this draw. Review it before planning confirmation.', 1;
                END;
                IF EXISTS(SELECT 1 FROM @r AS r WHERE r.sampleType='capillary' AND r.valueCanonical>=3.5 AND r.operator IN ('=','>') AND r.collectedDate<@draw
                  AND r.collectedDate>COALESCE((SELECT MAX(collectedDate) FROM @r WHERE sampleType='venous' AND collectedDate<@draw),'00010101'))
                BEGIN
                    ;THROW 51422, N'An earlier unresolved elevated capillary draw exists. Use that initial draw; later capillary tests do not restart confirmation.', 1;
                END;
                SET @latest=CASE WHEN @value>=45 THEN @draw WHEN @value>=20 THEN DATEADD(DAY,14,@draw) WHEN @value>=10 THEN DATEADD(DAY,28,@draw) ELSE DATEADD(DAY,84,@draw) END;
                SET @urgent=CASE WHEN @value>=45 THEN 1 ELSE 0 END;
                SET @advice=CASE WHEN @urgent=1 THEN N'Urgent clinician coordination now: confirmation within 48 hours of the actual initial draw timestamp. Date-only data cannot establish the exact 48-hour cutoff; record the timestamp and clinician plan in evidence.'
                  ELSE N'MDH March 2025 Table 2: confirmation within 12 weeks for 3.5 to under 10, 4 weeks for 10 to under 20, 2 weeks for 20 to under 45. Greater elevations require faster clinician assessment.' END;
            END
            ELSE IF @planKind IN ('early','later')
            BEGIN
                IF EXISTS(SELECT 1 FROM @r WHERE collectedDate>@draw)
                BEGIN
                    ;THROW 51422, N'A newer blood lead test exists. Review the latest test before choosing the next follow-up anchor.', 1;
                END;
                IF @sample<>'venous' OR @op<>'='
                BEGIN
                    ;THROW 51422, N'Follow-up intervals require an exact venous result; use clinical/source review for censored or capillary results.', 1;
                END;
                SET @urgent=CASE WHEN @value>=45 THEN 1 ELSE 0 END;
                SET @latest=CASE WHEN @value>=45 THEN @draw WHEN @value<3.5 THEN DATEADD(MONTH,3,@draw)
                  WHEN @value>=20 THEN DATEADD(MONTH,CASE WHEN @planKind='early' THEN 1 ELSE 3 END,@draw)
                  WHEN @value>=10 THEN DATEADD(MONTH,CASE WHEN @planKind='early' THEN 3 ELSE 6 END,@draw)
                  ELSE DATEADD(MONTH,CASE WHEN @planKind='early' THEN 3 ELSE 9 END,@draw) END;
                SET @advice=CASE WHEN @urgent=1 THEN N'As soon as possible: urgent clinician coordination and individualized treatment plan; do not wait for a routine retest.'
                  WHEN @value<3.5 THEN N'Below-reference monitoring: repeat every 3 months toward reviewed closure; remediation and two consecutive low results are required.'
                  ELSE N'MDH March 2025 Table 4. Early: 3 months / 1-3 months / 2 weeks-1 month. Later, only after clinician-reviewed decline: 6-9 / 3-6 / 1-3 months. Select an earlier target within the approved clinical plan; never infer decline from test count alone.' END;
            END
            ELSE
            BEGIN
                IF @exposureCaseId IS NULL OR @remediationConfirmed<>1
                BEGIN
                    ;THROW 51422, N'Choose the active case and attest completed environmental remediation with source evidence.', 1;
                END;
                DECLARE @recent TABLE(id UNIQUEIDENTIFIER,draw DATE,value DECIMAL(18,6),op VARCHAR(4));
                INSERT @recent SELECT TOP(2) labResultId,collectedDate,valueCanonical,operator FROM @r
                  WHERE collectedDate<=@today AND collectedDate>=(SELECT r.collectedDate FROM dbo.exposureCase AS c JOIN dbo.labResult AS r ON r.labResultId=c.qualifyingLabResultId WHERE c.exposureCaseId=@exposureCaseId)
                  ORDER BY collectedDate DESC,labResultId;
                IF (SELECT COUNT(*) FROM @recent)<>2 OR EXISTS(SELECT 1 FROM @recent WHERE value IS NULL OR op IS NULL OR NOT ((op='=' AND value<3.5) OR (op='<' AND value<=3.5)))
                  OR (SELECT MAX(draw) FROM @recent)<DATEADD(MONTH,3,(SELECT MIN(draw) FROM @recent))
                  OR @draw<>(SELECT MAX(draw) FROM @recent)
                BEGIN
                    ;THROW 51422, N'Closure needs the latest two consecutive valid blood lead tests below 3.5, at least three calendar months apart, and completed environmental remediation. Select the latest result.', 1;
                END;
                SELECT @second=id FROM @recent WHERE id<>@labResultId;
                SET @latest=@today;
                SET @advice=N'MDH March 2025 Table 6: remediation reviewed and two consecutive below-reference tests at least three calendar months apart. This records readiness evidence; it does not close the case.';
            END;
        END;
        IF EXISTS(SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId=@exposureCaseId AND caseStatus='closed')
        BEGIN
            ;THROW 51422, N'Use an active case or a patient-only testing plan.', 1;
        END;
        -- A current-care plan for an old draw is not a reconstruction of the policy in force at collection.
        DECLARE @policyReference DATE=COALESCE(@draw,@latest);
        IF @policyReference<'20250301' AND (ISNULL(@currentGuidanceConfirmed,0)<>1 OR NULLIF(TRIM(@currentGuidanceReason),N'') IS NULL)
        BEGIN
            ;THROW 51422, N'This source predates March 2025 guidance. Historical policy reconstruction is not supported. For current care, explicitly confirm the clinician chose current guidance and document why; otherwise obtain an approved historical plan.', 1;
        END;
        SET @due=COALESCE(@targetDate,@latest);
        IF @due>@latest OR (@draw IS NOT NULL AND @due<@draw) OR @due<@dob
        BEGIN
            ;THROW 51422, N'The target cannot extend the guideline window or precede the source draw/birth date. Urgent plans require coordination now.', 1;
        END;
        IF EXISTS(SELECT 1 FROM dbo.workTask WHERE personId=@personId AND isDeleted=0 AND taskStatus='open' AND queueCode='testingFollowUp'
         AND (JSON_VALUE(testingPlanJson,'$.kind')=@planKind OR (@planKind IN ('early','later') AND JSON_VALUE(testingPlanJson,'$.kind') IN ('early','later'))))
        BEGIN
            ;THROW 51422, N'An open plan of this type exists. Complete it with a real test or dismiss it with a documented clinical reason before replacing it.', 1;
        END;
        DECLARE @json NVARCHAR(MAX)=(SELECT 'MDH-2025-03' AS policyVersion,@planKind AS kind,@labResultId AS anchorResultId,@draw AS anchorDate,
         @second AS previousLowResultId,@remediationConfirmed AS remediationConfirmed,@evidence AS clinicalEvidence,
         @policyReference AS policyReferenceDate,'currentCare' AS policyPurpose,
         @currentGuidanceConfirmed AS currentGuidanceConfirmed,@currentGuidanceReason AS currentGuidanceReason,
         auth.udfSessionToday() AS reviewedDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
        INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,labResultId,queueCode,title,detail,dueDate,isUrgent,testingPlanJson,taskStatus,completedUtc,completedByUserId,resolutionNote)
        VALUES(@workTaskId,@root,@owner,@personId,@exposureCaseId,@labResultId,CASE WHEN @planKind='discharge' THEN 'testingDischarge' ELSE 'testingFollowUp' END,
         CONCAT(N'Maryland testing: ',@planKind),CONCAT(@advice,N' Source review: ',@evidence,
         CASE WHEN @policyReference<'20250301' THEN CONCAT(N' Current-care policy decision: ',@currentGuidanceReason) END),@due,@urgent,@json,
         CASE WHEN @planKind='discharge' THEN 'done' ELSE 'open' END,CASE WHEN @planKind='discharge' THEN SYSUTCDATETIME() END,
         CASE WHEN @planKind='discharge' THEN auth.udfSessionUserId() END,CASE WHEN @planKind='discharge' THEN @evidence END);
        UPDATE dbo.workTask SET taskStatus='done',completedUtc=SYSUTCDATETIME(),completedByUserId=auth.udfSessionUserId(),resolutionNote=N'Clinician-approved testing plan recorded.'
         WHERE personId=@personId AND queueCode='testingReview' AND taskStatus='open' AND isDeleted=0;

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
    , @ObjectName  = N'uspSaveTestingPlan'
    , @Description = N'uspSaveTestingPlan';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSaveTestingPlan TO meritWebRole;
END;
GO
