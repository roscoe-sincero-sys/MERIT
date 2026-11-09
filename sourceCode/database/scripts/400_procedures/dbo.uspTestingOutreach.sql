-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspTestingOutreach
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Scoped clinical testing outreach using existing tasks, explicit contact ambiguity and jurisdiction-local date filters.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission task.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspTestingOutreach ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspTestingOutreach
      @dueFilter VARCHAR(12) = 'all'
      , @planKind VARCHAR(20) = NULL
      , @assignedToUserId UNIQUEIDENTIFIER = NULL
      , @taskStatus VARCHAR(20) = 'open'
      , @pageNumber INT = 1
      , @pageSize INT = 50
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspTestingOutreach]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = N'patient care metadata; no text or file content';

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'task.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (task.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF auth.udfHasPermission(N'person.view')=0
        BEGIN
            ;THROW 51403, N'Patient view permission is required.', 1;
        END;
        IF @dueFilter NOT IN ('all','overdue','today','next7') OR @taskStatus NOT IN ('open','done','dismissed')
         OR @pageNumber<1 OR @pageSize NOT BETWEEN 1 AND 500
         OR (@planKind IS NOT NULL AND @planKind NOT IN ('screen12','screen24','confirmation','early','later','review','discharge'))
        BEGIN
            ;THROW 51422, N'Choose a supported testing stage, status and due-date filter.', 1;
        END;
        DECLARE @today DATE=auth.udfSessionToday(),@contacts BIT=auth.udfHasPermission(N'person.edit');
        -- Global permission unions must not reveal contacts in a second tenant where this profile only has read access.
        DECLARE @contactTenants TABLE(tenantId UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT @contactTenants
        SELECT m.tenantId FROM auth.tvfUserGrantPermissions(auth.udfSessionUserId()) m
        JOIN auth.sessionScope ss ON ss.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
        WHERE m.permissionCode='person.edit' AND ss.isDeleted=0 AND ss.revokedUtc IS NULL AND ss.expiresUtc>SYSUTCDATETIME()
        AND ((ss.viewAsUserId IS NOT NULL AND EXISTS(SELECT 1 FROM auth.tvfUserGrantPermissions(ss.viewAsUserId) v WHERE v.tenantId=m.tenantId AND v.permissionCode=m.permissionCode)
         AND EXISTS(SELECT 1 FROM auth.tvfUserGrantPermissions(ss.appUserId) a WHERE a.tenantId=m.tenantId AND a.permissionCode='tenant.administer'))
         OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NOT NULL AND EXISTS
         (SELECT 1 FROM auth.tvfProfileGrantPermissions(ss.userProfileId) p WHERE p.tenantId=m.tenantId AND p.permissionCode=m.permissionCode))
         OR (ss.viewAsUserId IS NULL AND ss.userProfileId IS NULL));
        WITH plans AS (
         SELECT w.*,p.lastName,p.firstName,p.dob,COALESCE(JSON_VALUE(w.testingPlanJson,'$.kind'),'review') AS planKind
         FROM dbo.workTask w JOIN dbo.person p ON p.personId=w.personId AND p.isDeleted=0
         WHERE w.isDeleted=0 AND w.queueCode IN ('testingFollowUp','testingReview','testingDischarge') AND w.taskStatus=@taskStatus
         AND (@assignedToUserId IS NULL OR w.assignedToUserId=@assignedToUserId)
         AND (w.exposureCaseId IS NULL OR (auth.udfHasPermission(N'case.view')=1 AND EXISTS
          (SELECT 1 FROM dbo.exposureCase c WHERE c.exposureCaseId=w.exposureCaseId AND c.isDeleted=0)))
         AND (@dueFilter='all' OR (@taskStatus='open' AND
          ((@dueFilter='overdue' AND w.dueDate<@today) OR (@dueFilter='today' AND w.dueDate=@today)
           OR (@dueFilter='next7' AND w.dueDate BETWEEN @today AND DATEADD(DAY,7,@today)))))
        ), filtered AS (SELECT * FROM plans WHERE @planKind IS NULL OR planKind=@planKind)
        SELECT w.workTaskId,w.personId,CONCAT(w.lastName,N', ',w.firstName) AS patientName,w.exposureCaseId,
         w.planKind,w.taskStatus,w.dueDate,w.isUrgent,TRY_CONVERT(DATE,JSON_VALUE(w.testingPlanJson,'$.anchorDate')) AS anchorDate,
         u.displayName AS assignedToName,COUNT(*) OVER() AS totalCount,
         CASE WHEN @contacts=0 OR NOT EXISTS(SELECT 1 FROM @contactTenants ct WHERE ct.tenantId=w.owningTenantId) THEN N'Contact details unavailable in this profile'
              WHEN rel.primaryCount=1 AND rel.primaryVerified=1 AND contact.personId IS NULL THEN N'Caregiver details unavailable in this profile'
              WHEN rel.primaryCount=1 AND rel.primaryVerified=1 THEN N'Primary caregiver relationship verified; verify contact details'
              WHEN rel.contactCount>1 THEN N'Multiple caregivers: review before contacting'
              WHEN rel.contactCount=1 AND rel.allVerified=0 THEN N'Caregiver relationship needs verification'
              WHEN rel.contactCount=1 AND contact.personId IS NULL THEN N'Caregiver details unavailable in this profile'
              WHEN rel.contactCount=1 THEN N'Caregiver relationship verified; verify contact details'
              WHEN contact.personId=w.personId THEN N'Patient contact; verify details'
              ELSE N'No caregiver recorded: review required' END AS contactStatus,
         CONCAT(contact.firstName,N' ',contact.lastName) AS contactName,methods.phone,methods.email,addr.mailingAddress
        FROM filtered w
        LEFT JOIN auth.appUser u ON u.appUserId=w.assignedToUserId AND u.isDeleted=0
        OUTER APPLY(SELECT COUNT(DISTINCT r.relatedPersonId) AS contactCount,
         MIN(CONVERT(VARCHAR(36),r.relatedPersonId)) AS contactKey,
         COUNT(DISTINCT CASE WHEN r.isPrimaryContact=1 THEN r.relatedPersonId END) AS primaryCount,
         MIN(CASE WHEN r.isPrimaryContact=1 THEN CONVERT(VARCHAR(36),r.relatedPersonId) END) AS primaryKey,
         MIN(CASE WHEN r.isPrimaryContact=1 AND (r.verifiedDate IS NULL OR r.verifiedDate>@today) THEN 0 ELSE 1 END) AS primaryVerified,
         MIN(CASE WHEN r.verifiedDate IS NOT NULL AND r.verifiedDate<=@today THEN 1 ELSE 0 END) AS allVerified
         FROM dbo.personRelationship r
         WHERE r.personId=w.personId AND r.isDeleted=0 AND (r.startDate IS NULL OR r.startDate<=@today)
         AND (r.endDate IS NULL OR r.endDate>@today)
         AND (r.hasLegalCustody=1 OR r.isPrimaryContact=1 OR r.role IN ('parent','legalGuardian','fosterParent','caregiver'))) rel
        LEFT JOIN dbo.person contact ON contact.personId=CASE WHEN @contacts=1 AND EXISTS(SELECT 1 FROM @contactTenants ct WHERE ct.tenantId=w.owningTenantId) THEN
         CASE WHEN rel.primaryCount=1 AND rel.primaryVerified=1 THEN TRY_CONVERT(UNIQUEIDENTIFIER,rel.primaryKey)
         WHEN rel.contactCount=1 AND rel.allVerified=1 THEN TRY_CONVERT(UNIQUEIDENTIFIER,rel.contactKey)
         WHEN rel.contactCount=0 AND DATEADD(YEAR,18,w.dob)<=@today THEN w.personId END END AND contact.isDeleted=0
        OUTER APPLY(SELECT STRING_AGG(CASE WHEN m.contactType='phone' THEN CONVERT(NVARCHAR(MAX),m.contactValue) END,N'; ') AS phone,
         STRING_AGG(CASE WHEN m.contactType='email' THEN CONVERT(NVARCHAR(MAX),m.contactValue) END,N'; ') AS email
         FROM dbo.personContactMethod m WHERE m.personId=contact.personId AND m.isDeleted=0) methods
        OUTER APPLY(SELECT TOP(1) CONCAT_WS(N', ',NULLIF(l.addressLine1,N''),NULLIF(l.unit,N''),NULLIF(l.city,N''),
         NULLIF(l.state,N''),NULLIF(l.zip5,N''),NULLIF(l.postalCode,N''),NULLIF(l.country,N'')) AS mailingAddress
         FROM dbo.personLocation pl JOIN dbo.location l ON l.locationId=pl.locationId AND l.isDeleted=0
         WHERE pl.personId=contact.personId AND pl.isDeleted=0 AND pl.role='residence' AND pl.isPrimary=1
         AND pl.startDate<=@today AND (pl.endDate IS NULL OR pl.endDate>=@today)
         ORDER BY pl.startDate DESC,pl.personLocationId) addr
        ORDER BY w.isUrgent DESC,CASE WHEN w.dueDate IS NULL THEN 0 ELSE 1 END,w.dueDate,w.workTaskId
        OFFSET (CONVERT(BIGINT,@pageNumber)-1)*@pageSize ROWS FETCH NEXT @pageSize ROWS ONLY;

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = NULL
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
    , @ObjectName  = N'uspTestingOutreach'
    , @Description = N'Scoped clinical testing outreach using existing tasks, explicit contact ambiguity and jurisdiction-local date filters.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspTestingOutreach TO meritWebRole;
END;
GO
