-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspOpenExposureCase
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Opens a case for a person and analyte from a qualifying result. The owning tenant comes from the case ownership rules: child and pregnancy cases go to the local health department serving the county of the current primary residence; adult cases go to the tenant named by setting adultCaseOwnerTenantCode; otherwise the root tenant. Gives the owner access to the person.

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Requires permission case.open (auth.udfHasPermission); refuses with 51403 otherwise.

One open case per person per analyte is enforced by UX_dbo_exposureCase_OneOpenPerAnalyte as well as by the check here, which gives the user a readable message. Returns caseNumber, owningTenantId and owningTenantName.

Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec dbo.uspOpenExposureCase ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		T045
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspOpenExposureCase
      @exposureCaseId        UNIQUEIDENTIFIER
      , @personId              UNIQUEIDENTIFIER
      , @analyteId             UNIQUEIDENTIFIER
      , @population            VARCHAR (10)
      , @classification        VARCHAR (12)
      , @qualifyingLabResultId UNIQUEIDENTIFIER
      , @workflowTemplate      VARCHAR (30)
      , @openedDate            DATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspOpenExposureCase]')
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
    SET @KeyParameters = CONCAT (N'exposureCaseId=', @exposureCaseId, N', personId=', @personId, N', analyteId=', @analyteId);

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        IF auth.udfHasPermission (N'case.open') = 0
        BEGIN
            ;THROW 51403, N'You do not have permission to do this (case.open).', 1;
        END;

        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @root UNIQUEIDENTIFIER = (SELECT rootTenantId FROM dbo.person WHERE personId = @personId);
        DECLARE @today DATE = CAST (SYSUTCDATETIME () AS DATE);
        DECLARE @owner UNIQUEIDENTIFIER;

        IF EXISTS (SELECT 1 FROM dbo.exposureCase WITH (UPDLOCK, HOLDLOCK)
                    WHERE personId = @personId AND analyteId = @analyteId AND caseStatus <> 'closed' AND isDeleted = 0)
        BEGIN
            ;THROW 51422, N'This person already has an open case for this analyte; the result attaches to it.', 1;
        END;

        IF @population IN ('child', 'pregnant')
        BEGIN
            SELECT TOP (1) @owner = tj.tenantId
              FROM dbo.personLocation AS pl
              JOIN dbo.location AS l ON l.locationId = pl.locationId
              JOIN config.subJurisdiction AS sj ON sj.rootTenantId = l.rootTenantId AND sj.countyFips = l.countyFips
              JOIN auth.tenantJurisdiction AS tj ON tj.subJurisdictionId = sj.subJurisdictionId AND tj.isDeleted = 0
              JOIN auth.tenant AS t ON t.tenantId = tj.tenantId AND t.tenantType = 'localHealthDepartment' AND t.isActive = 1
             WHERE pl.personId = @personId AND pl.isDeleted = 0 AND pl.role = 'residence' AND pl.isPrimary = 1
               AND pl.startDate <= @today AND (pl.endDate IS NULL OR pl.endDate >= @today);
        END
        ELSE IF @population = 'adult'
        BEGIN
            SELECT TOP (1) @owner = t.tenantId
              FROM config.jurisdictionSetting AS s
              JOIN auth.tenant AS t ON t.tenantCode = s.settingValue AND t.rootTenantId = @root AND t.isActive = 1
             WHERE s.rootTenantId = @root AND s.settingKey = 'adultCaseOwnerTenantCode' AND s.isDeleted = 0
               AND s.effectiveFrom <= @today AND (s.effectiveTo IS NULL OR s.effectiveTo >= @today);
        END;

        SET @owner = COALESCE (@owner, @root);

        DECLARE @code VARCHAR (8) = (SELECT code FROM config.analyte WHERE analyteId = @analyteId);
        DECLARE @number VARCHAR (20) = CONCAT (@code, '-', YEAR (@openedDate), '-',
                                               RIGHT (CONCAT ('000000', NEXT VALUE FOR dbo.caseNumberSequence), 6));

        INSERT dbo.exposureCase (exposureCaseId, rootTenantId, owningTenantId, caseNumber, personId, analyteId, population,
                                 classification, computedClassification, caseStatus, qualifyingLabResultId, highestLabResultId,
                                 workflowTemplate, openedDate)
        VALUES (@exposureCaseId, @root, @owner, @number, @personId, @analyteId, @population, @classification, @classification,
                'open', @qualifyingLabResultId, @qualifyingLabResultId, @workflowTemplate, @openedDate);

        INSERT dbo.caseActivity (caseActivityId, rootTenantId, owningTenantId, exposureCaseId, activityType, activityDate,
                                 workerUserId, note)
        VALUES (CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) AS BINARY (6)) AS UNIQUEIDENTIFIER), @root, @owner, @exposureCaseId, 'statusChange', @openedDate, auth.udfSessionUserId (),
                CONCAT (N'Case opened as ', @classification, N' from a qualifying result.'));

        EXEC dbo.uspRefreshPersonAccess @personId = @personId;

        SELECT @number AS caseNumber, @owner AS owningTenantId, t.tenantName AS owningTenantName
          FROM auth.tenant AS t WHERE t.tenantId = @owner;

        SET @Comments = N'Case opened.';

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
    , @ObjectName  = N'uspOpenExposureCase'
    , @Description = N'Opens a case for a person and analyte from a qualifying result. The owning tenant comes from the case ownership rules: child and pregnancy cases go to the local health department serving the county of the current primary residence; adult cases go to the tenant named by setting adultCaseOwnerTenantCode; otherwise the root tenant. Gives the owner access to the person.';
GO

IF DATABASE_PRINCIPAL_ID (N'meritWebRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspOpenExposureCase TO meritWebRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'meritWorkerRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspOpenExposureCase TO meritWorkerRole;
END;
GO
