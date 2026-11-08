-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReconcileNotifications
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Idempotent receipt/threshold rule evaluation in batches. Every matching rule gets its own deadline; censored results require explicit applicability review.

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

exec dbo.uspReconcileNotifications ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReconcileNotifications
      @labResultId UNIQUEIDENTIFIER = NULL
      , @notificationRuleId UNIQUEIDENTIFIER = NULL
      , @personId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspReconcileNotifications]')
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

        IF auth.udfHasPermission (N'result.edit') = 0 AND auth.udfHasPermission (N'person.edit') = 0 AND auth.udfHasPermission (N'config.edit') = 0
        BEGIN
            ;THROW 51403, N'Clinical edit or policy configuration permission is required.', 1;
        END;
        IF @labResultId IS NULL AND @notificationRuleId IS NULL AND @personId IS NULL
        BEGIN
            ;THROW 51422, N'Choose a patient, result or approved rule to evaluate.', 1;
        END;
        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId (), @lock INT,
                @resource NVARCHAR (100) = CONCAT (N'MERIT.notifications.', auth.udfSessionRootTenantId ());
        EXEC @lock = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @lock < 0
        BEGIN
            ;THROW 51409, N'Notification evaluation is busy. Retry shortly.', 1;
        END;
        SELECT TOP (500) CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER) AS notificationObligationId, CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER) AS workTaskId,
               r.personId, r.labResultId, r.rootTenantId, COALESCE (c.owningTenantId, r.owningTenantId) AS owningTenantId,
               c.exposureCaseId, n.notificationRuleId, n.ruleName, n.recipientKind, n.timeZoneId, n.triggerEvent,
               CAST (r.receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE) AS anchorDate,
               config.udfNotificationDueDate (CAST (r.receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE),
                   n.dueInDays, n.dayBasis, n.holidayDatesJson) AS dueDate,
               CASE WHEN n.eligibilityMode = 'threshold' AND n.recipientKind <> 'owner' AND r.operator = '=' AND (n.population = 'all' OR n.population = r.population)
                      AND ((n.minimumAgeMonths IS NULL AND n.maximumAgeMonths IS NULL) OR r.ageAtCollectionMonths IS NOT NULL)
                    THEN 'eligible' ELSE 'needsReview' END AS evaluationState,
               (SELECT n.ruleName, n.rulePurpose, n.eligibilityMode, n.population, n.minimumAgeMonths, n.maximumAgeMonths, n.minimumValue, n.maximumValue,
                       n.recipientKind, n.triggerEvent, n.dueInDays, n.dayBasis, n.timeZoneId, n.calendarName,
                       JSON_QUERY (n.holidayDatesJson) AS holidays, n.effectiveFrom, n.effectiveTo, n.approvalReference
                  FOR JSON PATH, WITHOUT_ARRAY_WRAPPER) AS policySnapshotJson
          INTO #new FROM dbo.labResult AS r JOIN config.notificationRule AS n ON n.rootTenantId = r.rootTenantId
               AND n.analyteSpecimenId = r.analyteSpecimenId AND n.isActive = 1 AND n.isDeleted = 0 AND n.rulePurpose = 'notification'
          OUTER APPLY (SELECT TOP (1) ec.exposureCaseId, ec.owningTenantId FROM dbo.exposureCase AS ec
                       JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
                      WHERE ec.personId = r.personId AND ec.analyteId = s.analyteId AND ec.isDeleted = 0
                        AND (ec.qualifyingLabResultId = r.labResultId OR ec.caseStatus <> 'closed')
                      ORDER BY CASE WHEN ec.qualifyingLabResultId = r.labResultId THEN 0 ELSE 1 END, ec.openedDate DESC) AS c
         WHERE r.rootTenantId = @root AND r.isDeleted = 0 AND r.resultRowStatus = 'valid' AND r.resultStatus IN ('final','corrected')
           AND r.personId IS NOT NULL AND r.valueCanonical IS NOT NULL
           AND (@labResultId IS NULL OR r.labResultId = @labResultId) AND (@personId IS NULL OR r.personId = @personId)
           AND (@notificationRuleId IS NULL OR n.notificationRuleId = @notificationRuleId)
           AND EXISTS (SELECT 1 FROM auth.tenantClosure AS tc WHERE tc.ancestorTenantId = n.owningTenantId AND tc.descendantTenantId = r.owningTenantId AND tc.isDeleted = 0)
           AND CAST (r.receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE) >= n.effectiveFrom
           AND (n.effectiveTo IS NULL OR CAST (r.receivedUtc AT TIME ZONE 'UTC' AT TIME ZONE n.timeZoneId AS DATE) <= n.effectiveTo)
           AND (n.population = 'all' OR n.population = r.population OR r.population = 'unknown')
           AND (n.minimumAgeMonths IS NULL OR r.ageAtCollectionMonths IS NULL OR r.ageAtCollectionMonths >= n.minimumAgeMonths)
           AND (n.maximumAgeMonths IS NULL OR r.ageAtCollectionMonths IS NULL OR r.ageAtCollectionMonths <= n.maximumAgeMonths)
           AND ((r.operator = '=' AND r.valueCanonical >= n.minimumValue AND (n.maximumValue IS NULL OR r.valueCanonical < n.maximumValue))
             OR (r.operator = '<' AND r.valueCanonical > n.minimumValue)
             OR (r.operator = '>' AND (n.maximumValue IS NULL OR r.valueCanonical < n.maximumValue)))
           AND NOT EXISTS (SELECT 1 FROM dbo.notificationObligation AS o WHERE o.notificationRuleId = n.notificationRuleId AND o.labResultId = r.labResultId AND o.isDeleted = 0)
         ORDER BY r.receivedUtc, r.labResultId, n.notificationRuleId;
        IF EXISTS (SELECT 1 FROM #new WHERE dueDate IS NULL)
        BEGIN
            ;THROW 51422, N'The approved calendar cannot produce a bounded deadline. Correct the calendar before retrying.', 1;
        END;
        INSERT dbo.workTask (workTaskId, rootTenantId, owningTenantId, queueCode, title, detail, personId, exposureCaseId, labResultId, dueDate, isUrgent)
        SELECT workTaskId, rootTenantId, owningTenantId, 'notificationDue',
               CONCAT (CASE WHEN evaluationState = 'needsReview' THEN N'Review potential notification: ' ELSE N'Notify ' END, recipientKind, N' — ', ruleName),
               CONCAT (N'Due ', CONVERT (VARCHAR (10), dueDate, 23), N' (', timeZoneId, N'). Required recipient: ', recipientKind, N'. Open the patient notification workspace to record completed delivery. Attempts do not complete this task.'),
               personId, exposureCaseId, labResultId, dueDate, 1 FROM #new;
        INSERT dbo.notificationObligation (notificationObligationId, rootTenantId, owningTenantId, personId, exposureCaseId,
            labResultId, notificationRuleId, workTaskId, policySnapshotJson, ruleName, recipientKind, triggerEvent, anchorDate, dueDate, timeZoneId, evaluationState)
        SELECT notificationObligationId, rootTenantId, owningTenantId, personId, exposureCaseId, labResultId, notificationRuleId,
               workTaskId, policySnapshotJson, ruleName, recipientKind, triggerEvent, anchorDate, dueDate, timeZoneId, evaluationState FROM #new;
        SELECT COUNT (*) AS createdCount FROM #new;

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
    , @ObjectName  = N'uspReconcileNotifications'
    , @Description = N'Idempotent receipt/threshold rule evaluation in batches. Every matching rule gets its own deadline; censored results require explicit applicability review.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReconcileNotifications TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReconcileNotifications TO meritWorkerRole;
END;
GO
