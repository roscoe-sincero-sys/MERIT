-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReviewNotificationObligation
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Auditable applicability decision for an uncertain/corrected result; never counts an exclusion as completed notification.

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

exec dbo.uspReviewNotificationObligation ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReviewNotificationObligation
      @personId UNIQUEIDENTIFIER
      , @notificationObligationId UNIQUEIDENTIFIER
      , @applies BIT
      , @reason NVARCHAR (500)
      , @criteriaConfirmed BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspReviewNotificationObligation]')
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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @exposureCaseId UNIQUEIDENTIFIER, @task UNIQUEIDENTIFIER;
        SELECT @exposureCaseId = exposureCaseId, @task = workTaskId FROM dbo.notificationObligation WITH (UPDLOCK, HOLDLOCK)
         WHERE notificationObligationId = @notificationObligationId AND personId = @personId AND isDeleted = 0 AND completedActivityId IS NULL;
        IF @exposureCaseId IS NOT NULL AND (auth.udfHasPermission (N'case.view') = 0 OR NOT EXISTS
            (SELECT 1 FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId AND personId = @personId AND isDeleted = 0))
        BEGIN
            ;THROW 51404, N'The case was not found for this patient, or is outside your access.', 1;
        END;
        IF @exposureCaseId IS NOT NULL AND auth.udfHasPermission (N'case.edit') = 0
        BEGIN
            ;THROW 51403, N'Case edit permission is required for case-associated records.', 1;
        END;
        IF @task IS NULL OR NULLIF (LTRIM (RTRIM (@reason)), N'') IS NULL
        BEGIN
            ;THROW 51422, N'Choose a pending obligation and record why it applies or does not apply.', 1;
        END;
        IF @applies = 1 AND EXISTS (SELECT 1 FROM dbo.notificationObligation WHERE notificationObligationId = @notificationObligationId
             AND COALESCE (JSON_VALUE (policySnapshotJson, '$.eligibilityMode'), 'threshold') <> 'threshold') AND @criteriaConfirmed <> 1
        BEGIN
            ;THROW 51422, N'Confirm the Maryland evidence checklist and document confirmation tests, recipient and property facts before marking this notice required.', 1;
        END;
        IF @applies = 1 AND EXISTS (SELECT 1 FROM dbo.notificationObligation WHERE notificationObligationId = @notificationObligationId
            AND JSON_VALUE (policySnapshotJson, '$.eligibilityMode') IN ('mdEbl','mdOwner','mdAffectedOwner'))
           AND EXISTS (SELECT 1 FROM dbo.notificationObligation AS o WHERE o.notificationObligationId=@notificationObligationId
             AND dbo.udfMarylandEblConfirmed (o.labResultId,TRY_CONVERT(DECIMAL(18,6),JSON_VALUE(o.policySnapshotJson,'$.minimumValue')))=0)
        BEGIN
            ;THROW 51422, N'Maryland EBL confirmation requires a qualifying venous test or two qualifying capillary tests within 84 days with no discordant result. Verify age and source evidence; leave this review pending while confirmation is outstanding.', 1;
        END;
        UPDATE dbo.notificationObligation SET evaluationState = CASE WHEN @applies = 1 THEN 'eligible' ELSE 'notApplicable' END,
            reviewReason = @reason WHERE notificationObligationId = @notificationObligationId;
        UPDATE dbo.workTask SET taskStatus = CASE WHEN @applies = 1 THEN 'open' ELSE 'dismissed' END,
            completedUtc = CASE WHEN @applies = 0 THEN SYSUTCDATETIME () END,
            completedByUserId = CASE WHEN @applies = 0 THEN auth.udfSessionUserId () END,
            resolutionNote = CASE WHEN @applies = 0 THEN N'Explicit notification applicability review; see obligation reason.' END
         WHERE workTaskId = @task;
        IF @applies = 1 AND EXISTS (SELECT 1 FROM dbo.notificationObligation WHERE notificationObligationId = @notificationObligationId
            AND JSON_VALUE (policySnapshotJson, '$.eligibilityMode') = 'mdEbl' AND recipientKind IN ('patient','caregiver'))
        BEGIN
            INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, personId, exposureCaseId, labResultId)
            SELECT NEWID (), o.rootTenantId, o.owningTenantId, 'assessmentContact', N'Schedule environmental assessment',
              N'COMAR 26.16.08.05: contact caregiver/patient for primary-residence assessment. Record two telephone attempts; if unsuccessful send a first-class final-attempt letter with a response date and required rights information. Record discretionary closure and any property-owner closure letter. No numeric scheduling deadline is inferred.',
              o.personId, o.exposureCaseId, o.labResultId FROM dbo.notificationObligation AS o WHERE o.notificationObligationId = @notificationObligationId
              AND NOT EXISTS (SELECT 1 FROM dbo.workTask WHERE labResultId = o.labResultId AND queueCode = 'assessmentContact' AND isDeleted = 0);
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
    , @ObjectName  = N'uspReviewNotificationObligation'
    , @Description = N'Auditable applicability decision for an uncertain/corrected result; never counts an exclusion as completed notification.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReviewNotificationObligation TO meritWebRole;
END;
GO
