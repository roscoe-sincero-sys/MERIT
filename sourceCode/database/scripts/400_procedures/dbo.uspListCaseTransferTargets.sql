-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName:   dbo.uspListCaseTransferTargets
Author:       MERIT build
CreateDate:   2026-11-11
========================================================================================================================
Description:
The jurisdictions, programs and data root a case can be transferred to by the caller: same data root, active, not the
current owner, and where the caller holds case.edit with write access. Flags the patient's current residence
jurisdiction, the usual destination. Returns nothing when the caller cannot write the current owner.
========================================================================================================================
Requirements and Key Dependencies:
auth.tvfSessionWritePermission, dbo.udfPatientWorkOwner.
logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).
========================================================================================================================
Notes:
Requires permission case.edit (auth.udfHasPermission); refuses with 51403 otherwise.
Rule 8 error-only instrumentation (a procedure that only reads). Row-level security applies to every table read.
========================================================================================================================
Example Usage and Performance:
EXEC dbo.uspListCaseTransferTargets @exposureCaseId = @id;
========================================================================================================================
Modification History:
Date:		2026-11-11
Author:		MERIT build
Ticket:		#274
Description:	Initial version.
-----------------------------------------------------------------------------------------------------------------------
***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListCaseTransferTargets
      @exposureCaseId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListCaseTransferTargets]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;
    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'exposureCaseId=', @exposureCaseId);
    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';
    BEGIN TRY
        IF auth.udfHasPermission (N'case.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.edit).', 1;
        END;
        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================
        DECLARE @root UNIQUEIDENTIFIER, @from UNIQUEIDENTIFIER, @person UNIQUEIDENTIFIER;
        SELECT @root = rootTenantId, @from = owningTenantId, @person = personId
          FROM dbo.exposureCase WHERE exposureCaseId = @exposureCaseId AND isDeleted = 0 AND caseStatus <> 'closed';
        DECLARE @residence UNIQUEIDENTIFIER = dbo.udfPatientWorkOwner (@person);

        SELECT t.tenantId, t.tenantName, t.tenantType,
               CAST (CASE WHEN t.tenantId = @residence THEN 1 ELSE 0 END AS BIT) AS isResidence
          FROM auth.tenant AS t
         WHERE @from IS NOT NULL
           AND t.rootTenantId = @root AND t.isActive = 1 AND t.isDeleted = 0 AND t.tenantId <> @from
           AND (t.tenantType IN ('jurisdiction', 'program') OR t.tenantId = t.rootTenantId)
           AND EXISTS (SELECT 1 FROM auth.tvfSessionWritePermission ('case.edit') AS w WHERE w.tenantId = t.tenantId)
           AND EXISTS (SELECT 1 FROM auth.tvfSessionWritePermission ('case.edit') AS w WHERE w.tenantId = @from)
         ORDER BY CASE WHEN t.tenantId = @residence THEN 0 ELSE 1 END, t.tenantName;
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
    , @ObjectName  = N'uspListCaseTransferTargets'
    , @Description = N'Jurisdictions, programs and the data root the caller may transfer a case to (case.edit with write access at both ends), residence jurisdiction first (#274).';
GO
IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListCaseTransferTargets TO meritWebRole;
END;
GO
