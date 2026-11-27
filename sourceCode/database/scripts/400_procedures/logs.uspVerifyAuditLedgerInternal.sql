-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspVerifyAuditLedgerInternal
Author:       MERIT build
CreateDate:   2026-10-24
========================================================================================================================
Description:

Ungranted owner-context helper for logs.uspVerifyAuditLedger: verifies automatic digest storage via sys.sp_verify_database_ledger_from_digest_storage when @useAutomaticStorage = 1, verifies a required external digest when @requireDigest = 1, or generates and verifies a fresh digest (#334).

========================================================================================================================
Requirements and Key Dependencies:

See the procedure body.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

Rule 8 error-only instrumentation (a procedure that only reads), generated from templates/procedure-readonly.sql by tools/generateProcedures.py. Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

exec logs.uspVerifyAuditLedgerInternal ...

========================================================================================================================
Modification History:

Date:		2026-10-24
Author:		MERIT build
Ticket:		334
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspVerifyAuditLedgerInternal
      @digestJson          NVARCHAR (MAX) = NULL
      , @useAutomaticStorage BIT = 0
      , @requireDigest       BIT = 0
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    IF @@TRANCOUNT > 0 OR (@@OPTIONS & 2) = 2
    BEGIN
        -- Non-error return status (-100) preserves caller transactions even under XACT_ABORT ON + TRY/CATCH
        -- and rejects IMPLICIT_TRANSACTIONS ON before any table write.
        RAISERROR (N'logs.uspVerifyAuditLedgerInternal must be called standalone with IMPLICIT_TRANSACTIONS OFF and no enclosing transaction.', 10, 1);
        RETURN -100;
    END;

    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspVerifyAuditLedgerInternal]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = CONCAT (N'useAutomaticStorage=', @useAutomaticStorage, N', requireDigest=', @requireDigest);

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

        DECLARE @effectiveDigest NVARCHAR (MAX) = NULLIF (LTRIM (RTRIM (@digestJson)), N'');
        DECLARE @verified BIT = 0;
        DECLARE @lastBlockId BIGINT = NULL;
        DECLARE @detail NVARCHAR (500) = N'OK';
        DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
        DECLARE @hasAutoDigest BIT = CASE WHEN EXISTS (SELECT 1 FROM sys.database_ledger_digest_locations WHERE is_current = 1) THEN 1 ELSE 0 END;

        BEGIN TRY
            IF @useAutomaticStorage = 1
            BEGIN
                DECLARE @autoLocations NVARCHAR (MAX) = (
                    SELECT path, last_digest_block_id, is_current
                      FROM sys.database_ledger_digest_locations
                     FOR JSON AUTO, INCLUDE_NULL_VALUES
                );
                IF @hasAutoDigest = 0 OR @autoLocations IS NULL
                BEGIN
                    SET @detail = N'No active automatic ledger digest storage endpoint is configured.';
                END
                ELSE
                BEGIN
                    DECLARE @autoRc INT = 0;
                    EXEC @autoRc = sys.sp_verify_database_ledger_from_digest_storage @digest_locations = @autoLocations;
                    IF @autoRc = 0
                    BEGIN
                        SET @verified = 1;
                        SET @detail = N'Automatic digest storage verified.';
                    END
                    ELSE
                    BEGIN
                        SET @detail = CONCAT (N'Automatic digest storage verification returned status ', @autoRc, N'.');
                    END;
                END;
            END
            ELSE IF @requireDigest = 1 AND @effectiveDigest IS NULL
            BEGIN
                SET @detail = N'A non-empty external digest JSON payload is required.';
            END
            ELSE
            BEGIN
                IF @effectiveDigest IS NULL
                BEGIN
                    DECLARE @gen TABLE (latest_digest NVARCHAR (MAX));
                    INSERT @gen (latest_digest)
                    EXEC sys.sp_generate_database_ledger_digest;
                    SELECT TOP (1) @effectiveDigest = latest_digest FROM @gen;
                END;

                IF @effectiveDigest IS NULL
                BEGIN
                    SET @detail = N'No database ledger digest was returned.';
                END
                ELSE
                BEGIN
                    -- sys.sp_verify_database_ledger cannot run inside INSERT ... EXEC (error 574); call directly
                    -- so the caller reads the engine's native last_verified_block_id result set.
                    DECLARE @manualRc INT = 0;
                    EXEC @manualRc = sys.sp_verify_database_ledger @digest_locations = @effectiveDigest;
                    IF @manualRc = 0
                    BEGIN
                        SET @verified = 1;
                        SET @detail = N'Ledger verified.';
                    END
                    ELSE
                    BEGIN
                        SET @detail = CONCAT (N'Ledger verification returned status ', @manualRc, N'.');
                    END;
                END;
            END;
        END TRY
        BEGIN CATCH
            SET @verified = 0;
            SET @detail = CONCAT (N'Ledger verification failed (error ', ERROR_NUMBER (), N').');
        END CATCH;

        -- I20: Surface SQL verification failures as a critical platform security finding so /admin/security-alerts shows tampering.
        IF @verified = 0
        BEGIN
            DECLARE @platform UNIQUEIDENTIFIER = CAST (HASHBYTES ('MD5', N'tenant:PLATFORM') AS UNIQUEIDENTIFIER);
            DECLARE @fp VARCHAR (128) = CONCAT ('ledgerVerificationFailed|', CONVERT (VARCHAR (10), @now, 23));
            IF NOT EXISTS (SELECT 1 FROM logs.securityFinding WHERE fingerprint = @fp AND isDeleted = 0)
            BEGIN
                INSERT logs.securityFinding (securityFindingId, rootTenantId, ruleCode, severity, title, detailJson,
                                             windowStartUtc, windowEndUtc, detectedUtc, status, fingerprint)
                VALUES (NEWID (), @platform, 'ledgerVerificationFailed', 'critical',
                        N'Audit ledger verification failed',
                        (SELECT @detail AS detail FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
                        @now, @now, @now, 'open', @fp);
            END;
        END;

        SELECT @verified AS verified,
               @lastBlockId AS lastVerifiedBlockId,
               @effectiveDigest AS digestJson,
               @now AS verifiedUtc,
               @detail AS detail,
               @hasAutoDigest AS hasAutomaticDigestStorage;

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
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspVerifyAuditLedgerInternal'
    , @Description = N'Ungranted owner-context helper for logs.uspVerifyAuditLedger: verifies automatic digest storage via sys.sp_verify_database_ledger_from_digest_storage when @useAutomaticStorage = 1, verifies a required external digest when @requireDigest = 1, or generates and verifies a fresh digest (#334).';
GO
