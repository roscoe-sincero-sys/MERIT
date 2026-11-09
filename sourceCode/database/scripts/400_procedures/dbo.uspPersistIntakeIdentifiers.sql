-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspPersistIntakeIdentifiers
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Persists only identifiers parsed from an authorized HL7 staging row for its matched patient. Does not grant identifier display rights.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission intake.upload (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspPersistIntakeIdentifiers ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		255
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspPersistIntakeIdentifiers
      @stagingRowId UNIQUEIDENTIFIER
      , @personId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspPersistIntakeIdentifiers]')
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
    SET @KeyParameters = CONCAT(N'stagingRowId=',@stagingRowId,N', personId=',@personId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'intake.upload') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (intake.upload).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        IF auth.udfHasPermission(N'person.edit')=0
        BEGIN
            ;THROW 51403,N'Patient editing permission is required for intake.',1;
        END;
        DECLARE @json NVARCHAR(MAX),@corrections NVARCHAR(MAX),@root UNIQUEIDENTIFIER,@owner UNIQUEIDENTIFIER;
        SELECT @json=s.parsedJson,@corrections=s.correctedJson,@root=s.rootTenantId FROM dbo.stagingRow s JOIN dbo.intakeBatch b ON b.intakeBatchId=s.intakeBatchId
         JOIN dbo.intakeProfile f ON f.intakeProfileId=b.intakeProfileId
         WHERE s.stagingRowId=@stagingRowId AND s.isDeleted=0 AND f.fileType='hl7' AND b.isDeleted=0;
        SELECT @owner=owningTenantId FROM dbo.person WHERE personId=@personId AND rootTenantId=@root AND isDeleted=0
         AND (personId=TRY_CONVERT(UNIQUEIDENTIFIER,JSON_VALUE(@corrections,'$.matchPersonId'))
         OR (dob=TRY_CONVERT(DATE,COALESCE(JSON_VALUE(@corrections,'$.dob'),JSON_VALUE(@json,'$.dob'))) AND EXISTS(SELECT 1 FROM dbo.personAlias a WHERE a.personId=@personId AND a.isDeleted=0
          AND (a.lastName=COALESCE(JSON_VALUE(@corrections,'$.lastName'),JSON_VALUE(@json,'$.lastName'))
            OR (JSON_VALUE(@corrections,'$.lastName') IS NULL AND a.lastNameKey=JSON_VALUE(@json,'$.hl7PatientLastNameKey')))
          AND (COALESCE(a.firstName,N'')=COALESCE(JSON_VALUE(@corrections,'$.firstName'),JSON_VALUE(@json,'$.firstName'),N'')
            OR (JSON_VALUE(@corrections,'$.firstName') IS NULL AND a.firstNameKey=JSON_VALUE(@json,'$.hl7PatientFirstNameKey'))))));
        IF @owner IS NULL OR @json IS NULL
        BEGIN
            ;THROW 51404,N'The source row and matched patient are unavailable.',1;
        END;
        INSERT dbo.personIdentifier(personIdentifierId,rootTenantId,owningTenantId,personId,identifierType,identifierValue,assigningFacility,source,reportedDate)
        SELECT NEWID(),@root,@owner,@personId,j.kind,j.val,j.authority,'labReport',COALESCE(TRY_CONVERT(DATE,JSON_VALUE(@json,'$.reportedDate')),TRY_CONVERT(DATE,JSON_VALUE(@json,'$.collectedDate')))
        FROM (SELECT DISTINCT kind,val,authority FROM OPENJSON((SELECT ids FROM OPENJSON(@json) WITH(ids NVARCHAR(MAX) '$.hl7PatientIdentifiersJson')))
         WITH(kind VARCHAR(20) '$.Type',val NVARCHAR(60) '$.Value',authority NVARCHAR(120) '$.Authority')) j
        WHERE j.kind IN('mrn','medicaid','other') AND NULLIF(j.val,N'') IS NOT NULL AND NOT EXISTS
         (SELECT 1 FROM dbo.personIdentifier i WHERE i.personId=@personId AND i.identifierType=j.kind AND i.identifierValue=j.val
         AND COALESCE(i.assigningFacility,N'')=COALESCE(j.authority,N'') AND i.isDeleted=0);
        DECLARE @count INT=@@ROWCOUNT;
        IF @count>0 INSERT logs.auditEvent(appUserId,rootTenantId,eventAction,entityType,entityId,recordCount)
         VALUES(auth.udfSessionUserId(),@root,'create','intakeIdentifiers',@personId,@count);

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
    , @ObjectName  = N'uspPersistIntakeIdentifiers'
    , @Description = N'Persists only identifiers parsed from an authorized HL7 staging row for its matched patient. Does not grant identifier display rights.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspPersistIntakeIdentifiers TO meritWorkerRole;
END;
GO
