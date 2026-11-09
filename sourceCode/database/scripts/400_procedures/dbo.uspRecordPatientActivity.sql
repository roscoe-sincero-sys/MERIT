-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspRecordPatientActivity
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Records an actual patient activity and, only with explicit matching completion evidence, completes a notification atomically.

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

exec dbo.uspRecordPatientActivity ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspRecordPatientActivity
      @personActivityId UNIQUEIDENTIFIER
      , @personId UNIQUEIDENTIFIER
      , @activityType VARCHAR (30)
      , @activityDate DATE
      , @audience VARCHAR (12)
      , @channel VARCHAR (12)
      , @outcome VARCHAR (30)
      , @contactPersonId UNIQUEIDENTIFIER = NULL
      , @recipientLabel NVARCHAR (200) = NULL
      , @note NVARCHAR (2000) = NULL
      , @exposureCaseId UNIQUEIDENTIFIER = NULL
      , @labResultId UNIQUEIDENTIFIER = NULL
      , @notificationObligationId UNIQUEIDENTIFIER = NULL
      , @completeMatching BIT = 0
      , @noticeFormReference NVARCHAR (300) = NULL
      , @ownerScopeConfirmed BIT = 0
      , @returnCount BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspRecordPatientActivity]')
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

        DECLARE @today DATE = auth.udfSessionToday ();
        IF @notificationObligationId IS NOT NULL
        BEGIN
            DECLARE @zone VARCHAR (100);
            SELECT @zone = timeZoneId FROM dbo.notificationObligation WHERE notificationObligationId = @notificationObligationId AND personId = @personId AND isDeleted = 0;
            IF @zone IS NOT NULL SET @today = CAST (SYSUTCDATETIME () AT TIME ZONE 'UTC' AT TIME ZONE @zone AS DATE);
        END;
        IF @activityDate > @today OR @activityDate < '1900-01-01' OR NULLIF (LTRIM (RTRIM (@note)), N'') IS NULL
        BEGIN
            ;THROW 51422, N'Enter an actual activity date and a description of what happened.', 1;
        END;
        IF @audience = 'internal' AND (@channel <> 'internal' OR @contactPersonId IS NOT NULL)
        BEGIN
            ;THROW 51422, N'Internal notes cannot name an external contact or delivery channel.', 1;
        END;
        IF @activityType='contactAttempt' AND (@audience='internal' OR @channel='internal' OR @outcome NOT IN ('reached','noAnswer','leftMessage','wrongNumber'))
        BEGIN
            ;THROW 51422, N'A contact attempt requires an external recipient, channel and actual contact outcome. Use a note for internal work.', 1;
        END;
        IF @contactPersonId IS NOT NULL AND (@contactPersonId <> @personId AND NOT EXISTS
            (SELECT 1 FROM dbo.personRelationship AS r WHERE r.isDeleted = 0
              AND ((r.personId = @personId AND r.relatedPersonId = @contactPersonId) OR (r.relatedPersonId = @personId AND r.personId = @contactPersonId))
              AND (r.startDate IS NULL OR r.startDate <= @activityDate) AND (r.endDate IS NULL OR r.endDate >= @activityDate)))
        BEGIN
            ;THROW 51422, N'Choose the patient or a relationship valid on the activity date.', 1;
        END;
        IF @contactPersonId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.person WHERE personId = @contactPersonId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The selected contact is not available.', 1;
        END;
        IF @audience = 'patient' AND (@contactPersonId IS NULL OR @contactPersonId <> @personId)
        BEGIN
            ;THROW 51422, N'Choose the patient as recipient.', 1;
        END;
        IF @audience = 'caregiver' AND (@contactPersonId IS NULL OR @contactPersonId = @personId OR NOT EXISTS
            (SELECT 1 FROM dbo.personRelationship AS r WHERE r.personId = @personId AND r.relatedPersonId = @contactPersonId AND r.isDeleted = 0
               AND (r.startDate IS NULL OR r.startDate <= @activityDate) AND (r.endDate IS NULL OR r.endDate >= @activityDate)
               AND (r.hasLegalCustody = 1 OR r.isPrimaryContact = 1 OR r.role IN ('parent','legalGuardian','fosterParent','caregiver'))))
        BEGIN
            ;THROW 51422, N'Choose a recorded caregiver or guardian valid on this date. Add or verify the relationship first.', 1;
        END;
        IF @audience <> 'internal' AND (@channel = 'internal' OR (@contactPersonId IS NULL AND NULLIF (LTRIM (RTRIM (@recipientLabel)), N'') IS NULL))
        BEGIN
            ;THROW 51422, N'Record the external recipient and how contact was attempted or completed.', 1;
        END;
        IF @labResultId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.labResult WHERE labResultId = @labResultId AND personId = @personId AND isDeleted = 0)
        BEGIN
            ;THROW 51404, N'The result is not available for this patient.', 1;
        END;
        IF @activityType = 'notificationCompleted' AND EXISTS (SELECT 1 FROM dbo.labResult WHERE labResultId = @labResultId AND collectedDate > @activityDate)
        BEGIN
            ;THROW 51422, N'Notification cannot precede collection of the blood test.', 1;
        END;
        IF @activityType = 'notificationCompleted' AND (@outcome <> 'completed' OR (@contactPersonId IS NULL AND @audience <> 'owner') OR @labResultId IS NULL OR @audience NOT IN ('patient','caregiver','owner'))
        BEGIN
            ;THROW 51422, N'Completed notification requires a result, a recorded recipient, a delivery channel and a completed outcome.', 1;
        END;
        IF @activityType = 'notificationCompleted' AND @audience = 'owner' AND (@ownerScopeConfirmed <> 1 OR NULLIF (TRIM (@recipientLabel), N'') IS NULL)
        BEGIN
            ;THROW 51422, N'Name the property-owner recipients and confirm that every required owner was notified. List property addresses and delivery details in the activity note; use the last actual delivery date.', 1;
        END;
        IF @notificationObligationId IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.notificationObligation WHERE notificationObligationId = @notificationObligationId
            AND COALESCE (JSON_VALUE (policySnapshotJson, '$.eligibilityMode'), 'threshold') <> 'threshold')
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.notificationObligation AS o WHERE o.notificationObligationId=@notificationObligationId
              AND dbo.udfMarylandEblConfirmed (o.labResultId,TRY_CONVERT(DECIMAL(18,6),JSON_VALUE(o.policySnapshotJson,'$.minimumValue')))=0)
            BEGIN
                ;THROW 51422, N'The Maryland confirmation evidence has changed or is insufficient. Review the source tests before completing delivery.', 1;
            END;
            IF NULLIF (TRIM (@noticeFormReference), N'') IS NULL
            BEGIN
                ;THROW 51422, N'Record the Department-approved notice form or attached document reference used for this Maryland notice.', 1;
            END;
            IF @audience = 'caregiver' AND NOT EXISTS (SELECT 1 FROM dbo.personRelationship WHERE personId = @personId AND relatedPersonId = @contactPersonId
                AND isDeleted = 0 AND (startDate IS NULL OR startDate <= @activityDate) AND (endDate IS NULL OR endDate >= @activityDate)
                AND (hasLegalCustody = 1 OR role IN ('parent','legalGuardian','fosterParent')))
            BEGIN
                ;THROW 51422, N'Maryland caregiver notice requires a recorded parent, guardian or legal responsibility; primary-contact status alone is insufficient.', 1;
            END;
        END;
        IF EXISTS (SELECT 1 FROM dbo.caseActivity WHERE caseActivityId = @personActivityId)
        BEGIN
            ;THROW 51422, N'That activity identifier already belongs to a case event.', 1;
        END;
        IF EXISTS (SELECT 1 FROM dbo.personActivity WHERE personActivityId = @personActivityId)
        BEGIN
            ;THROW 51409, N'This activity was already submitted. Refresh the history before submitting another event.', 1;
        END;
        INSERT dbo.personActivity (personActivityId, rootTenantId, owningTenantId, personId, exposureCaseId, labResultId,
            activityType, activityDate, audience, channel, contactPersonId, recipientLabel, noticeFormReference, ownerScopeConfirmed, outcome, note, workerUserId)
        VALUES (@personActivityId, @root, @owner, @personId, @exposureCaseId, @labResultId, @activityType, @activityDate,
            @audience, @channel, @contactPersonId, @recipientLabel, @noticeFormReference, @ownerScopeConfirmed, @outcome, @note, auth.udfSessionUserId ());
        IF @notificationObligationId IS NOT NULL
        BEGIN
            DECLARE @task UNIQUEIDENTIFIER;
            SELECT @task = workTaskId FROM dbo.notificationObligation WITH (UPDLOCK, HOLDLOCK)
             WHERE notificationObligationId = @notificationObligationId AND personId = @personId AND labResultId = @labResultId
               AND recipientKind = @audience AND isDeleted = 0 AND completedActivityId IS NULL AND evaluationState = 'eligible'
               AND (@activityType = 'notificationCompleted' AND @outcome = 'completed')
               AND ((exposureCaseId IS NULL AND @exposureCaseId IS NULL) OR exposureCaseId = @exposureCaseId);
            IF @task IS NULL
            BEGIN
                ;THROW 51422, N'This activity cannot complete that obligation, or completion was already recorded. Nothing was saved.', 1;
            END;
            DECLARE @targets TABLE (id UNIQUEIDENTIFIER PRIMARY KEY, taskId UNIQUEIDENTIFIER);
            INSERT @targets SELECT notificationObligationId, workTaskId FROM dbo.notificationObligation WITH (UPDLOCK, HOLDLOCK)
             WHERE personId = @personId AND labResultId = @labResultId AND recipientKind = @audience
               AND isDeleted = 0 AND completedActivityId IS NULL AND evaluationState = 'eligible' AND anchorDate <= @activityDate
               AND ((exposureCaseId IS NULL AND @exposureCaseId IS NULL) OR exposureCaseId = @exposureCaseId)
               AND (notificationObligationId = @notificationObligationId OR (@completeMatching = 1 AND (COALESCE (JSON_VALUE (policySnapshotJson, '$.eligibilityMode'), 'threshold') = 'threshold'
                 OR (JSON_VALUE(policySnapshotJson,'$.eligibilityMode')=(SELECT JSON_VALUE(picked.policySnapshotJson,'$.eligibilityMode') FROM dbo.notificationObligation AS picked WHERE picked.notificationObligationId=@notificationObligationId)
                   AND dbo.udfMarylandEblConfirmed(labResultId,TRY_CONVERT(DECIMAL(18,6),JSON_VALUE(policySnapshotJson,'$.minimumValue')))=1))));
            IF NOT EXISTS (SELECT 1 FROM @targets WHERE id = @notificationObligationId)
            BEGIN
                ;THROW 51422, N'Completed notification cannot precede receipt of this blood test. Nothing was saved.', 1;
            END;
            UPDATE o SET completedActivityId = @personActivityId, completedDate = @activityDate
              FROM dbo.notificationObligation AS o JOIN @targets AS t ON t.id = o.notificationObligationId;
            UPDATE w SET taskStatus = 'done', completedUtc = SYSUTCDATETIME (), completedByUserId = auth.udfSessionUserId (),
                resolutionNote = N'Completed notification recorded with recipient and result evidence.'
              FROM dbo.workTask AS w JOIN @targets AS t ON t.taskId = w.workTaskId;
        END;
        IF @returnCount=1 SELECT COUNT (*) AS failedContactAttempts FROM dbo.vwPatientActivityStream
         WHERE exposureCaseId = @exposureCaseId AND activityType = 'contactAttempt' AND isDeleted = 0 AND isVoided = 0
           AND outcome IN ('noAnswer','leftMessage','wrongNumber');

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
    , @ObjectName  = N'uspRecordPatientActivity'
    , @Description = N'Records an actual patient activity and, only with explicit matching completion evidence, completes a notification atomically.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspRecordPatientActivity TO meritWebRole;
END;
GO
