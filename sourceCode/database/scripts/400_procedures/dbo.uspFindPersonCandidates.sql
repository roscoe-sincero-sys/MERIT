-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspFindPersonCandidates
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Candidate persons for matching an incoming record: same last name (or alias) with the same date of birth or with day and month swapped, or same first name and date of birth. Scoring and the link decision are made in the application under the configured matching rules. A person a reviewer has already called a different person for this incoming identity (@incomingKey) is never returned.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission result.view (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspFindPersonCandidates ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T121
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspFindPersonCandidates
      @lastNameKey  NVARCHAR (60)
      , @firstNameKey NVARCHAR (60) = NULL
      , @dob          DATE = NULL
      , @maxRows      INT = 5
      , @incomingKey  NVARCHAR (200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspFindPersonCandidates]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'maxRows=', @maxRows, N', hasDob=', IIF (@dob IS NULL, 0, 1));

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        IF auth.udfHasPermission (N'result.view') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (result.view).', 1;
        END;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @swapped DATE = CASE WHEN @dob IS NOT NULL AND DAY (@dob) <= 12
                                     THEN DATEFROMPARTS (YEAR (@dob), DAY (@dob), MONTH (@dob)) END;

        SELECT TOP (@maxRows) p.personId, p.lastName, p.firstName, p.lastNameKey, p.firstNameKey, p.dob, p.sex,
               p.isPlaceholderName, x.via
          FROM (SELECT personId, N'name' AS via FROM dbo.person
                 WHERE isDeleted = 0 AND mergedIntoPersonId IS NULL AND lastNameKey = @lastNameKey
                   AND (@dob IS NULL OR dob IN (@dob, @swapped) OR dob IS NULL)
                UNION
                SELECT a.personId, N'alias' FROM dbo.personAlias AS a
                  JOIN dbo.person AS p ON p.personId = a.personId AND p.isDeleted = 0 AND p.mergedIntoPersonId IS NULL
                 WHERE a.isDeleted = 0 AND a.lastNameKey = @lastNameKey AND (@dob IS NULL OR p.dob IN (@dob, @swapped))
                UNION
                SELECT personId, N'firstNameDob' FROM dbo.person
                 WHERE isDeleted = 0 AND mergedIntoPersonId IS NULL AND @firstNameKey IS NOT NULL AND firstNameKey = @firstNameKey
                   AND @dob IS NOT NULL AND dob = @dob) AS x
          JOIN dbo.person AS p ON p.personId = x.personId
         WHERE @incomingKey IS NULL
            OR NOT EXISTS (SELECT 1 FROM dbo.matchExclusion AS e
                            WHERE e.incomingKey = @incomingKey AND e.personId = p.personId AND e.isDeleted = 0)
         ORDER BY CASE WHEN p.dob = @dob THEN 0 ELSE 1 END, p.lastName;

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
    , @ObjectName  = N'uspFindPersonCandidates'
    , @Description = N'Candidate persons for matching an incoming record: same last name (or alias) with the same date of birth or with day and month swapped, or same first name and date of birth. Scoring and the link decision are made in the application under the configured matching rules. A person a reviewer has already called a different person for this incoming identity (@incomingKey) is never returned.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspFindPersonCandidates TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspFindPersonCandidates TO meritWorkerRole;
END;
GO
