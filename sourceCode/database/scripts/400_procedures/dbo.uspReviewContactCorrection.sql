-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspReviewContactCorrection
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

uspReviewContactCorrection

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

exec dbo.uspReviewContactCorrection ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		202-204
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspReviewContactCorrection
      @contactCorrectionId UNIQUEIDENTIFIER
      , @requestStatus VARCHAR(12)
      , @reviewNote NVARCHAR(1000)
      , @rowVersion BINARY(8)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspReviewContactCorrection]')
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

        DECLARE @caregiverId UNIQUEIDENTIFIER,@root UNIQUEIDENTIFIER,@type VARCHAR(12),@value NVARCHAR(200),@city NVARCHAR(80),@state VARCHAR(2),@zip VARCHAR(5),@unitAction VARCHAR(12),@addressKey NVARCHAR(300);
        SELECT @caregiverId=r.caregiverId,@root=r.rootTenantId,@type=r.contactType,@value=r.proposedValue,@city=r.city,@state=r.state,@zip=r.zip5,@unitAction=r.unitAction,@addressKey=r.proposedAddressKey
        FROM dbo.contactCorrection r WITH(UPDLOCK,HOLDLOCK) WHERE r.contactCorrectionId=@contactCorrectionId AND r.isDeleted=0
         AND r.requestStatus='pending' AND r.rowVersion=@rowVersion AND EXISTS(SELECT 1 FROM dbo.person g
         JOIN auth.sessionScopeTenant st ON st.sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS UNIQUEIDENTIFIER)
         AND st.isDeleted=0 AND st.canReadRelatedContacts=1 AND st.tenantId=g.owningTenantId
         CROSS APPLY auth.tvfPersonWritePredicate(g.rootTenantId,g.owningTenantId,g.personId,0) writable
         WHERE g.personId=r.caregiverId AND g.isDeleted=0);
        IF @caregiverId IS NULL
        BEGIN
         ;THROW 51409,N'The request changed, was reviewed, or is outside your caregiver contact-maintenance authority.',1;
        END;
        IF @requestStatus NOT IN('applied','rejected') OR NULLIF(TRIM(@reviewNote),N'') IS NULL
        BEGIN
         ;THROW 51422,N'Choose applied or rejected and record review evidence.',1;
        END;
        IF @requestStatus='applied' AND NOT
         ((@type IN('phone','email') AND EXISTS(SELECT 1 FROM dbo.personContactMethod WHERE personId=@caregiverId AND contactType=@type AND contactValue=@value AND isDeleted=0))
         OR (@type='address' AND EXISTS(SELECT 1 FROM dbo.personLocation pl JOIN dbo.location l ON l.locationId=pl.locationId AND l.isDeleted=0
         WHERE pl.personId=@caregiverId AND pl.role='residence' AND pl.isPrimary=1 AND pl.isDeleted=0 AND pl.startDate<=auth.udfSessionToday()
         AND (pl.endDate IS NULL OR pl.endDate>=auth.udfSessionToday()) AND l.addressLine1=@value AND l.city=@city AND l.state=@state AND l.zip5=@zip
         AND (@unitAction='unspecified' OR (@unitAction IN('set','remove') AND l.addressKey=@addressKey)))))
        BEGIN
         ;THROW 51422,N'Apply and verify the proposed contact, including any requested apartment/unit change, in the caregiver workspace before marking this request applied.',1;
        END;
        UPDATE dbo.contactCorrection SET requestStatus=@requestStatus,reviewNote=@reviewNote,reviewedByUserId=auth.udfSessionUserId(),reviewedUtc=SYSUTCDATETIME(),afterJson=(SELECT (SELECT contactType,contactValue,isPreferred FROM dbo.personContactMethod WHERE personId=@caregiverId AND isDeleted=0 FOR JSON PATH) AS methods,
         (SELECT l.addressLine1,l.unit,l.city,l.state,l.zip5,pl.isPrimary,pl.startDate,pl.endDate FROM dbo.personLocation pl
         JOIN dbo.location l ON l.locationId=pl.locationId AND l.isDeleted=0 WHERE pl.personId=@caregiverId AND pl.isDeleted=0 FOR JSON PATH) AS addresses FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
        WHERE contactCorrectionId=@contactCorrectionId;
        INSERT logs.auditEvent(appUserId,rootTenantId,eventAction,entityType,entityId)
        VALUES(auth.udfSessionUserId(),@root,'update','contactCorrection',@contactCorrectionId);

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
    , @ObjectName  = N'uspReviewContactCorrection'
    , @Description = N'uspReviewContactCorrection';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspReviewContactCorrection TO meritWebRole;
END;
GO
