-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspResolveWorkWithEvidence
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

uspResolveWorkWithEvidence

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission task.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspResolveWorkWithEvidence ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspResolveWorkWithEvidence
      @workTaskId UNIQUEIDENTIFIER
      , @personActivityId UNIQUEIDENTIFIER
      , @action VARCHAR(12)
      , @activityDate DATE
      , @note NVARCHAR(1000)
      , @outcome VARCHAR(30) = 'completed'
      , @audience VARCHAR(12) = 'internal'
      , @channel VARCHAR(12) = 'internal'
      , @contactPersonId UNIQUEIDENTIFIER = NULL
      , @recipientLabel NVARCHAR(200) = NULL
      , @evidenceJson NVARCHAR(MAX) = N'{}'
      , @transferCaseId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspResolveWorkWithEvidence]')
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

        IF auth.udfHasPermission (N'task.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (task.edit).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @personId UNIQUEIDENTIFIER,@exposureCaseId UNIQUEIDENTIFIER,@root UNIQUEIDENTIFIER,@owner UNIQUEIDENTIFIER,
         @snapshot NVARCHAR(MAX),@title NVARCHAR(200),@queue VARCHAR(40),@definition UNIQUEIDENTIFIER,@sourceResult UNIQUEIDENTIFIER;
        SELECT @personId=COALESCE(w.personId,c.personId),@exposureCaseId=w.exposureCaseId,@root=w.rootTenantId,@owner=w.owningTenantId,
         @snapshot=w.workflowSnapshotJson,@title=w.title,@queue=w.queueCode,@definition=w.workflowDefinitionId,@sourceResult=w.labResultId
         FROM dbo.workTask AS w WITH(UPDLOCK,HOLDLOCK) LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId=w.exposureCaseId AND c.isDeleted=0
         WHERE w.workTaskId=@workTaskId AND w.taskStatus='open' AND w.isDeleted=0
         AND (w.exposureCaseId IS NULL OR (c.exposureCaseId IS NOT NULL AND (w.personId IS NULL OR w.personId=c.personId)));
        IF @personId IS NULL
        BEGIN
            ;THROW 51404, N'The open patient task is not available. Work without a patient is handled in the queue.', 1;
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
        IF @queue IN ('programTask','workflowReview') AND USER_NAME()<>N'dbo' AND
         (NOT EXISTS(SELECT 1 FROM auth.tvfSessionWritePermission('person.edit') WHERE tenantId=@owner)
         OR NOT EXISTS(SELECT 1 FROM auth.tvfSessionWritePermission('task.edit') WHERE tenantId=@owner)
         OR (@exposureCaseId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM auth.tvfSessionWritePermission('case.edit') WHERE tenantId=@owner))
         OR (@action='waive' AND NOT EXISTS(SELECT 1 FROM auth.tvfSessionWritePermission('workflow.waive') WHERE tenantId=@owner)))
        BEGIN
         ;THROW 51403,N'This profile cannot change program work in the responsible agency.',1;
        END;
        IF @queue IN ('notificationDue','receiptFinding','missingInformation','labReportingReview','testingFollowUp','testingDischarge')
        BEGIN
            ;THROW 51422, N'This task has a dedicated notification, receipt, testing or closure-letter evidence workflow. Use its original workspace.', 1;
        END;
        IF @action NOT IN ('complete','attempt','apply','exclude','waive','transfer') OR NULLIF(TRIM(@note),N'') IS NULL
         OR @activityDate>auth.udfSessionToday() OR @activityDate<'19000101'
        BEGIN
            ;THROW 51422, N'Choose an action and record the actual event date and source evidence.', 1;
        END;
        IF EXISTS(SELECT 1 FROM dbo.personActivity WHERE personActivityId=@personActivityId) OR EXISTS(SELECT 1 FROM dbo.caseActivity WHERE caseActivityId=@personActivityId)
        BEGIN
            ;THROW 51409, N'This event was already recorded. Reload the task history before resubmitting.', 1;
        END;
        DECLARE @kind VARCHAR(12)=COALESCE(JSON_VALUE(@snapshot,'$.completionKind'),
         CASE WHEN @queue IN ('letterDue','prenatalNotification') THEN 'delivery'
         WHEN @queue IN ('confirmatoryTest','newbornTesting','actionLevel','crossLink','investigationRequest','assessmentContact') THEN 'contact' ELSE 'internal' END);
        IF JSON_VALUE(@snapshot,'$.evaluationState')='needsReview' AND @action NOT IN ('apply','exclude','transfer')
        BEGIN
            ;THROW 51422, N'Record an explicit applicability decision before completing this conditional task.', 1;
        END;
        IF @action IN ('apply','exclude') AND COALESCE(JSON_VALUE(@snapshot,'$.evaluationState'),'eligible')<>'needsReview'
        BEGIN
            ;THROW 51422, N'This task is not awaiting an applicability decision.', 1;
        END;
        IF @action='waive' AND (auth.udfHasPermission(N'workflow.waive')=0 OR COALESCE(JSON_VALUE(@snapshot,'$.allowWaiver'),'false')<>'true')
        BEGIN
            ;THROW 51403, N'This policy does not allow a waiver, or the active profile lacks workflow waiver permission.', 1;
        END;
        IF @action='complete'
        BEGIN
            IF @kind='internal' AND (@audience<>'internal' OR @outcome<>'completed' OR @channel<>'internal')
             OR @kind='contact' AND (@audience='internal' OR @channel='internal' OR @outcome<>'reached')
             OR @kind='delivery' AND (@audience='internal' OR @channel='internal' OR @outcome<>'completed')
            BEGIN
                ;THROW 51422, N'Use the required completion outcome, recipient and channel. Unsuccessful contact is an attempt and leaves the task open.', 1;
            END;
            IF ISJSON(@evidenceJson)<>1 OR LEFT(TRIM(@evidenceJson),1)<>'{' OR LEN(@evidenceJson)>1200
            BEGIN
                ;THROW 51422, N'Provide a bounded object containing the required evidence fields.', 1;
            END;
            DECLARE @fields NVARCHAR(MAX)=COALESCE(JSON_VALUE(@snapshot,'$.evidenceFieldsJson'),N'[]');
            IF EXISTS(SELECT 1 FROM OPENJSON(@fields) WITH([key] NVARCHAR(30),type VARCHAR(20),required BIT) AS f
             OUTER APPLY(SELECT [value] FROM OPENJSON(@evidenceJson) WHERE [key]=f.[key]) AS e
             WHERE (f.required=1 AND NULLIF(TRIM(e.[value]),N'') IS NULL) OR (NULLIF(TRIM(e.[value]),N'') IS NOT NULL AND
             ((f.type='date' AND (LEN(e.[value])<>10 OR TRY_CONVERT(DATE,e.[value],23) IS NULL)) OR (f.type='number' AND TRY_CONVERT(DECIMAL(18,6),e.[value]) IS NULL)
             OR (f.type='yesNo' AND e.[value] NOT IN ('yes','no')))))
             OR EXISTS(SELECT 1 FROM OPENJSON(@evidenceJson) AS e WHERE e.type NOT IN (0,1) OR NOT EXISTS(SELECT 1 FROM OPENJSON(@fields) WITH([key] NVARCHAR(30)) WHERE [key]=e.[key]))
            BEGIN
                ;THROW 51422, N'Complete each required evidence field using the configured text, date, number or yes/no format.', 1;
            END;
            IF COALESCE(TRY_CONVERT(INT,JSON_VALUE(@snapshot,'$.minimumContactAttempts')),0)>
             (SELECT COUNT(*) FROM dbo.vwPatientActivityStream WHERE exposureCaseId=@exposureCaseId AND activityType='contactAttempt' AND isDeleted=0 AND isVoided=0
              AND outcome IN ('noAnswer','leftMessage','wrongNumber') AND (contactPersonId IS NOT NULL OR NULLIF(TRIM(recipientLabel),N'') IS NOT NULL))
            BEGIN
                ;THROW 51422, N'The required number of evidenced unsuccessful contact attempts has not been recorded.', 1;
            END;
        END;
        IF @action='attempt' AND (@audience='internal' OR @channel='internal' OR @outcome NOT IN ('reached','noAnswer','leftMessage','wrongNumber'))
        BEGIN
            ;THROW 51422, N'Record the external recipient, channel and actual contact outcome. An attempt does not complete required delivery.', 1;
        END;
        IF @action='transfer' AND (@transferCaseId IS NULL OR @transferCaseId=@exposureCaseId OR NOT EXISTS
         (SELECT 1 FROM dbo.exposureCase AS c WHERE c.exposureCaseId=@transferCaseId AND c.personId=@personId AND c.caseStatus<>'closed' AND c.isDeleted=0
         AND EXISTS(SELECT 1 FROM auth.tvfTenantWritePredicate(c.rootTenantId,c.owningTenantId,c.isDeleted))
         AND (@queue NOT IN ('programTask','workflowReview') OR USER_NAME()=N'dbo' OR
         (EXISTS(SELECT 1 FROM auth.tvfSessionWritePermission('task.edit') WHERE tenantId=c.owningTenantId)
         AND EXISTS(SELECT 1 FROM auth.tvfSessionWritePermission('case.edit') WHERE tenantId=c.owningTenantId)))))
        BEGIN
            ;THROW 51422, N'Choose a different active case for this patient within your write scope. The receiving task will remain open.', 1;
        END;
        DECLARE @activityType VARCHAR(30)=CASE WHEN @action='attempt' OR (@action='complete' AND @kind='contact') THEN 'contactAttempt'
         WHEN @action='complete' AND @kind='delivery' THEN 'letter' ELSE 'note' END;
        IF @action NOT IN ('complete','attempt')
        BEGIN
            SET @audience='internal'; SET @channel='internal'; SET @outcome='completed'; SET @contactPersonId=NULL; SET @recipientLabel=NULL;
        END;
        DECLARE @fullNote NVARCHAR(MAX)=CONCAT(N'Task: ',@title,N'. Action: ',@action,N'. ',@note,CASE WHEN @action='complete' AND @evidenceJson<>N'{}' THEN N' Evidence: '+@evidenceJson ELSE N'' END);
        IF LEN(@fullNote)>2000
        BEGIN
            ;THROW 51422, N'Shorten the note/evidence so the complete activity remains within 2,000 characters.', 1;
        END;
        DECLARE @activityNote NVARCHAR(2000)=@fullNote;
        EXEC dbo.uspRecordPatientActivity @personActivityId=@personActivityId,@personId=@personId,@activityType=@activityType,@activityDate=@activityDate,
         @audience=@audience,@channel=@channel,@outcome=@outcome,@contactPersonId=@contactPersonId,@recipientLabel=@recipientLabel,@note=@activityNote,@exposureCaseId=@exposureCaseId,@returnCount=0;
        IF @action='apply'
            UPDATE dbo.workTask SET queueCode='programTask',workflowSnapshotJson=JSON_MODIFY(workflowSnapshotJson,'$.evaluationState','eligible'),resolutionNote=@note WHERE workTaskId=@workTaskId;
        IF @action IN ('complete','waive','exclude','transfer')
            UPDATE dbo.workTask SET taskStatus=CASE WHEN @action='complete' THEN 'done' ELSE 'dismissed' END,completedUtc=SYSUTCDATETIME(),completedByUserId=auth.udfSessionUserId(),
             resolutionKind=CASE @action WHEN 'complete' THEN 'completed' WHEN 'waive' THEN 'waived' WHEN 'exclude' THEN 'notApplicable' ELSE 'transferred' END,
             completionActivityId=@personActivityId,resolutionNote=@note WHERE workTaskId=@workTaskId;
        IF @action='transfer'
        BEGIN
            DECLARE @next UNIQUEIDENTIFIER=NEWID();
            INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,labResultId,queueCode,title,detail,dueDate,isUrgent,
             workflowDefinitionId,workflowSnapshotJson,workflowOccurrenceKey)
            SELECT @next,w.rootTenantId,c.owningTenantId,@personId,@transferCaseId,w.labResultId,w.queueCode,w.title,w.detail,w.dueDate,w.isUrgent,
             w.workflowDefinitionId,w.workflowSnapshotJson,CONCAT(N'transfer/',@workTaskId,N'/',@next)
            FROM dbo.workTask AS w JOIN dbo.exposureCase AS c ON c.exposureCaseId=@transferCaseId WHERE w.workTaskId=@workTaskId;
            UPDATE dbo.workTask SET resolutionNote=CONCAT(@note,N' Transferred to task ',@next,N' on case ',@transferCaseId,N'.') WHERE workTaskId=@workTaskId;
        END;
        DECLARE @repeat INT=TRY_CONVERT(INT,JSON_VALUE(@snapshot,'$.recurrenceDays'));
        IF @action='complete' AND @repeat>0 AND EXISTS(SELECT 1 FROM config.workflowDefinition WHERE workflowDefinitionId=@definition AND definitionStatus='published' AND isDeleted=0)
        BEGIN
            DECLARE @frozenHolidays NVARCHAR(MAX)=(SELECT holidayDatesJson FROM OPENJSON(@snapshot) WITH(holidayDatesJson NVARCHAR(MAX)));
            DECLARE @nextDue DATE=config.udfNotificationDueDate(@activityDate,@repeat,JSON_VALUE(@snapshot,'$.dayBasis'),@frozenHolidays);
            IF @nextDue<=COALESCE(TRY_CONVERT(DATE,JSON_VALUE(@snapshot,'$.effectiveTo')),'99991231')
                INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,personId,exposureCaseId,queueCode,title,detail,dueDate,workflowDefinitionId,workflowSnapshotJson,workflowOccurrenceKey)
                VALUES(NEWID(),@root,@owner,@personId,@exposureCaseId,'programTask',@title,N'Recurring task from actual prior completion; original definition retained.',@nextDue,@definition,@snapshot,CONCAT(N'repeat/',@workTaskId,N'/',@personActivityId));
        END;
        IF @queue='assessmentOwnerClosure' EXEC dbo.uspRefreshPersonAccess @personId=@personId;

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
    , @ObjectName  = N'uspResolveWorkWithEvidence'
    , @Description = N'uspResolveWorkWithEvidence';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspResolveWorkWithEvidence TO meritWebRole;
END;
GO
