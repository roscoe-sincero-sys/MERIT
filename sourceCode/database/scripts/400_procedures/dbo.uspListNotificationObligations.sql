-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspListNotificationObligations
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Scoped notification work and evidence, including source-result changes and immutable policy snapshots.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspListNotificationObligations ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspListNotificationObligations
      @personId UNIQUEIDENTIFIER = NULL
      , @exposureCaseId UNIQUEIDENTIFIER = NULL
      , @pageNumber INT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspListNotificationObligations]')
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

        IF auth.udfHasPermission (N'person.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF @pageNumber NOT BETWEEN 1 AND 100000
        BEGIN
            ;THROW 51422, N'Choose a valid notification page.', 1;
        END;
        SELECT a.*, p.lastName, p.firstName, c.caseNumber, r.collectedDate, r.receivedUtc, r.resultRowStatus,
               r.valueCanonical, r.operator, s.canonicalUnit, an.displayName AS analyteName, s.specimen,
               pa.activityDate AS evidenceDate, pa.channel AS evidenceChannel,
               CASE WHEN pa.audience='owner' THEN pa.recipientLabel ELSE CONCAT (cp.lastName, N', ', cp.firstName) END AS recipientName,
               CAST (SYSUTCDATETIME () AT TIME ZONE 'UTC' AT TIME ZONE a.timeZoneId AS DATE) AS policyToday,
               COUNT (*) OVER () AS totalCount
          FROM dbo.notificationObligation AS a JOIN dbo.person AS p ON p.personId = a.personId
          JOIN dbo.labResult AS r ON r.labResultId = a.labResultId
          JOIN config.analyteSpecimen AS s ON s.analyteSpecimenId = r.analyteSpecimenId
          JOIN config.analyte AS an ON an.analyteId = s.analyteId
          LEFT JOIN dbo.exposureCase AS c ON c.exposureCaseId = a.exposureCaseId
          LEFT JOIN dbo.personActivity AS pa ON pa.personActivityId = a.completedActivityId AND pa.isVoided = 0
          LEFT JOIN dbo.person AS cp ON cp.personId = pa.contactPersonId
         WHERE a.isDeleted = 0 AND p.isDeleted = 0 AND (@personId IS NULL OR a.personId = @personId)
           AND (@exposureCaseId IS NULL OR a.exposureCaseId = @exposureCaseId)
           AND (a.exposureCaseId IS NULL OR (auth.udfHasPermission (N'case.view') = 1 AND EXISTS
            (SELECT 1 FROM dbo.exposureCase AS c WHERE c.exposureCaseId = a.exposureCaseId AND c.personId = a.personId AND c.isDeleted = 0)))
         ORDER BY CASE WHEN a.completedDate IS NULL AND a.evaluationState <> 'notApplicable' THEN 0 ELSE 1 END,
                  a.dueDate, a.notificationObligationId OFFSET (@pageNumber - 1) * 50 ROWS FETCH NEXT 50 ROWS ONLY;

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
    , @ObjectName  = N'uspListNotificationObligations'
    , @Description = N'Scoped notification work and evidence, including source-result changes and immutable policy snapshots.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspListNotificationObligations TO meritWebRole;
END;
GO
