-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspListContactCorrections
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Scoped correction history and owning-team review inbox; no clinical chart rights are added.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspListContactCorrections ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListContactCorrections
      @personId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListContactCorrections]')
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

        IF auth.udfHasPermission (N'person.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.edit).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        SELECT r.*,t.tenantName AS responsibleAgency,CONCAT(p.lastName,N', ',p.firstName) AS patientName,
         r.caregiverName,CAST(CASE WHEN EXISTS(SELECT 1 FROM dbo.person g
         JOIN auth.sessionScopeTenant st ON st.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
         AND st.isDeleted=0 AND st.canReadRelatedContacts=1 AND st.tenantId=g.owningTenantId
         CROSS APPLY auth.tvfPersonWritePredicate(g.rootTenantId,g.owningTenantId,g.personId,0) writable
         WHERE g.personId=r.caregiverId AND g.isDeleted=0) THEN 1 ELSE 0 END AS BIT) AS canReview
        FROM dbo.contactCorrection r LEFT JOIN dbo.person p ON p.personId=r.personId
        LEFT JOIN dbo.person g ON g.personId=r.caregiverId JOIN auth.tenant t ON t.tenantId=COALESCE(g.owningTenantId,r.responsibleTenantId)
        WHERE r.isDeleted=0 AND (@personId IS NULL OR r.personId=@personId) AND (EXISTS(SELECT 1 FROM dbo.person p
         JOIN auth.sessionScopeTenant st ON st.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
         AND st.isDeleted=0 AND st.canReadRelatedContacts=1
         CROSS APPLY auth.tvfPersonPredicate(p.rootTenantId,p.owningTenantId,p.personId) clinical
         WHERE p.personId=r.personId AND p.isDeleted=0 AND (p.owningTenantId=st.tenantId OR EXISTS
         (SELECT 1 FROM dbo.personAccess pa WHERE pa.personId=p.personId AND pa.tenantId=st.tenantId AND pa.isDeleted=0))) OR EXISTS(SELECT 1 FROM dbo.person g
         JOIN auth.sessionScopeTenant st ON st.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
         AND st.isDeleted=0 AND st.canReadRelatedContacts=1 AND st.tenantId=g.owningTenantId
         CROSS APPLY auth.tvfPersonWritePredicate(g.rootTenantId,g.owningTenantId,g.personId,0) writable
         WHERE g.personId=r.caregiverId AND g.isDeleted=0))
        ORDER BY CASE WHEN r.requestStatus='pending' THEN 0 ELSE 1 END,r.createdDateUtc DESC;

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
    , @ObjectName  = N'uspListContactCorrections'
    , @Description = N'Scoped correction history and owning-team review inbox; no clinical chart rights are added.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListContactCorrections TO meritWebRole;
END;
GO
