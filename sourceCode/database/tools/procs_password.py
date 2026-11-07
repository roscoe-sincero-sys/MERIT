"""Password self-service: change, forgotten-password reset links, and the forced change an administrator asks for (#190).

A reset never bypasses MFA. The emailed link proves the mailbox, and the page then also asks for an authenticator code
or a recovery code. Email can't also be the second factor, because NIST SP 800-63B-4 does not accept it for out-of-band
authentication. Tokens reach the database only as keyed verifiers. Issuing, limiting, superseding and using a link happen
here under a lock on the user's row, so one link can set a password once.
"""
from procs_common import WEB

SUPERSEDE_OPEN = """UPDATE auth.passwordReset
   SET supersededUtc = SYSUTCDATETIME ()
 WHERE appUserId = @appUserId AND consumedUtc IS NULL AND supersededUtc IS NULL AND isDeleted = 0;"""

PROCS = [
    {
        "schema": "auth", "name": "uspGetPasswordPolicy", "kind": "read", "grants": [], "ticket": "T211",
        "execute_as_owner": True,
        "desc": "The effective password minimum for a user's or selected home tenant's data root (15 to 128). Runs as its "
                "owner, because a reset page has no session yet and row-level security hides the setting. Granted to "
                "no role; reached only through the password procedures, which pass the user they are working on.",
        "params": """@appUserId         UNIQUEIDENTIFIER = NULL
, @passwordMinLength INT OUTPUT
, @homeTenantId      UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "example": "DECLARE @n INT; exec auth.uspGetPasswordPolicy @appUserId = '...', @passwordMinLength = @n OUTPUT",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = CASE WHEN @homeTenantId IS NOT NULL
    THEN (SELECT rootTenantId FROM auth.tenant WHERE tenantId = @homeTenantId AND isDeleted = 0)
    ELSE (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @appUserId AND isDeleted = 0) END;
DECLARE @today DATE = CAST (SYSUTCDATETIME () AS DATE);
SET @passwordMinLength = NULL;

SELECT TOP (1) @passwordMinLength = TRY_CAST (s.settingValue AS INT)
  FROM config.jurisdictionSetting AS s
 WHERE s.rootTenantId = @root AND s.settingKey = 'passwordMinLength' AND s.isDeleted = 0
   AND s.effectiveFrom <= @today AND (s.effectiveTo IS NULL OR s.effectiveTo >= @today)
 ORDER BY s.effectiveFrom DESC;

SET @passwordMinLength = CASE WHEN @passwordMinLength IS NULL OR @passwordMinLength < 15 THEN 15
                             WHEN @passwordMinLength > 128 THEN 128 ELSE @passwordMinLength END;
""",
    },
    {
        "schema": "auth", "name": "uspGetAdminPasswordPolicy", "kind": "read", "grants": WEB, "ticket": "198",
        "permission": "tenant.administer",
        "desc": "The effective password minimum for an account's selected home tenant. Checks authority over both the "
                "existing account and the selected tenant before the private owner-executed policy lookup; never uses "
                "the administrator's own active root as a substitute for the account's policy.",
        "params": """@appUserId    UNIQUEIDENTIFIER = NULL
, @homeTenantId UNIQUEIDENTIFIER = NULL""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', homeTenantId=', @homeTenantId)",
        "body": """
DECLARE @admin TABLE (tenantId UNIQUEIDENTIFIER PRIMARY KEY);
INSERT @admin SELECT tenantId FROM auth.tvfAdminTenants ();
IF @appUserId IS NOT NULL AND NOT EXISTS
    (SELECT 1 FROM auth.appUser AS u JOIN @admin AS a ON a.tenantId = u.homeTenantId WHERE u.appUserId = @appUserId AND u.isDeleted = 0)
BEGIN
    ;THROW 51403, N'Only an administrator of the account home tenant can check its password policy.', 1;
END;
SET @homeTenantId = COALESCE (@homeTenantId,
    (SELECT homeTenantId FROM auth.appUser WHERE appUserId = @appUserId AND isDeleted = 0),
    TRY_CAST (SESSION_CONTEXT (N'actingTenantId') AS UNIQUEIDENTIFIER));
IF @homeTenantId IS NULL OR NOT EXISTS (SELECT 1 FROM @admin WHERE tenantId = @homeTenantId)
BEGIN
    ;THROW 51403, N'Choose a home tenant you administer.', 1;
END;
DECLARE @minimum INT = 15;
EXEC auth.uspGetPasswordPolicy @appUserId = @appUserId, @passwordMinLength = @minimum OUTPUT, @homeTenantId = @homeTenantId;
SELECT @minimum AS passwordMinLength;
""",
    },
    {
        "schema": "auth", "name": "uspUpgradePasswordHash", "kind": "write", "grants": WEB, "ticket": "198",
        "desc": "Upgrades a password hash after the application verifies the original credential. A binary compare-and-swap "
                "of the old hash and security stamp prevents a concurrent password reset, deactivation or MFA reset from "
                "being overwritten. Does not change the password-change date, stamp, sessions or required-change flag. "
                "Neither old nor new hash is logged. Returns whether the upgrade won.",
        "params": """@appUserId             UNIQUEIDENTIFIER
, @expectedSecurityStamp UNIQUEIDENTIFIER
, @expectedPasswordHash  NVARCHAR (500)
, @passwordHash          NVARCHAR (500)""",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "body": """
IF @passwordHash IS NULL OR LEN (@passwordHash) < 30 OR @expectedPasswordHash IS NULL
BEGIN
    ;THROW 50000, N'Valid password hashes are required.', 1;
END;
UPDATE auth.appUser SET passwordHash = @passwordHash
 WHERE appUserId = @appUserId AND securityStamp = @expectedSecurityStamp AND isActive = 1 AND isDeleted = 0
   AND (lockoutEndUtc IS NULL OR lockoutEndUtc <= SYSUTCDATETIME ())
   AND passwordHash COLLATE Latin1_General_100_BIN2 = @expectedPasswordHash COLLATE Latin1_General_100_BIN2;
DECLARE @updated BIT = CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END;
SELECT @updated AS upgraded;
""",
        "comments": "N'Credential hash upgrade checked.'",
    },
    {
        "schema": "auth", "name": "uspCreatePasswordReset", "kind": "write", "grants": WEB, "ticket": "T211",
        "desc": "Issues a password-reset link when the account may have one: active, enrolled in the authenticator app (a "
                "reset also needs the second factor) and with an email address. Enforces the cooldown and the "
                "per-account and per-address limits, supersedes the user's open link and stores the new token's "
                "verifier. Returns the outcome and, when created, the address and expiry.",
        "params": """@passwordResetId  UNIQUEIDENTIFIER
, @appUserId        UNIQUEIDENTIFIER
, @tokenVerifier    VARBINARY (32)
, @supportReference VARCHAR (16)
, @correlationId    VARCHAR (64) = NULL
, @clientAddress    VARCHAR (45) = NULL
, @lifetimeMinutes  INT = 20
, @maxAttempts      INT = 5
, @cooldownSeconds  INT = 60
, @windowMinutes    INT = 60
, @maxPerUser       INT = 3
, @maxPerAddress    INT = 10""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', supportReference=', @supportReference)",
        "notes": "Outcomes: created; cooldown (a link went out less than @cooldownSeconds ago and is still open); limit "
                 "(@maxPerUser for the account or @maxPerAddress from the address in @windowMinutes); notAvailable. "
                 "The page that asked says the same thing whatever the outcome, so none of this reaches the requester.",
        "validate": """IF DATALENGTH (@tokenVerifier) <> 32
BEGIN
    ;THROW 50000, N'@tokenVerifier must be a 32-byte HMAC-SHA256 value.', 1;
END;

IF @lifetimeMinutes NOT BETWEEN 5 AND 120 OR @maxAttempts NOT BETWEEN 1 AND 10 OR @cooldownSeconds NOT BETWEEN 0 AND 3600
   OR @windowMinutes NOT BETWEEN 1 AND 1440 OR @maxPerUser < 1 OR @maxPerAddress < 1
BEGIN
    ;THROW 50000, N'A lifetime, attempt or rate-limit argument is out of range.', 1;
END;""",
        "body": """
DECLARE @now     DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @outcome VARCHAR (20)  = 'created';
DECLARE @email   NVARCHAR (200), @active BIT, @mfa BIT, @last DATETIME2 (3);

SELECT @email = email, @active = isActive, @mfa = mfaEnabled
  FROM auth.appUser WITH (UPDLOCK, HOLDLOCK)
 WHERE appUserId = @appUserId AND isDeleted = 0;

IF @active IS NULL OR @active = 0 OR @mfa = 0 OR NULLIF (LTRIM (RTRIM (@email)), N'') IS NULL
BEGIN
    SET @outcome = 'notAvailable';
END
ELSE
BEGIN
    SELECT @last = MAX (createdDateUtc)
      FROM auth.passwordReset
     WHERE appUserId = @appUserId AND deliveryStatus <> 'failed' AND consumedUtc IS NULL AND isDeleted = 0;

    IF @last IS NOT NULL AND DATEADD (SECOND, @cooldownSeconds, @last) > @now
    BEGIN
        SET @outcome = 'cooldown';
    END
    ELSE IF (SELECT COUNT (*) FROM auth.passwordReset
              WHERE appUserId = @appUserId AND createdDateUtc > DATEADD (MINUTE, -@windowMinutes, @now) AND isDeleted = 0) >= @maxPerUser
         OR (@clientAddress IS NOT NULL
             AND (SELECT COUNT (*) FROM auth.passwordReset
                   WHERE clientAddress = @clientAddress AND createdDateUtc > DATEADD (MINUTE, -@windowMinutes, @now)
                     AND isDeleted = 0) >= @maxPerAddress)
    BEGIN
        SET @outcome = 'limit';
    END;
END;

IF @outcome = 'created'
BEGIN
    {SUPERSEDE_OPEN}

    INSERT auth.passwordReset (passwordResetId, appUserId, tokenVerifier, expiresUtc, maxAttempts, supportReference,
                               correlationId, clientAddress)
    VALUES (@passwordResetId, @appUserId, @tokenVerifier, DATEADD (MINUTE, @lifetimeMinutes, @now), @maxAttempts,
            @supportReference, @correlationId, @clientAddress);
END;

SELECT @outcome                                                              AS outcome
     , CASE WHEN @outcome = 'created' THEN @email END                        AS email
     , CASE WHEN @outcome = 'created' THEN DATEADD (MINUTE, @lifetimeMinutes, @now) END AS expiresUtc;

SET @Comments = CONCAT (N'Password reset ', @outcome, N'.');
""".replace("{SUPERSEDE_OPEN}", SUPERSEDE_OPEN.replace("\n", "\n    ")),
    },
    {
        "schema": "auth", "name": "uspRecordPasswordResetDelivery", "kind": "write", "grants": WEB, "ticket": "T211",
        "desc": "Records how sending a reset link ended. A failed send closes the link. Submitted means the mail server "
                "accepted it, which is audited as passwordResetRequested.",
        "params": """@passwordResetId  UNIQUEIDENTIFIER
, @submitted        BIT
, @deliveryAttempts INT
, @failureKind      VARCHAR (30) = NULL
, @smtpReplyCode    INT = NULL""",
        "key": "CONCAT (N'passwordResetId=', @passwordResetId, N', submitted=', @submitted, N', attempts=', @deliveryAttempts, "
               "N', failureKind=', @failureKind, N', smtpReplyCode=', @smtpReplyCode)",
        "body": """
DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();

UPDATE auth.passwordReset
   SET deliveryStatus     = CASE WHEN @submitted = 1 THEN 'submitted' ELSE 'failed' END
     , deliveryUpdatedUtc = @now
     , deliveryAttempts   = @deliveryAttempts
     , failureKind        = CASE WHEN @submitted = 1 THEN NULL ELSE @failureKind END
     , smtpReplyCode      = @smtpReplyCode
     , supersededUtc      = CASE WHEN @submitted = 0 THEN COALESCE (supersededUtc, @now) ELSE supersededUtc END
 WHERE passwordResetId = @passwordResetId AND deliveryStatus = 'pending' AND isDeleted = 0;

IF @@ROWCOUNT > 0 AND @submitted = 1
BEGIN
    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
    SELECT r.appUserId, u.rootTenantId, 'passwordResetRequested', 'appUser', r.appUserId, r.clientAddress
      FROM auth.passwordReset AS r
      JOIN auth.appUser       AS u ON u.appUserId = r.appUserId
     WHERE r.passwordResetId = @passwordResetId;
END;

SET @Comments = CASE WHEN @submitted = 1 THEN N'Reset link submitted.' ELSE N'Reset link not sent.' END;
""",
    },
    {
        "schema": "auth", "name": "uspGetPasswordReset", "kind": "read", "grants": WEB, "ticket": "T211",
        "desc": "Checks a reset link: its id and the verifier of its token. Only a matching, open, unexpired link returns "
                "the account, its user name, whether it has a second factor, the password rule and the attempts left. "
                "Otherwise the status alone: expired, used, superseded or none.",
        "params": """@passwordResetId UNIQUEIDENTIFIER
, @tokenVerifier   VARBINARY (32)""",
        "key": "CONCAT (N'passwordResetId=', @passwordResetId)",
        "notes": "The token is 32 random bytes, so guessing it is not practical and a wrong token costs no attempt. "
                 "Attempts count wrong second-factor codes.",
        "body": """
DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @user UNIQUEIDENTIFIER, @status VARCHAR (20), @minLength INT = 15;

SELECT @user   = r.appUserId
     , @status = CASE WHEN r.consumedUtc IS NOT NULL THEN 'used'
                      WHEN r.supersededUtc IS NOT NULL THEN 'superseded'
                      WHEN r.expiresUtc <= @now THEN 'expired'
                      WHEN r.failedAttempts >= r.maxAttempts THEN 'superseded'
                      ELSE 'valid' END
  FROM auth.passwordReset AS r
 WHERE r.passwordResetId = @passwordResetId AND r.tokenVerifier = @tokenVerifier AND r.isDeleted = 0;

IF @status = 'valid'
BEGIN
    EXEC auth.uspGetPasswordPolicy @appUserId = @user, @passwordMinLength = @minLength OUTPUT;
END;

SELECT COALESCE (@status, 'none')                                    AS status
     , CASE WHEN @status = 'valid' THEN u.appUserId END              AS appUserId
     , CASE WHEN @status = 'valid' THEN u.userName END               AS userName
     , CASE WHEN @status = 'valid' THEN u.mfaEnabled ELSE 0 END      AS mfaEnabled
     , CASE WHEN @status = 'valid' THEN u.lockoutEndUtc END          AS lockoutEndUtc
     , @minLength                                                    AS passwordMinLength
     , CASE WHEN @status = 'valid' THEN r.maxAttempts - r.failedAttempts ELSE 0 END AS remainingAttempts
  FROM (SELECT 1 AS one) AS x
  LEFT JOIN auth.passwordReset AS r ON r.passwordResetId = @passwordResetId AND @status = 'valid'
  LEFT JOIN auth.appUser       AS u ON u.appUserId = @user AND @status = 'valid';
""",
    },
    {
        "schema": "auth", "name": "uspRecordPasswordResetFailure", "kind": "write", "grants": WEB, "ticket": "T211",
        "desc": "Counts a wrong second-factor code entered with a reset link. When none are left the link is closed. "
                "Returns the attempts left.",
        "params": """@passwordResetId UNIQUEIDENTIFIER
, @tokenVerifier   VARBINARY (32)""",
        "key": "CONCAT (N'passwordResetId=', @passwordResetId)",
        "body": """
DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @left TABLE (remaining INT NOT NULL);

UPDATE auth.passwordReset
   SET failedAttempts = failedAttempts + 1
     , supersededUtc  = CASE WHEN failedAttempts + 1 >= maxAttempts THEN @now ELSE supersededUtc END
OUTPUT inserted.maxAttempts - inserted.failedAttempts INTO @left (remaining)
 WHERE passwordResetId = @passwordResetId AND tokenVerifier = @tokenVerifier AND consumedUtc IS NULL
   AND supersededUtc IS NULL AND expiresUtc > @now AND isDeleted = 0;

SELECT COALESCE ((SELECT TOP (1) remaining FROM @left), 0) AS remainingAttempts;

SET @Comments = N'Reset link attempt counted.';
""",
    },
    {
        "schema": "auth", "name": "uspCompletePasswordReset", "kind": "write", "grants": WEB, "ticket": "T211",
        "desc": "Sets a new password with a reset link, after the web application has checked the second factor. The "
                "link is consumed in the same statement that checks it, so it works once. The security stamp changes "
                "and every session ends, the lockout clears, other open links close, and passwordResetCompleted is audited.",
        "params": """@passwordResetId UNIQUEIDENTIFIER
, @tokenVerifier   VARBINARY (32)
, @passwordHash    NVARCHAR (500)
, @clientAddress   VARCHAR (45) = NULL""",
        "key": "CONCAT (N'passwordResetId=', @passwordResetId)",
        "body": """
DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @used TABLE (appUserId UNIQUEIDENTIFIER NOT NULL);

UPDATE auth.passwordReset
   SET consumedUtc = @now
OUTPUT inserted.appUserId INTO @used (appUserId)
 WHERE passwordResetId = @passwordResetId AND tokenVerifier = @tokenVerifier AND consumedUtc IS NULL
   AND supersededUtc IS NULL AND expiresUtc > @now AND failedAttempts < maxAttempts AND isDeleted = 0;

DECLARE @appUserId UNIQUEIDENTIFIER = (SELECT TOP (1) appUserId FROM @used);

IF @appUserId IS NOT NULL
BEGIN
    UPDATE auth.appUser
       SET passwordHash = @passwordHash, securityStamp = NEWID (), failedSignInCount = 0, lockoutEndUtc = NULL
         , passwordChangedUtc = @now, passwordChangeRequired = 0
     WHERE appUserId = @appUserId AND isDeleted = 0;

    {SUPERSEDE_OPEN}

    UPDATE auth.sessionScope SET revokedUtc = @now WHERE appUserId = @appUserId AND revokedUtc IS NULL;

    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
    SELECT @appUserId, rootTenantId, 'passwordResetCompleted', 'appUser', @appUserId, @clientAddress
      FROM auth.appUser WHERE appUserId = @appUserId;
END;

SELECT CASE WHEN @appUserId IS NULL THEN 'invalid' ELSE 'reset' END AS outcome, @appUserId AS appUserId;

SET @Comments = CASE WHEN @appUserId IS NULL THEN N'Reset link not valid.' ELSE N'Password reset.' END;
""".replace("{SUPERSEDE_OPEN}", SUPERSEDE_OPEN.replace("\n", "\n    ")),
    },
    {
        "schema": "auth", "name": "uspChangeMyPassword", "kind": "write", "grants": WEB, "ticket": "T211",
        "desc": "The signed-in user's new password, after the web application has checked the current password (and, "
                "for a voluntary change, an authenticator code). The security stamp changes and every session ends, so "
                "the caller starts a fresh session for this browser. Open reset links close, any forced change is "
                "satisfied, and passwordChanged is audited.",
        "params": """@passwordHash  NVARCHAR (500)
, @clientAddress VARCHAR (45) = NULL""",
        "key": "N'own password'",
        "body": """
DECLARE @appUserId UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();

IF @appUserId IS NULL
BEGIN
    ;THROW 51403, N'Sign in to change your password.', 1;
END;

UPDATE auth.appUser
   SET passwordHash = @passwordHash, securityStamp = NEWID (), passwordChangedUtc = @now, passwordChangeRequired = 0
 WHERE appUserId = @appUserId AND isDeleted = 0 AND isActive = 1;

IF @@ROWCOUNT = 0
BEGIN
    ;THROW 51404, N'Your account is not available.', 1;
END;

{SUPERSEDE_OPEN}

UPDATE auth.sessionScope SET revokedUtc = @now WHERE appUserId = @appUserId AND revokedUtc IS NULL;

INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
SELECT @appUserId, rootTenantId, 'passwordChanged', 'appUser', @appUserId, @clientAddress
  FROM auth.appUser WHERE appUserId = @appUserId;

SET @Comments = N'Password changed.';
""".replace("{SUPERSEDE_OPEN}", SUPERSEDE_OPEN),
    },
    {
        "schema": "auth", "name": "uspGetMyPasswordPolicy", "kind": "read", "grants": WEB, "ticket": "T211",
        "desc": "The signed-in user's password rule (minimum length) and when they last changed their password, for the "
                "change-password forms.",
        "params": "",
        "key": "N'own password policy'",
        "body": """
DECLARE @appUserId UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @minLength INT = 15;

IF @appUserId IS NOT NULL
BEGIN
    EXEC auth.uspGetPasswordPolicy @appUserId = @appUserId, @passwordMinLength = @minLength OUTPUT;
END;

SELECT @minLength AS passwordMinLength, u.passwordChangedUtc, u.passwordChangeRequired
  FROM (SELECT 1 AS one) AS x
  LEFT JOIN auth.appUser AS u ON u.appUserId = @appUserId AND u.isDeleted = 0;
""",
    },
]
