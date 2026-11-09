-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspRequestContactCorrection
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

uspRequestContactCorrection

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission person.edit (auth.udfHasPermission); refuses with 51403 otherwise.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspRequestContactCorrection ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspRequestContactCorrection
      @contactCorrectionId UNIQUEIDENTIFIER
      , @personId UNIQUEIDENTIFIER
      , @caregiverId UNIQUEIDENTIFIER
      , @contactType VARCHAR(12)
      , @proposedValue NVARCHAR(200)
      , @sourceNote NVARCHAR(1000)
      , @city NVARCHAR(80) = NULL
      , @state VARCHAR(2) = NULL
      , @zip5 VARCHAR(5) = NULL
      , @unitAction VARCHAR(12) = 'unspecified'
      , @unit NVARCHAR(30) = NULL
      , @proposedAddressKey NVARCHAR(300) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspRequestContactCorrection]')
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

        IF auth.udfHasPermission (N'person.edit') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (person.edit).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER,@owner UNIQUEIDENTIFIER,@responsible UNIQUEIDENTIFIER,@name NVARCHAR(140);
        SELECT @root=p.rootTenantId,@owner=p.owningTenantId,@responsible=g.owningTenantId,@name=CONCAT(g.lastName,N', ',g.firstName) FROM dbo.person p
        JOIN dbo.personRelationship rel ON rel.personId=p.personId AND rel.relatedPersonId=@caregiverId AND rel.isDeleted=0
        JOIN dbo.person g ON g.personId=rel.relatedPersonId AND g.rootTenantId=p.rootTenantId AND g.isDeleted=0
        CROSS APPLY(SELECT p.personId,@caregiverId AS caregiverId) r
        WHERE p.personId=@personId AND p.isDeleted=0 AND p.mergedIntoPersonId IS NULL
         AND (rel.startDate IS NULL OR rel.startDate<=auth.udfSessionToday()) AND (rel.endDate IS NULL OR rel.endDate>auth.udfSessionToday())
         AND (rel.isPrimaryContact=1 OR rel.hasLegalCustody=1 OR rel.role IN('caregiver','parent','legalGuardian','fosterParent')) AND EXISTS(SELECT 1 FROM dbo.person p
         JOIN auth.sessionScopeTenant st ON st.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
         AND st.isDeleted=0 AND st.canReadRelatedContacts=1
         CROSS APPLY auth.tvfPersonPredicate(p.rootTenantId,p.owningTenantId,p.personId) clinical
         WHERE p.personId=r.personId AND p.isDeleted=0 AND (p.owningTenantId=st.tenantId OR EXISTS
         (SELECT 1 FROM dbo.personAccess pa WHERE pa.personId=p.personId AND pa.tenantId=st.tenantId AND pa.isDeleted=0)));
        IF @owner IS NULL
        BEGIN
         ;THROW 51403,N'An active caregiver relationship and clinical contact access to this patient are required.',1;
        END;
        IF @contactType NOT IN('phone','email','address') OR NULLIF(TRIM(@proposedValue),N'') IS NULL OR NULLIF(TRIM(@sourceNote),N'') IS NULL
         OR @unitAction IS NULL OR @unitAction NOT IN('unspecified','set','remove')
         OR (@contactType<>'address' AND (@unitAction<>'unspecified' OR @unit IS NOT NULL OR @proposedAddressKey IS NOT NULL))
         OR (@unitAction='set' AND NULLIF(TRIM(@unit),N'') IS NULL)
         OR (@unitAction<>'set' AND @unit IS NOT NULL)
         OR (@unitAction='unspecified' AND @proposedAddressKey IS NOT NULL)
         OR (@unitAction IN('set','remove') AND NULLIF(@proposedAddressKey,N'') IS NULL)
         OR (@contactType='address' AND (NULLIF(TRIM(@city),N'') IS NULL OR @state IS NULL OR @zip5 IS NULL OR LEN(@state)<>2 OR LEN(@zip5)<>5 OR @zip5 LIKE '%[^0-9]%'))
        BEGIN
         ;THROW 51422,N'Provide the proposed contact details and their source; addresses require city, state and ZIP.',1;
        END;
        INSERT dbo.contactCorrection(contactCorrectionId,rootTenantId,owningTenantId,personId,caregiverId,caregiverName,responsibleTenantId,contactType,proposedValue,sourceNote,city,state,zip5,unitAction,unit,proposedAddressKey,beforeJson)
        VALUES(@contactCorrectionId,@root,@owner,@personId,@caregiverId,@name,@responsible,@contactType,@proposedValue,@sourceNote,@city,@state,@zip5,@unitAction,@unit,@proposedAddressKey,(SELECT (SELECT contactType,contactValue,isPreferred FROM dbo.personContactMethod WHERE personId=@caregiverId AND isDeleted=0 FOR JSON PATH) AS methods,
         (SELECT l.addressLine1,l.unit,l.city,l.state,l.zip5,pl.isPrimary,pl.startDate,pl.endDate FROM dbo.personLocation pl
         JOIN dbo.location l ON l.locationId=pl.locationId AND l.isDeleted=0 WHERE pl.personId=@caregiverId AND pl.isDeleted=0 FOR JSON PATH) AS addresses FOR JSON PATH,WITHOUT_ARRAY_WRAPPER));
        INSERT logs.auditEvent(appUserId,rootTenantId,eventAction,entityType,entityId)
        VALUES(auth.udfSessionUserId(),@root,'create','contactCorrection',@contactCorrectionId);

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
    , @ObjectName  = N'uspRequestContactCorrection'
    , @Description = N'uspRequestContactCorrection';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspRequestContactCorrection TO meritWebRole;
END;
GO
