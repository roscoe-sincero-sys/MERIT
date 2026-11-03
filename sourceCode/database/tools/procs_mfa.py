"""Email one-time codes: an additional second factor next to the authenticator app (schema auth).

The authenticator app (TOTP) stays required for every account; email codes are an extra way in that a user turns on
only after proving, with a current authenticator code and a code sent to the address, that both are theirs. A
jurisdiction turns the method on with its emailCodesEnabled setting (off by default: NIST SP 800-63B-4 does not
accept email for out-of-band authentication), and it is never offered to anyone holding an administrator role.

The web application generates the six digits, keeps them only long enough to send them, and passes the database a
keyed verifier (HMAC-SHA256). Issuing, superseding, rate limiting and consuming a code all happen here under a lock on
the user's row, so two clicks, two tabs or two web copies cannot hand out two working codes or use one code twice.
"""
from procs_common import WEB

# Every open challenge of the user ends: a newer code, an email change or an MFA reset makes it useless.
SUPERSEDE_OPEN = """UPDATE auth.mfaEmailChallenge
   SET supersededUtc = SYSUTCDATETIME ()
 WHERE appUserId = @appUserId AND consumedUtc IS NULL AND supersededUtc IS NULL AND isDeleted = 0;"""

PROCS = [
    {
        "schema": "auth", "name": "uspGetEmailCodePolicy", "kind": "read", "grants": [], "ticket": "T190",
        "execute_as_owner": True,
        "desc": "Says whether email one-time codes may be used by a user: their data root's emailCodesEnabled setting is "
                "true today and they hold no administrator role. Runs as its owner because the setting is root-scoped "
                "reference data that row-level security hides before a session exists; granted to no role and reached "
                "only through the email-code procedures, which pass the user they are already working on.",
        "params": """@appUserId         UNIQUEIDENTIFIER
, @emailCodesAllowed BIT OUTPUT""",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "notes": "Administrators need phishing-resistant MFA (specification, Authentication), so any administrator "
                 "assignment, current or not, keeps email codes off for the account.",
        "example": "DECLARE @a BIT; exec auth.uspGetEmailCodePolicy @appUserId = '...', @emailCodesAllowed = @a OUTPUT",
        "body": """
DECLARE @root UNIQUEIDENTIFIER = (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @appUserId AND isDeleted = 0);
DECLARE @today DATE = CAST (SYSUTCDATETIME () AS DATE);
DECLARE @value NVARCHAR (2000);

SELECT TOP (1) @value = s.settingValue
  FROM config.jurisdictionSetting AS s
 WHERE s.rootTenantId = @root AND s.settingKey = 'emailCodesEnabled' AND s.isDeleted = 0
   AND s.effectiveFrom <= @today AND (s.effectiveTo IS NULL OR s.effectiveTo >= @today)
 ORDER BY s.effectiveFrom DESC;

SET @emailCodesAllowed = CASE WHEN LOWER (LTRIM (RTRIM (@value))) = N'true'
                               AND NOT EXISTS (SELECT 1
                                                 FROM auth.userAssignment AS ua
                                                 JOIN auth.role           AS r ON r.roleId = ua.roleId
                                                WHERE ua.appUserId = @appUserId AND ua.isDeleted = 0
                                                  AND r.isAdministrator = 1)
                              THEN 1 ELSE 0 END;
""",
    },
    {
        "schema": "auth", "name": "uspGetEmailCodeOption", "kind": "read", "grants": WEB, "ticket": "T190",
        "desc": "What the sign-in page and the sign-in methods page need about email codes for one user: whether the "
                "jurisdiction allows them for this account, whether the user has turned them on, their preferred "
                "method and the address codes would go to. Runs before a session exists at sign-in, so it reads auth "
                "tables and the policy procedure only.",
        "params": "@appUserId UNIQUEIDENTIFIER",
        "key": "CONCAT (N'appUserId=', @appUserId)",
        "body": """
DECLARE @allowed BIT = 0;
EXEC auth.uspGetEmailCodePolicy @appUserId = @appUserId, @emailCodesAllowed = @allowed OUTPUT;

SELECT u.appUserId
     , u.email
     , u.mfaEnabled
     , u.mfaEmailVerifiedUtc
     , u.mfaPreferredMethod
     , @allowed AS emailCodesAllowed
  FROM auth.appUser AS u
 WHERE u.appUserId = @appUserId AND u.isDeleted = 0;
""",
    },
    {
        "schema": "auth", "name": "uspCreateEmailChallenge", "kind": "write", "grants": WEB, "ticket": "T190",
        "desc": "Issues an email one-time code when the account may have one: active, not locked, enrolled in the "
                "authenticator app, with an email address, allowed by the jurisdiction and (for sign-in) with email "
                "codes turned on. Enforces the resend cooldown and the per-account and per-address limits, supersedes "
                "the user's open code and stores the new code's verifier. Returns the outcome, the seconds to wait "
                "when refused, and the address to send to.",
        "params": """@mfaEmailChallengeId UNIQUEIDENTIFIER
, @appUserId           UNIQUEIDENTIFIER
, @purpose             VARCHAR (10)
, @codeVerifier        VARBINARY (32)
, @supportReference    VARCHAR (16)
, @correlationId       VARCHAR (64) = NULL
, @clientAddress       VARCHAR (45) = NULL
, @lifetimeSeconds     INT = 300
, @maxAttempts         INT = 5
, @cooldownSeconds     INT = 30
, @windowMinutes       INT = 15
, @maxPerUser          INT = 5
, @maxPerAddress       INT = 20""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', purpose=', @purpose, N', supportReference=', @supportReference)",
        "notes": "Outcomes: created; cooldown (a code went out less than @cooldownSeconds ago and has been neither used nor failed); limit "
                 "(@maxPerUser codes for the account, or @maxPerAddress from the address, in @windowMinutes); "
                 "notAvailable (the account may not use email codes now). A code whose send failed does not start a "
                 "cooldown, so the person can try again at once, but it still counts toward the limits.",
        "validate": """IF @purpose NOT IN ('signIn', 'enroll')
BEGIN
    ;THROW 50000, N'@purpose must be signIn or enroll.', 1;
END;

IF DATALENGTH (@codeVerifier) <> 32
BEGIN
    ;THROW 50000, N'@codeVerifier must be a 32-byte HMAC-SHA256 value.', 1;
END;

IF @lifetimeSeconds NOT BETWEEN 60 AND 900 OR @maxAttempts NOT BETWEEN 1 AND 10 OR @cooldownSeconds NOT BETWEEN 0 AND 600
   OR @windowMinutes NOT BETWEEN 1 AND 1440 OR @maxPerUser < 1 OR @maxPerAddress < 1
BEGIN
    ;THROW 50000, N'A lifetime, attempt or rate-limit argument is out of range.', 1;
END;""",
        "body": """
DECLARE @now        DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @outcome    VARCHAR (20)  = 'created';
DECLARE @retryAfter INT           = 0;
DECLARE @allowed    BIT           = 0;
DECLARE @email      NVARCHAR (200), @root UNIQUEIDENTIFIER, @active BIT, @mfa BIT, @verified DATETIME2 (3),
        @lockout    DATETIME2 (3), @last DATETIME2 (3), @oldest DATETIME2 (3);

-- Serializes every request for this account: concurrent requests take turns here and see each other's rows.
SELECT @email = email, @root = rootTenantId, @active = isActive, @mfa = mfaEnabled, @verified = mfaEmailVerifiedUtc,
       @lockout = lockoutEndUtc
  FROM auth.appUser WITH (UPDLOCK, HOLDLOCK)
 WHERE appUserId = @appUserId AND isDeleted = 0;

EXEC auth.uspGetEmailCodePolicy @appUserId = @appUserId, @emailCodesAllowed = @allowed OUTPUT;

IF @root IS NULL OR @active = 0 OR @mfa = 0 OR @allowed = 0 OR NULLIF (LTRIM (RTRIM (@email)), N'') IS NULL
   OR (@lockout IS NOT NULL AND @lockout > @now) OR (@purpose = 'signIn' AND @verified IS NULL)
BEGIN
    SET @outcome = 'notAvailable';
END
ELSE
BEGIN
    -- The wait applies to a code still on its way or waiting to be typed; one already used or never sent holds nothing back.
    SELECT @last = MAX (createdDateUtc)
      FROM auth.mfaEmailChallenge
     WHERE appUserId = @appUserId AND deliveryStatus <> 'failed' AND consumedUtc IS NULL AND isDeleted = 0;

    SELECT @oldest = MIN (createdDateUtc)
      FROM auth.mfaEmailChallenge
     WHERE appUserId = @appUserId AND createdDateUtc > DATEADD (MINUTE, -@windowMinutes, @now) AND isDeleted = 0
    HAVING COUNT (*) >= @maxPerUser;

    IF @last IS NOT NULL AND DATEADD (SECOND, @cooldownSeconds, @last) > @now
    BEGIN
        SELECT @outcome = 'cooldown', @retryAfter = DATEDIFF (SECOND, @now, DATEADD (SECOND, @cooldownSeconds, @last)) + 1;
    END
    ELSE IF @oldest IS NOT NULL
    BEGIN
        SELECT @outcome = 'limit', @retryAfter = DATEDIFF (SECOND, @now, DATEADD (MINUTE, @windowMinutes, @oldest)) + 1;
    END
    ELSE IF @clientAddress IS NOT NULL
        AND (SELECT COUNT (*)
               FROM auth.mfaEmailChallenge
              WHERE clientAddress = @clientAddress AND createdDateUtc > DATEADD (MINUTE, -@windowMinutes, @now)
                AND isDeleted = 0) >= @maxPerAddress
    BEGIN
        SELECT @outcome = 'limit', @retryAfter = @windowMinutes * 60;
    END;
END;

IF @outcome = 'created'
BEGIN
    {SUPERSEDE_OPEN}

    INSERT auth.mfaEmailChallenge (mfaEmailChallengeId, appUserId, purpose, codeVerifier, expiresUtc, maxAttempts,
                                   supportReference, correlationId, clientAddress)
    VALUES (@mfaEmailChallengeId, @appUserId, @purpose, @codeVerifier, DATEADD (SECOND, @lifetimeSeconds, @now),
            @maxAttempts, @supportReference, @correlationId, @clientAddress);
END
ELSE IF @outcome IN ('cooldown', 'limit')
BEGIN
    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
    VALUES (@appUserId, @root, 'mfaCodeRefused', 'appUser', @appUserId, @clientAddress);
END;

SELECT @outcome                                                                AS outcome
     , @retryAfter                                                             AS retryAfterSeconds
     , CASE WHEN @outcome = 'created' THEN @email END                          AS email
     , CASE WHEN @outcome = 'created' THEN DATEADD (SECOND, @lifetimeSeconds, @now) END AS expiresUtc;

SET @Comments = CONCAT (N'Email code ', @outcome, N'.');
""".replace("{SUPERSEDE_OPEN}", SUPERSEDE_OPEN.replace("\n", "\n    ")),
    },
    {
        "schema": "auth", "name": "uspRecordEmailChallengeDelivery", "kind": "write", "grants": WEB, "ticket": "T190",
        "desc": "Records how sending an email code ended. Submitted means the mail server accepted the message, not that "
                "it reached the mailbox. A failed send closes the challenge, so a message that arrives late never "
                "works after the person was told it could not be sent, and writes the failure category and SMTP reply "
                "code (never the server's text) for support.",
        "params": """@mfaEmailChallengeId UNIQUEIDENTIFIER
, @submitted           BIT
, @deliveryAttempts    INT
, @failureKind         VARCHAR (30) = NULL
, @smtpReplyCode       INT = NULL""",
        "key": "CONCAT (N'mfaEmailChallengeId=', @mfaEmailChallengeId, N', submitted=', @submitted, N', attempts=', "
               "@deliveryAttempts, N', failureKind=', @failureKind, N', smtpReplyCode=', @smtpReplyCode)",
        "body": """
DECLARE @now DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @user UNIQUEIDENTIFIER, @root UNIQUEIDENTIFIER, @address VARCHAR (45);

UPDATE auth.mfaEmailChallenge
   SET deliveryStatus     = CASE WHEN @submitted = 1 THEN 'submitted' ELSE 'failed' END
     , deliveryUpdatedUtc = @now
     , deliveryAttempts   = @deliveryAttempts
     , failureKind        = CASE WHEN @submitted = 1 THEN NULL ELSE @failureKind END
     , smtpReplyCode      = @smtpReplyCode
     , supersededUtc      = CASE WHEN @submitted = 0 THEN COALESCE (supersededUtc, @now) ELSE supersededUtc END
 WHERE mfaEmailChallengeId = @mfaEmailChallengeId AND deliveryStatus = 'pending' AND isDeleted = 0;

IF @@ROWCOUNT > 0
BEGIN
    SELECT @user = c.appUserId, @root = u.rootTenantId, @address = c.clientAddress
      FROM auth.mfaEmailChallenge AS c
      JOIN auth.appUser           AS u ON u.appUserId = c.appUserId
     WHERE c.mfaEmailChallengeId = @mfaEmailChallengeId;

    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
    VALUES (@user, @root, CASE WHEN @submitted = 1 THEN 'mfaCodeSent' ELSE 'mfaCodeNotSent' END, 'appUser', @user,
            @address);
END;

SET @Comments = CASE WHEN @submitted = 1 THEN N'Email code submitted.' ELSE N'Email code not sent.' END;
""",
    },
    {
        "schema": "auth", "name": "uspVerifyEmailChallenge", "kind": "write", "grants": WEB, "ticket": "T190",
        "desc": "Checks a typed email code against the user's open challenge for the purpose, in one UPDATE so two "
                "submissions of the same code cannot both succeed: a match consumes the challenge, a miss counts an "
                "attempt. An enroll match also turns email codes on for the account. Returns verified, wrong (with "
                "the attempts left), locked (no attempts left), expired, used, superseded (a newer code was sent, or this one could not be sent) or none.",
        "params": """@appUserId     UNIQUEIDENTIFIER
, @purpose       VARCHAR (10)
, @codeVerifier  VARBINARY (32)
, @clientAddress VARCHAR (45) = NULL""",
        "key": "CONCAT (N'appUserId=', @appUserId, N', purpose=', @purpose)",
        "notes": "The verifier is compared inside SQL Server. That comparison is not constant-time, but it compares "
                 "HMAC outputs: without the web application's key, timing reveals nothing about the code.",
        "body": """
DECLARE @now       DATETIME2 (3) = SYSUTCDATETIME ();
DECLARE @outcome   VARCHAR (20);
DECLARE @remaining INT = 0;
DECLARE @root      UNIQUEIDENTIFIER = (SELECT rootTenantId FROM auth.appUser WHERE appUserId = @appUserId);
DECLARE @result    TABLE (matched BIT NOT NULL, failedAttempts INT NOT NULL, maxAttempts INT NOT NULL);

-- An older code (replaced, or already used) is recognised as such and costs no attempt against the open one.
IF NOT EXISTS (SELECT 1 FROM auth.mfaEmailChallenge
                WHERE appUserId = @appUserId AND purpose = @purpose AND codeVerifier = @codeVerifier AND consumedUtc IS NULL
                  AND supersededUtc IS NULL AND isDeleted = 0)
BEGIN
    SELECT TOP (1) @outcome = CASE WHEN consumedUtc IS NOT NULL THEN 'used' ELSE 'superseded' END
      FROM auth.mfaEmailChallenge
     WHERE appUserId = @appUserId AND purpose = @purpose AND codeVerifier = @codeVerifier AND isDeleted = 0
       AND (consumedUtc IS NOT NULL OR supersededUtc IS NOT NULL)
     ORDER BY createdDateUtc DESC;
END;

IF @outcome IS NULL
UPDATE auth.mfaEmailChallenge
   SET consumedUtc    = CASE WHEN codeVerifier = @codeVerifier THEN @now END
     , failedAttempts = failedAttempts + CASE WHEN codeVerifier = @codeVerifier THEN 0 ELSE 1 END
OUTPUT CASE WHEN inserted.consumedUtc IS NULL THEN 0 ELSE 1 END, inserted.failedAttempts, inserted.maxAttempts
  INTO @result (matched, failedAttempts, maxAttempts)
 WHERE appUserId = @appUserId AND purpose = @purpose AND consumedUtc IS NULL AND supersededUtc IS NULL
   AND deliveryStatus <> 'failed' AND expiresUtc > @now AND failedAttempts < maxAttempts AND isDeleted = 0;

IF @outcome IS NOT NULL
BEGIN
    SET @remaining = 0;
END
ELSE IF EXISTS (SELECT 1 FROM @result WHERE matched = 1)
BEGIN
    SET @outcome = 'verified';
END
ELSE IF EXISTS (SELECT 1 FROM @result)
BEGIN
    SELECT @remaining = maxAttempts - failedAttempts FROM @result;
    SET @outcome = CASE WHEN @remaining > 0 THEN 'wrong' ELSE 'locked' END;
END
ELSE
BEGIN
    SELECT TOP (1) @outcome = CASE WHEN consumedUtc IS NOT NULL THEN 'used'
                                   WHEN supersededUtc IS NOT NULL THEN 'superseded'
                                   WHEN failedAttempts >= maxAttempts THEN 'locked'
                                   WHEN expiresUtc <= @now THEN 'expired'
                                   ELSE 'none' END
      FROM auth.mfaEmailChallenge
     WHERE appUserId = @appUserId AND purpose = @purpose AND isDeleted = 0
     ORDER BY createdDateUtc DESC;

    SET @outcome = COALESCE (@outcome, 'none');
END;

IF @outcome = 'verified' AND @purpose = 'enroll'
BEGIN
    UPDATE auth.appUser SET mfaEmailVerifiedUtc = @now WHERE appUserId = @appUserId AND isDeleted = 0;

    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
    VALUES (@appUserId, @root, 'mfaEmailEnabled', 'appUser', @appUserId, @clientAddress);
END
ELSE IF @outcome = 'locked' AND EXISTS (SELECT 1 FROM @result)
BEGIN
    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId, clientAddress)
    VALUES (@appUserId, @root, 'mfaCodeLocked', 'appUser', @appUserId, @clientAddress);
END;

SELECT @outcome AS outcome, @remaining AS remainingAttempts;

SET @Comments = CONCAT (N'Email code ', @outcome, N'.');
""",
    },
    {
        "schema": "auth", "name": "uspSaveMyMfaMethods", "kind": "write", "grants": WEB, "ticket": "T190",
        "desc": "The signed-in user turns email codes off or chooses which second factor sign-in offers first. Turning "
                "email codes on is not done here: only a confirmed enroll code does that (auth.uspVerifyEmailChallenge). "
                "The authenticator app cannot be removed.",
        "params": """@emailCodesOn    BIT
, @preferredMethod VARCHAR (10)""",
        "key": "CONCAT (N'emailCodesOn=', @emailCodesOn, N', preferredMethod=', @preferredMethod)",
        "validate": """IF @preferredMethod NOT IN ('totp', 'email')
BEGIN
    ;THROW 50000, N'@preferredMethod must be totp or email.', 1;
END;""",
        "body": """
DECLARE @appUserId UNIQUEIDENTIFIER = auth.udfSessionUserId ();
DECLARE @wasOn BIT, @wasPreferred VARCHAR (10), @root UNIQUEIDENTIFIER;

IF @appUserId IS NULL
BEGIN
    ;THROW 51403, N'Sign in to change your sign-in methods.', 1;
END;

SELECT @wasOn = CASE WHEN mfaEmailVerifiedUtc IS NULL THEN 0 ELSE 1 END, @wasPreferred = mfaPreferredMethod,
       @root = rootTenantId
  FROM auth.appUser WITH (UPDLOCK, HOLDLOCK)
 WHERE appUserId = @appUserId AND isDeleted = 0;

IF @emailCodesOn = 1 AND @wasOn = 0
BEGIN
    ;THROW 51422, N'Confirm a code sent to your email before turning on email codes.', 1;
END;

IF @emailCodesOn = 0
BEGIN
    SET @preferredMethod = 'totp';
END;

UPDATE auth.appUser
   SET mfaEmailVerifiedUtc = CASE WHEN @emailCodesOn = 0 THEN NULL ELSE mfaEmailVerifiedUtc END
     , mfaPreferredMethod  = @preferredMethod
 WHERE appUserId = @appUserId AND isDeleted = 0;

IF @emailCodesOn = 0 AND @wasOn = 1
BEGIN
    {SUPERSEDE_OPEN}

    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
    VALUES (@appUserId, @root, 'mfaEmailDisabled', 'appUser', @appUserId);
END
ELSE IF @preferredMethod <> @wasPreferred
BEGIN
    INSERT logs.auditEvent (appUserId, rootTenantId, eventAction, entityType, entityId)
    VALUES (@appUserId, @root, 'mfaMethodChanged', 'appUser', @appUserId);
END;

SET @Comments = N'Sign-in methods saved.';
""".replace("{SUPERSEDE_OPEN}", SUPERSEDE_OPEN.replace("\n", "\n    ")),
    },
]
