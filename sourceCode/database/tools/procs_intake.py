"""Intake: profiles, batches, staging rows, held records and person-match candidates."""
from procs_common import NEWID, WEB, WORKER, BOTH, CONCURRENCY, not_found, JOB_SCOPE, JOB_SCOPE_COLUMNS, JOB_SCOPE_VALUES

HELD_SUMMARY_QUERY = """
SELECT COUNT (*) AS totalCount,
       (SELECT b.fileName FROM dbo.intakeBatch AS b WHERE b.intakeBatchId = @intakeBatchId AND b.isDeleted = 0) AS batchFileName
  FROM dbo.stagingRow AS s
 WHERE s.rowStatus = 'held' AND s.isDeleted = 0
   AND (@intakeBatchId IS NULL OR s.intakeBatchId = @intakeBatchId);
"""

PROCS = [
    {
        "schema": "dbo", "name": "uspGetHeldSummary", "kind": "read", "grants": WEB, "ticket": "#71 #72",
        "permission": "result.release_hold",
        "desc": "Distinct held-row count and the active batch filename, in the same scope as held groups and rows.",
        "params": "@intakeBatchId UNIQUEIDENTIFIER = NULL",
        "key": "CONCAT (N'intakeBatchId=', @intakeBatchId)",
        "body": HELD_SUMMARY_QUERY,
    },
    {
        "schema": "dbo", "name": "uspListIntakeProfiles", "kind": "read", "grants": BOTH, "ticket": "T048",
        "permission": "intake.upload",
        "desc": "Active intake profiles with their sender, for the upload screen.",
        "params": "",
        "key": "NULL",
        "body": """
SELECT p.intakeProfileId, p.profileName, p.profileVersion, p.fileType, p.senderOrganizationId,
       o.organizationName AS senderName, p.headerSignature
  FROM dbo.intakeProfile AS p
  JOIN dbo.organization AS o ON o.organizationId = p.senderOrganizationId
 WHERE p.isDeleted = 0 AND p.isActive = 1
 ORDER BY o.organizationName, p.profileName;
""",
    },
    {
        "schema": "dbo", "name": "uspGetIntakeProfile", "kind": "read", "grants": BOTH, "ticket": "T048",
        "permission": "intake.upload",
        "desc": "One intake profile: file type, header row, date formats, column map, value translations and constants.",
        "params": "@intakeProfileId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'intakeProfileId=', @intakeProfileId)",
        "body": """
SELECT p.intakeProfileId, p.senderOrganizationId, o.organizationName AS senderName, o.cliaNumber AS senderClia,
       p.profileName, p.profileVersion, p.fileType, p.headerRow, p.dateFormats, p.columnMapJson,
       p.valueTranslationsJson, p.constantsJson, p.headerSignature, p.isActive
  FROM dbo.intakeProfile AS p
  JOIN dbo.organization AS o ON o.organizationId = p.senderOrganizationId
 WHERE p.intakeProfileId = @intakeProfileId AND p.isDeleted = 0;
""",
    },
    {
        "schema": "dbo", "name": "uspSaveIntakeProfile", "kind": "write", "grants": WEB, "ticket": "T048",
        "permission": "intake.configure",
        "desc": "Saves an intake profile as a new version. The previous version is deactivated but kept, so batches loaded "
                "with it can be reprocessed with the corrected one.",
        "params": """@intakeProfileId       UNIQUEIDENTIFIER
, @previousProfileId     UNIQUEIDENTIFIER = NULL
, @senderOrganizationId  UNIQUEIDENTIFIER
, @profileName           NVARCHAR (120)
, @fileType              VARCHAR (20)
, @headerRow             INT = 1
, @dateFormats           NVARCHAR (200) = NULL
, @columnMapJson         NVARCHAR (MAX)
, @valueTranslationsJson NVARCHAR (MAX) = NULL
, @constantsJson         NVARCHAR (MAX) = NULL
, @headerSignature       NVARCHAR (2000) = NULL""",
        "key": "CONCAT (N'intakeProfileId=', @intakeProfileId, N', previousProfileId=', @previousProfileId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @version INT = COALESCE ((SELECT profileVersion FROM dbo.intakeProfile WHERE intakeProfileId = @previousProfileId), 0) + 1;

UPDATE dbo.intakeProfile SET isActive = 0 WHERE intakeProfileId = @previousProfileId AND isActive = 1;

INSERT dbo.intakeProfile (intakeProfileId, rootTenantId, owningTenantId, senderOrganizationId, profileName, profileVersion,
                          fileType, headerRow, dateFormats, columnMapJson, valueTranslationsJson, constantsJson,
                          headerSignature)
VALUES (@intakeProfileId, @root, @root, @senderOrganizationId, @profileName, @version, @fileType, @headerRow, @dateFormats,
        @columnMapJson, @valueTranslationsJson, @constantsJson, @headerSignature);

SET @Comments = CONCAT (N'Profile version ', @version, N' saved.');
""",
    },
    {
        "schema": "dbo", "name": "uspCreateIntakeBatch", "kind": "write", "grants": WEB, "ticket": "T083",
        "permission": "intake.upload",
        "desc": "Stores a received file unchanged and queues the intake job that parses, validates, matches and loads it. "
                "Returns at once; the upload screen polls the batch.",
        "params": """@intakeBatchId   UNIQUEIDENTIFIER
, @intakeProfileId UNIQUEIDENTIFIER
, @fileName        NVARCHAR (260)
, @rawContent      NVARCHAR (MAX)
, @jobId           UNIQUEIDENTIFIER
, @contentSha256   CHAR (64) = NULL""",
        "key": "CONCAT (N'intakeBatchId=', @intakeBatchId, N', intakeProfileId=', @intakeProfileId, N', jobId=', @jobId)",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = auth.udfSessionRootTenantId ();
DECLARE @acting UNIQUEIDENTIFIER = auth.udfSessionActingTenantId ();
DECLARE @user UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @sender UNIQUEIDENTIFIER = (SELECT senderOrganizationId FROM dbo.intakeProfile WHERE intakeProfileId = @intakeProfileId
                                      AND isDeleted = 0);

IF @sender IS NULL
BEGIN
    ;THROW 51404, N'The intake profile was not found.', 1;
END;

INSERT dbo.intakeBatch (intakeBatchId, rootTenantId, owningTenantId, intakeProfileId, senderOrganizationId, sourceChannel,
                        fileName, rawContent, contentSha256, receivedUtc, batchStatus, submittedByUserId)
VALUES (@intakeBatchId, @root, @acting, @intakeProfileId, @sender, 'upload', @fileName, @rawContent, @contentSha256,
        SYSUTCDATETIME (), 'received', @user);

""" + JOB_SCOPE + """

INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, availableUtc,
                     requestedByUserId, """ + JOB_SCOPE_COLUMNS + """)
VALUES (@jobId, @root, @acting, 'intakeBatch',
        CONCAT (N'{"intakeBatchId":"', @intakeBatchId, N'","releasedOnly":false}'), 1, SYSUTCDATETIME (), @user,
        """ + JOB_SCOPE_VALUES + """);

SET @Comments = N'Batch received and queued.';
""",
    },
    {
        "schema": "dbo", "name": "uspFindIntakeBatchByContent", "kind": "read", "grants": WEB, "ticket": "T183",
        "permission": "intake.upload",
        "desc": "The first batch already received for this intake profile with exactly this content, if any, so the same "
                "file is not processed twice by mistake.",
        "params": """@intakeProfileId UNIQUEIDENTIFIER
, @contentSha256   CHAR (64)""",
        "key": "CONCAT (N'intakeProfileId=', @intakeProfileId)",
        "body": """
SELECT TOP (1) intakeBatchId, fileName, receivedUtc
  FROM dbo.intakeBatch
 WHERE isDeleted = 0 AND contentSha256 IS NOT NULL AND contentSha256 = @contentSha256 AND intakeProfileId = @intakeProfileId
 ORDER BY receivedUtc;
""",
    },
    {
        "schema": "dbo", "name": "uspListIntakeBatches", "kind": "read", "grants": WEB, "ticket": "T083",
        "permission": "intake.upload",
        "desc": "Recent intake batches with their counts and the status of the job processing them.",
        "params": "@maxRows INT = 50",
        "key": "CONCAT (N'maxRows=', @maxRows)",
        "body": """
SELECT TOP (@maxRows) b.intakeBatchId, b.fileName, b.sourceChannel, b.receivedUtc, b.batchStatus, b.pauseReason,
       b.receivedCount, b.loadedCount, b.heldCount, b.rejectedCount, o.organizationName AS senderName,
       u.displayName AS submittedBy
  FROM dbo.intakeBatch AS b
  LEFT JOIN dbo.organization AS o ON o.organizationId = b.senderOrganizationId
  LEFT JOIN auth.appUser AS u ON u.appUserId = b.submittedByUserId
 WHERE b.isDeleted = 0 AND b.sourceChannel <> 'manual'
 ORDER BY b.receivedUtc DESC;
""",
    },
    {
        "schema": "dbo", "name": "uspGetIntakeBatch", "kind": "read", "grants": BOTH, "ticket": "T083",
        "permission": "intake.upload",
        "desc": "One intake batch for its status card, without the raw content. With @includeContent (the worker), also "
                "the raw content and its profile.",
        "params": """@intakeBatchId  UNIQUEIDENTIFIER
, @includeContent BIT = 0""",
        "key": "CONCAT (N'intakeBatchId=', @intakeBatchId, N', includeContent=', @includeContent)",
        "body": """
SELECT b.intakeBatchId, b.intakeProfileId, b.senderOrganizationId, o.organizationName AS senderName, b.fileName,
       b.sourceChannel, b.receivedUtc, b.batchStatus, b.pauseReason, b.receivedCount, b.loadedCount, b.heldCount,
       b.rejectedCount, IIF (@includeContent = 1, b.rawContent, NULL) AS rawContent
  FROM dbo.intakeBatch AS b
  LEFT JOIN dbo.organization AS o ON o.organizationId = b.senderOrganizationId
 WHERE b.intakeBatchId = @intakeBatchId AND b.isDeleted = 0;
""",
    },
    {
        "schema": "dbo", "name": "uspSetIntakeBatchStatus", "kind": "write", "grants": WORKER, "ticket": "T121",
        "permission": "intake.upload",
        "desc": "Moves a batch through parsing, paused (the sender's layout changed), loaded or failed, and recounts its "
                "rows. A paused batch is not a failed batch; it resumes once the mapping is fixed.",
        "params": """@intakeBatchId UNIQUEIDENTIFIER
, @batchStatus   VARCHAR (20)
, @pauseReason   NVARCHAR (500) = NULL""",
        "key": "CONCAT (N'intakeBatchId=', @intakeBatchId, N', batchStatus=', @batchStatus)",
        "body": """
UPDATE b
   SET batchStatus   = @batchStatus
     , pauseReason   = @pauseReason
     , receivedCount = x.received
     , loadedCount   = COALESCE (x.loaded, 0)
     , heldCount     = COALESCE (x.held, 0)
     , rejectedCount = COALESCE (x.rejected, 0)
  FROM dbo.intakeBatch AS b
 CROSS APPLY (SELECT COUNT (*) AS received,
                     SUM (CASE WHEN rowStatus = 'loaded' THEN 1 ELSE 0 END) AS loaded,
                     SUM (CASE WHEN rowStatus IN ('held', 'released') THEN 1 ELSE 0 END) AS held,
                     SUM (CASE WHEN rowStatus = 'rejected' THEN 1 ELSE 0 END) AS rejected
                FROM dbo.stagingRow AS s WHERE s.intakeBatchId = b.intakeBatchId AND s.isDeleted = 0) AS x
 WHERE b.intakeBatchId = @intakeBatchId AND b.isDeleted = 0;

SET @Comments = N'Batch status set.';
""",
    },
    {
        "schema": "dbo", "name": "uspSaveStagingRows", "kind": "write", "grants": WORKER, "ticket": "T121",
        "permission": "intake.upload",
        "desc": "Stores the parsed rows of a batch as staging rows, before validation. Rows already stored for the batch "
                "are left alone, so a retried job does not duplicate them.",
        "params": """@intakeBatchId UNIQUEIDENTIFIER
, @rowsJson      NVARCHAR (MAX)""",
        "key": "CONCAT (N'intakeBatchId=', @intakeBatchId)",
        "validate": """IF ISJSON (@rowsJson) = 0
BEGIN
    ;THROW 50000, N'@rowsJson must be a JSON array.', 1;
END;""",
        "body": """
INSERT dbo.stagingRow (stagingRowId, rootTenantId, owningTenantId, intakeBatchId, rowNumber, parsedJson)
SELECT j.stagingRowId, b.rootTenantId, b.owningTenantId, @intakeBatchId, j.rowNumber, j.parsedJson
  FROM OPENJSON (@rowsJson)
  WITH (stagingRowId UNIQUEIDENTIFIER, rowNumber INT, parsedJson NVARCHAR (MAX) AS JSON) AS j
  JOIN dbo.intakeBatch AS b ON b.intakeBatchId = @intakeBatchId
 WHERE NOT EXISTS (SELECT 1 FROM dbo.stagingRow AS s WHERE s.intakeBatchId = @intakeBatchId
                                                     AND s.rowNumber = j.rowNumber AND s.isDeleted = 0);

SET @Comments = CONCAT (@@ROWCOUNT, N' staging row(s) stored.');
""",
    },
    {
        "schema": "dbo", "name": "uspGetStagingRowsForProcessing", "kind": "read", "grants": WORKER, "ticket": "T121",
        "permission": "intake.upload",
        "desc": "Staging rows of a batch that still need processing: new rows, or only rows a user released.",
        "params": """@intakeBatchId UNIQUEIDENTIFIER
, @releasedOnly  BIT = 0""",
        "key": "CONCAT (N'intakeBatchId=', @intakeBatchId, N', releasedOnly=', @releasedOnly)",
        "body": """
SELECT stagingRowId, rowNumber, parsedJson, correctedJson, rowStatus
  FROM dbo.stagingRow
 WHERE intakeBatchId = @intakeBatchId AND isDeleted = 0
   AND ((@releasedOnly = 0 AND rowStatus IN ('pending', 'released')) OR (@releasedOnly = 1 AND rowStatus = 'released'))
 ORDER BY rowNumber;
""",
    },
    {
        "schema": "dbo", "name": "uspCompleteStagingRow", "kind": "write", "grants": WORKER, "ticket": "T121",
        "permission": "intake.upload",
        "desc": "Records the outcome of processing one staging row: loaded (with its result), or held with its named "
                "reasons from the system list. Earlier reasons that no longer apply are resolved.",
        "params": """@stagingRowId    UNIQUEIDENTIFIER
, @rowStatus       VARCHAR (20)
, @labResultId     UNIQUEIDENTIFIER = NULL
, @holdReasonsJson NVARCHAR (MAX) = N'[]'
, @isUrgent        BIT = 0""",
        "key": "CONCAT (N'stagingRowId=', @stagingRowId, N', rowStatus=', @rowStatus)",
        "body": f"""
UPDATE dbo.stagingHoldReason SET isResolved = 1
 WHERE stagingRowId = @stagingRowId AND isResolved = 0 AND isDeleted = 0;

UPDATE dbo.stagingRow
   SET rowStatus    = @rowStatus
     , labResultId  = COALESCE (@labResultId, labResultId)
     , isUrgent     = @isUrgent
     , heldSinceUtc = CASE WHEN @rowStatus = 'held' THEN COALESCE (heldSinceUtc, SYSUTCDATETIME ()) ELSE heldSinceUtc END
 WHERE stagingRowId = @stagingRowId AND isDeleted = 0;

INSERT dbo.stagingHoldReason (stagingHoldReasonId, rootTenantId, owningTenantId, stagingRowId, reasonCode, fieldName,
                              reasonMessage, reportedValue)
SELECT {NEWID}, s.rootTenantId, s.owningTenantId, @stagingRowId, j.reasonCode, j.fieldName, j.reasonMessage, j.reportedValue
  FROM OPENJSON (COALESCE (@holdReasonsJson, N'[]'))
  WITH (reasonCode VARCHAR (30), fieldName VARCHAR (60), reasonMessage NVARCHAR (500), reportedValue NVARCHAR (200)) AS j
  JOIN dbo.stagingRow AS s ON s.stagingRowId = @stagingRowId;

SET @Comments = CONCAT (N'Row ', @rowStatus, N'.');
""",
    },
    {
        "schema": "dbo", "name": "uspListHeldGroups", "kind": "read", "grants": WEB, "ticket": "T105",
        "permission": "result.release_hold",
        "desc": "Held records grouped by cause -- reason, the reported value that caused it, and sender -- so that "
                "fixing one unknown code releases every row held for it. Urgent groups first.",
        "params": "@intakeBatchId UNIQUEIDENTIFIER = NULL\n, @includeSummary BIT = 0",
        "key": "CONCAT (N'intakeBatchId=', @intakeBatchId)",
        "body": """
SELECT h.reasonCode, h.reportedValue, b.senderOrganizationId, o.organizationName AS senderName,
       COUNT (DISTINCT s.stagingRowId) AS heldRowCount,
       COUNT (DISTINCT CASE WHEN s.isUrgent = 1 THEN s.stagingRowId END) AS urgentCount,
       MIN (s.heldSinceUtc) AS oldestHeldUtc, COUNT (DISTINCT s.intakeBatchId) AS batchCount,
       MAX (h.reasonMessage) AS sampleMessage
  FROM dbo.stagingHoldReason AS h
  JOIN dbo.stagingRow AS s ON s.stagingRowId = h.stagingRowId AND s.rowStatus = 'held' AND s.isDeleted = 0
  JOIN dbo.intakeBatch AS b ON b.intakeBatchId = s.intakeBatchId
  LEFT JOIN dbo.organization AS o ON o.organizationId = b.senderOrganizationId
 WHERE h.isDeleted = 0 AND h.isResolved = 0
   AND (@intakeBatchId IS NULL OR s.intakeBatchId = @intakeBatchId)
 GROUP BY h.reasonCode, h.reportedValue, b.senderOrganizationId, o.organizationName
 ORDER BY COUNT (DISTINCT CASE WHEN s.isUrgent = 1 THEN s.stagingRowId END) DESC, MIN (s.heldSinceUtc);
""" + "\nIF @includeSummary = 1\nBEGIN\n" + HELD_SUMMARY_QUERY + "\nEND;\n",
    },
    {
        "schema": "dbo", "name": "uspListHeldRows", "kind": "read", "grants": WEB, "ticket": "T105",
        "permission": "result.release_hold",
        "desc": "One page of held rows, optionally for one cause, with their reasons, the reported values and any "
                "correction beside them, and how long each has waited. Urgent rows first.",
        "params": """@reasonCode           VARCHAR (30) = NULL
, @reportedValue        NVARCHAR (200) = NULL
, @senderOrganizationId UNIQUEIDENTIFIER = NULL
, @exactGroup           BIT = 0
, @intakeBatchId        UNIQUEIDENTIFIER = NULL
, @pageNumber           INT = 1
, @pageSize             INT = 50""",
        "key": "CONCAT (N'reasonCode=', @reasonCode, N', senderOrganizationId=', @senderOrganizationId, N', exactGroup=', "
               "@exactGroup, N', intakeBatchId=', @intakeBatchId, N', pageNumber=', @pageNumber)",
        "validate": """IF @pageNumber < 1 OR @pageSize NOT BETWEEN 1 AND 500
BEGIN
    ;THROW 50000, N'@pageNumber must be 1 or more and @pageSize between 1 and 500.', 1;
END;

IF @exactGroup = 1 AND @reasonCode IS NULL
BEGIN
    ;THROW 50000, N'@exactGroup needs @reasonCode: it selects one group from uspListHeldGroups.', 1;
END;""",
        "notes": "With @exactGroup = 1 the reason, reported value and sender select exactly one group of "
                 "uspListHeldGroups, and a null value or sender means none rather than any (issue #9). Without it a "
                 "null filter matches everything, as before.",
        "body": """
SELECT s.stagingRowId, s.intakeBatchId, b.fileName, o.organizationName AS senderName, s.rowNumber, s.parsedJson,
       s.correctedJson, s.isUrgent, s.heldSinceUtc, s.assignedToUserId, u.displayName AS assignedToName,
       (SELECT STRING_AGG (CONCAT (h.reasonCode, N': ', h.reasonMessage), N' | ')
          FROM dbo.stagingHoldReason AS h WHERE h.stagingRowId = s.stagingRowId AND h.isResolved = 0 AND h.isDeleted = 0) AS reasons,
       COUNT (*) OVER () AS totalCount
  FROM dbo.stagingRow AS s
  JOIN dbo.intakeBatch AS b ON b.intakeBatchId = s.intakeBatchId
  LEFT JOIN dbo.organization AS o ON o.organizationId = b.senderOrganizationId
  LEFT JOIN auth.appUser AS u ON u.appUserId = s.assignedToUserId
 WHERE s.rowStatus = 'held' AND s.isDeleted = 0
   AND (@intakeBatchId IS NULL OR s.intakeBatchId = @intakeBatchId)
   AND (b.senderOrganizationId = @senderOrganizationId
        OR (@senderOrganizationId IS NULL AND (@exactGroup = 0 OR b.senderOrganizationId IS NULL)))
   AND (@reasonCode IS NULL OR EXISTS (SELECT 1 FROM dbo.stagingHoldReason AS h WHERE h.stagingRowId = s.stagingRowId
                                          AND h.reasonCode = @reasonCode AND h.isResolved = 0 AND h.isDeleted = 0
                                          AND (h.reportedValue = @reportedValue
                                               OR (@reportedValue IS NULL AND (@exactGroup = 0 OR h.reportedValue IS NULL)))))
 ORDER BY s.isUrgent DESC, s.heldSinceUtc, s.stagingRowId
OFFSET (@pageNumber - 1) * @pageSize ROWS
 FETCH NEXT @pageSize ROWS ONLY;
""",
    },
    {
        "schema": "dbo", "name": "uspSaveStagingCorrection", "kind": "write", "grants": WEB, "ticket": "T105",
        "permission": "result.release_hold",
        "desc": "Saves corrections to a held row, kept beside the reported values. The parsed row and any result's "
                "as-reported snapshot are never changed.",
        "params": """@stagingRowId  UNIQUEIDENTIFIER
, @correctedJson NVARCHAR (MAX)""",
        "key": "CONCAT (N'stagingRowId=', @stagingRowId)",
        "validate": """IF ISJSON (@correctedJson) = 0 OR LEFT (LTRIM (@correctedJson), 1) <> N'{'
BEGIN
    ;THROW 50000, N'@correctedJson must be a JSON object.', 1;
END;""",
        "notes": "@correctedJson is a patch merged into the row's corrections: each field it names is set, a field set to "
                 "null is removed (the reported value applies again), and fields it leaves out are kept. Replacing the "
                 "whole object let a second correction erase the first (issue #10).",
        "body": """
UPDATE s
   SET correctedJson = (SELECT N'{' + STRING_AGG (CONCAT (N'"', STRING_ESCAPE (m.[key], 'json'), N'":"',
                                                        STRING_ESCAPE (m.[value], 'json'), N'"'), N',') + N'}'
                          FROM (SELECT COALESCE (p.[key], e.[key]) AS [key],
                                       CASE WHEN p.[key] IS NOT NULL THEN p.[value] ELSE e.[value] END AS [value]
                                  FROM OPENJSON (COALESCE (s.correctedJson, N'{}')) AS e
                                  FULL JOIN OPENJSON (@correctedJson) AS p ON p.[key] = e.[key]) AS m
                         WHERE m.[value] IS NOT NULL)
  FROM dbo.stagingRow AS s
 WHERE s.stagingRowId = @stagingRowId AND s.rowStatus = 'held' AND s.isDeleted = 0;

SET @Comments = CONCAT (@@ROWCOUNT, N' row(s) corrected.');
""",
    },
    {
        "schema": "dbo", "name": "uspReleaseStagingRows", "kind": "write", "grants": WEB, "ticket": "T105",
        "permission": "result.release_hold",
        "desc": "Releases held rows: they rerun validation and matching in a background job per batch.",
        "params": "@stagingRowIdsJson NVARCHAR (MAX)",
        "key": "N'release'",
        "validate": """IF ISJSON (@stagingRowIdsJson) = 0
BEGIN
    ;THROW 50000, N'@stagingRowIdsJson must be a JSON array of ids.', 1;
END;""",
        "body": f"""
DECLARE @ids TABLE (stagingRowId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @ids SELECT DISTINCT TRY_CAST ([value] AS UNIQUEIDENTIFIER) FROM OPENJSON (@stagingRowIdsJson)
             WHERE TRY_CAST ([value] AS UNIQUEIDENTIFIER) IS NOT NULL;

DECLARE @released TABLE (stagingRowId UNIQUEIDENTIFIER PRIMARY KEY);
DECLARE @batches TABLE (intakeBatchId UNIQUEIDENTIFIER PRIMARY KEY);

UPDATE s SET rowStatus = 'released'
OUTPUT inserted.stagingRowId INTO @released
  FROM dbo.stagingRow AS s
  JOIN @ids AS i ON i.stagingRowId = s.stagingRowId
 WHERE s.rowStatus = 'held' AND s.isDeleted = 0
   AND (NOT EXISTS (SELECT 1 FROM dbo.stagingHoldReason AS h
                     WHERE h.stagingRowId = s.stagingRowId AND h.reasonCode = 'uncertainMatch'
                       AND h.isResolved = 0 AND h.isDeleted = 0)
        OR TRY_CAST (JSON_VALUE (s.correctedJson, '$.matchPersonId') AS UNIQUEIDENTIFIER) IS NOT NULL
        OR JSON_VALUE (s.correctedJson, '$.matchNewPerson') = N'true');

INSERT @batches (intakeBatchId)
SELECT DISTINCT s.intakeBatchId FROM dbo.stagingRow AS s JOIN @released AS i ON i.stagingRowId = s.stagingRowId
 WHERE s.rowStatus = 'released';

{JOB_SCOPE}

INSERT dbo.jobQueue (jobId, rootTenantId, owningTenantId, jobType, payloadJson, payloadVersion, availableUtc,
                     requestedByUserId, {JOB_SCOPE_COLUMNS})
SELECT {NEWID}, b.rootTenantId, b.owningTenantId, 'intakeBatch',
       CONCAT (N'{{"intakeBatchId":"', b.intakeBatchId, N'","releasedOnly":true}}'), 1, SYSUTCDATETIME (),
       auth.udfSessionUserId (), {JOB_SCOPE_VALUES}
  FROM dbo.intakeBatch AS b JOIN @batches AS x ON x.intakeBatchId = b.intakeBatchId;

SELECT COUNT (*) AS releasedCount FROM @released;

SET @Comments = N'Rows released.';
""",
    },
    {
        "schema": "dbo", "name": "uspRejectStagingRows", "kind": "write", "grants": WEB, "ticket": "T105",
        "permission": "result.release_hold",
        "desc": "Rejects held rows with a required reason. The rows are kept; a follow-up letter to the reporter can be "
                "generated from them.",
        "params": """@stagingRowIdsJson NVARCHAR (MAX)
, @rejectReason      NVARCHAR (500)""",
        "key": "N'reject'",
        "validate": """IF ISJSON (@stagingRowIdsJson) = 0
BEGIN
    ;THROW 50000, N'@stagingRowIdsJson must be a JSON array of ids.', 1;
END;

IF NULLIF (LTRIM (RTRIM (@rejectReason)), N'') IS NULL
BEGIN
    ;THROW 51422, N'A reason is required to reject a held record.', 1;
END;""",
        "body": """
UPDATE s SET rowStatus = 'rejected', rejectReason = @rejectReason
  FROM dbo.stagingRow AS s
  JOIN OPENJSON (@stagingRowIdsJson) AS j ON TRY_CAST (j.[value] AS UNIQUEIDENTIFIER) = s.stagingRowId
 WHERE s.rowStatus = 'held' AND s.isDeleted = 0;

SELECT @@ROWCOUNT AS rejectedCount;
SET @Comments = N'Rows rejected.';
""",
    },
    {
        "schema": "dbo", "name": "uspFindPersonCandidates", "kind": "read", "grants": BOTH, "ticket": "T121",
        "permission": "result.view",
        "desc": "Candidate persons for matching an incoming record: same last name (or alias) with the same date of birth "
                "or with day and month swapped, or same first name and date of birth. Scoring and the link decision are "
                "made in the application under the configured matching rules. A person a reviewer has already called a "
                "different person for this incoming identity (@incomingKey) is never returned.",
        "params": """@lastNameKey  NVARCHAR (60)
, @firstNameKey NVARCHAR (60) = NULL
, @dob          DATE = NULL
, @maxRows      INT = 5
, @incomingKey  NVARCHAR (200) = NULL""",
        "key": "CONCAT (N'maxRows=', @maxRows, N', hasDob=', IIF (@dob IS NULL, 0, 1))",
        "body": """
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
""",
    },
    {
        "schema": "dbo", "name": "uspResolveOrganization", "kind": "read", "grants": BOTH, "ticket": "T121",
        "permission": "result.view",
        "desc": "Finds an organization by CLIA number, NPI, or exact normalized name, for linking the lab and ordering "
                "facility on an incoming result.",
        "params": """@cliaNumber VARCHAR (10) = NULL
, @npi        VARCHAR (10) = NULL
, @nameKey    NVARCHAR (120) = NULL""",
        "key": "CONCAT (N'cliaNumber=', @cliaNumber, N', npi=', @npi)",
        "notes": "The identifiers are tried in order, CLIA, then NPI, then name, each as its own index seek; one query "
                 "ORing them scanned the table. The oldest match wins when an identifier is shared.",
        "body": """
DECLARE @organizationId UNIQUEIDENTIFIER;

IF @cliaNumber IS NOT NULL
    SELECT TOP (1) @organizationId = organizationId
      FROM dbo.organization
     WHERE isDeleted = 0 AND cliaNumber IS NOT NULL AND cliaNumber = @cliaNumber
     ORDER BY createdDateUtc, organizationId;

IF @organizationId IS NULL AND @npi IS NOT NULL
    SELECT TOP (1) @organizationId = organizationId
      FROM dbo.organization
     WHERE isDeleted = 0 AND npi IS NOT NULL AND npi = @npi
     ORDER BY createdDateUtc, organizationId;

IF @organizationId IS NULL AND @nameKey IS NOT NULL
    SELECT TOP (1) @organizationId = organizationId
      FROM dbo.organization
     WHERE isDeleted = 0 AND organizationNameKey = @nameKey
     ORDER BY createdDateUtc, organizationId;

SELECT organizationId, organizationName, cliaNumber, npi
  FROM dbo.organization
 WHERE organizationId = @organizationId;
""",
    },
]
