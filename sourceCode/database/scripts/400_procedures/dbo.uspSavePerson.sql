-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSavePerson
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Creates or updates a person's identity. A new person is owned by the root tenant; when the caller acts for a local tenant, that tenant is given access so the creator can see the record. Keeps the match-key row in step for outside-scope checks.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Returns one row: personId, rowVersion, isNew, dobChanged, deceasedChanged. The application reruns classification when the DOB changed and creates the deceased review task when a death date was added.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspSavePerson ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T041
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSavePerson
      @personId          UNIQUEIDENTIFIER
      , @lastName          NVARCHAR (60)
      , @firstName         NVARCHAR (60) = NULL
      , @middleName        NVARCHAR (60) = NULL
      , @lastNameKey       NVARCHAR (60)
      , @firstNameKey      NVARCHAR (60) = NULL
      , @isPlaceholderName BIT = 0
      , @dob               DATE = NULL
      , @dobVerified       BIT = 0
      , @sex               VARCHAR (10) = 'unknown'
      , @ethnicity         VARCHAR (20) = 'unknown'
      , @preferredLanguage VARCHAR (20) = NULL
      , @deceasedDate      DATE = NULL
      , @rowVersion        BINARY (8) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspSavePerson]')
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
    SET @KeyParameters = CONCAT (N'personId=', @personId);

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

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
        DECLARE @acting UNIQUEIDENTIFIER = auth.udfSessionActingTenantId ();
        DECLARE @isNew BIT = 0, @dobChanged BIT = 0, @deceasedChanged BIT = 0;

        IF @dob IS NOT NULL AND @dob > auth.udfSessionToday ()
        BEGIN
            ;THROW 51422, N'Date of birth cannot be in the future.', 1;
        END;

        IF @personId IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId)
        BEGIN
            IF EXISTS (SELECT 1 FROM dbo.person WHERE personId = @personId AND mergedIntoPersonId IS NOT NULL)
            BEGIN
                ;THROW 51422, N'This record was merged into another and is read-only.', 1;
            END;

            SELECT @dobChanged      = IIF (dob IS DISTINCT FROM @dob, 1, 0),
                   @deceasedChanged = IIF (deceasedDate IS NULL AND @deceasedDate IS NOT NULL, 1, 0)
              FROM dbo.person WHERE personId = @personId;

            UPDATE dbo.person
               SET lastName = @lastName, firstName = @firstName, middleName = @middleName, lastNameKey = @lastNameKey,
                   firstNameKey = @firstNameKey, isPlaceholderName = @isPlaceholderName, dob = @dob, dobVerified = @dobVerified,
                   sex = @sex, ethnicity = @ethnicity, preferredLanguage = @preferredLanguage, deceasedDate = @deceasedDate
             WHERE personId = @personId AND isDeleted = 0 AND rowVersion = @rowVersion;
            IF @@ROWCOUNT = 0
            BEGIN
                ;THROW 51409, N'This record was changed by someone else, or is no longer available. Reload it and try again.', 1;
            END;
        END
        ELSE
        BEGIN
            SET @isNew = 1;
            SET @deceasedChanged = IIF (@deceasedDate IS NULL, 0, 1);

            INSERT dbo.person (personId, rootTenantId, owningTenantId, lastName, firstName, middleName, lastNameKey, firstNameKey,
                               isPlaceholderName, dob, dobVerified, sex, ethnicity, preferredLanguage, deceasedDate)
            VALUES (@personId, @root, @root, @lastName, @firstName, @middleName, @lastNameKey, @firstNameKey,
                    @isPlaceholderName, @dob, @dobVerified, @sex, @ethnicity, @preferredLanguage, @deceasedDate);

            IF @acting IS NOT NULL AND @acting <> @root
            BEGIN
                INSERT dbo.personAccess (personAccessId, rootTenantId, personId, tenantId, accessReason)
                VALUES (CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, @personId, @acting, 'share');
            END;
        END;

        MERGE dbo.personMatchKey AS t
        USING (SELECT @personId AS personId) AS s ON t.personId = s.personId AND t.isDeleted = 0
        WHEN MATCHED AND (t.lastNameKey <> @lastNameKey OR t.firstNameKey IS DISTINCT FROM @firstNameKey
                          OR t.dob IS DISTINCT FROM @dob) THEN
            UPDATE SET lastNameKey = @lastNameKey, firstNameKey = @firstNameKey, dob = @dob
        WHEN NOT MATCHED THEN
            INSERT (personMatchKeyId, rootTenantId, personId, lastNameKey, firstNameKey, dob)
            VALUES (CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, @personId, @lastNameKey, @firstNameKey, @dob);

        SELECT personId, rowVersion, @isNew AS isNew, @dobChanged AS dobChanged, @deceasedChanged AS deceasedChanged
          FROM dbo.person WHERE personId = @personId;

        SET @Comments = IIF (@isNew = 1, N'Person created.', N'Person updated.');

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
    , @ObjectName  = N'uspSavePerson'
    , @Description = N'Creates or updates a person''s identity. A new person is owned by the root tenant; when the caller acts for a local tenant, that tenant is given access so the creator can see the record. Keeps the match-key row in step for outside-scope checks.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSavePerson TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSavePerson TO meritWorkerRole;
END;
GO
