-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspImportReporters
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Adds or updates registry entries from checked rows (JSON): laboratories and facilities are matched on CLIA number, then NPI; providers on NPI. Blank fields never erase what is stored. Returns how many were added and updated.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission registry.edit (auth.udfHasPermission); refuses with 51403 otherwise.

The application has already checked identifier formats (NPI Luhn check digit, CLIA pattern) and removed duplicate rows; this procedure trusts the shape and decides only add versus update.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspImportReporters ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T163
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspImportReporters
      @rowsJson NVARCHAR (MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspImportReporters]')
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
    SET @KeyParameters = CONCAT (N'rows=', (SELECT COUNT (*) FROM OPENJSON (@rowsJson)));

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'registry.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (registry.edit).', 1;
        END;

        IF ISJSON (@rowsJson) = 0
        BEGIN
            ;THROW 50000, N'@rowsJson must be a JSON array of registry rows.', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @rows TABLE (rowNumber INT PRIMARY KEY, reporterKind VARCHAR (20), reporterName NVARCHAR (120),
                             nameKey NVARCHAR (120), lastName NVARCHAR (60), firstName NVARCHAR (60), credential NVARCHAR (20),
                             npi VARCHAR (10), cliaNumber VARCHAR (10), phone NVARCHAR (30), existingId UNIQUEIDENTIFIER,
                             newId UNIQUEIDENTIFIER);

        INSERT @rows (rowNumber, reporterKind, reporterName, nameKey, lastName, firstName, credential, npi, cliaNumber, phone)
        SELECT j.rowNumber, j.reporterKind, j.reporterName, j.nameKey, j.lastName, NULLIF (j.firstName, N''),
               NULLIF (j.credential, N''), NULLIF (j.npi, ''), NULLIF (j.cliaNumber, ''), NULLIF (j.phone, N'')
          FROM OPENJSON (@rowsJson)
          WITH (rowNumber INT '$.rowNumber', reporterKind VARCHAR (20) '$.kind', reporterName NVARCHAR (120) '$.name',
                nameKey NVARCHAR (120) '$.nameKey', lastName NVARCHAR (60) '$.lastName', firstName NVARCHAR (60) '$.firstName',
                credential NVARCHAR (20) '$.credential', npi VARCHAR (10) '$.npi', cliaNumber VARCHAR (10) '$.clia',
                phone NVARCHAR (30) '$.phone') AS j
         WHERE j.reporterKind IN ('laboratory', 'facility', 'provider');

        -- ------------------------------------------------------------------------------------------ organizations
        -- CLIA first, then NPI for the rows still unmatched: each is a seek on its own index, where one ORed condition
        -- scanned the organizations for every row.
        UPDATE r SET existingId = m.organizationId
          FROM @rows AS r
         CROSS APPLY (SELECT TOP (1) o.organizationId FROM dbo.organization AS o
                       WHERE o.isDeleted = 0 AND o.cliaNumber IS NOT NULL AND o.cliaNumber = r.cliaNumber
                       ORDER BY o.createdDateUtc, o.organizationId) AS m
         WHERE r.reporterKind IN ('laboratory', 'facility') AND r.cliaNumber IS NOT NULL;

        UPDATE r SET existingId = m.organizationId
          FROM @rows AS r
         CROSS APPLY (SELECT TOP (1) o.organizationId FROM dbo.organization AS o
                       WHERE o.isDeleted = 0 AND o.npi IS NOT NULL AND o.npi = r.npi
                       ORDER BY o.createdDateUtc, o.organizationId) AS m
         WHERE r.reporterKind IN ('laboratory', 'facility') AND r.existingId IS NULL AND r.npi IS NOT NULL;

        UPDATE o
           SET organizationName = r.reporterName, organizationNameKey = r.nameKey, npi = COALESCE (r.npi, o.npi),
               cliaNumber = COALESCE (r.cliaNumber, o.cliaNumber), phone = COALESCE (r.phone, o.phone), isActive = 1
          FROM dbo.organization AS o
          JOIN @rows AS r ON r.existingId = o.organizationId;

        UPDATE @rows SET newId = CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER) WHERE existingId IS NULL;

        INSERT dbo.organization (organizationId, rootTenantId, owningTenantId, organizationName, organizationNameKey, npi,
                                 cliaNumber, phone, isActive)
        SELECT r.newId, @root, @root, r.reporterName, r.nameKey, r.npi, r.cliaNumber, r.phone, 1
          FROM @rows AS r
         WHERE r.existingId IS NULL AND r.reporterKind IN ('laboratory', 'facility');

        INSERT dbo.organizationType (organizationTypeId, rootTenantId, owningTenantId, organizationId, organizationTypeCode)
        SELECT CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, @root, COALESCE (r.existingId, r.newId),
               CASE r.reporterKind WHEN 'laboratory' THEN 'laboratory' ELSE 'providerPractice' END
          FROM @rows AS r
         WHERE r.reporterKind IN ('laboratory', 'facility')
           AND NOT EXISTS (SELECT 1 FROM dbo.organizationType AS t
                            WHERE t.organizationId = COALESCE (r.existingId, r.newId) AND t.isDeleted = 0
                              AND t.organizationTypeCode = CASE r.reporterKind WHEN 'laboratory' THEN 'laboratory'
                                                                               ELSE 'providerPractice' END);

        -- ------------------------------------------------------------------------------------------ providers
        UPDATE r SET existingId = pv.providerId
          FROM @rows AS r
          JOIN dbo.provider AS pv ON pv.npi = r.npi AND pv.isDeleted = 0
         WHERE r.reporterKind = 'provider';

        UPDATE pv
           SET lastName = r.lastName, firstName = COALESCE (r.firstName, pv.firstName),
               credential = COALESCE (r.credential, pv.credential)
          FROM dbo.provider AS pv
          JOIN @rows AS r ON r.existingId = pv.providerId AND r.reporterKind = 'provider';

        INSERT dbo.provider (providerId, rootTenantId, owningTenantId, npi, lastName, firstName, credential)
        SELECT r.newId, @root, @root, r.npi, r.lastName, r.firstName, r.credential
          FROM @rows AS r
         WHERE r.existingId IS NULL AND r.reporterKind = 'provider';

        DECLARE @added INT = (SELECT COUNT (*) FROM @rows WHERE existingId IS NULL);
        DECLARE @updated INT = (SELECT COUNT (*) FROM @rows WHERE existingId IS NOT NULL);
        SELECT @added AS addedCount, @updated AS updatedCount;

        SET @Comments = CONCAT (N'Registry import: ', @added, N' added, ', @updated, N' updated.');

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
    , @ObjectName  = N'uspImportReporters'
    , @Description = N'Adds or updates registry entries from checked rows (JSON): laboratories and facilities are matched on CLIA number, then NPI; providers on NPI. Blank fields never erase what is stored. Returns how many were added and updated.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspImportReporters TO meritWebRole;
END;
GO
