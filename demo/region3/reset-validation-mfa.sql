SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
IF DB_NAME() <> 'MERITRegion3Demo' THROW 51000, 'Run only on MERITRegion3Demo.', 1;
BEGIN TRANSACTION;
UPDATE auth.appUser SET mfaEnabled=0,mfaSecretProtected=NULL,securityStamp=NEWID(),failedSignInCount=0,lockoutEndUtc=NULL
WHERE userName IN ('region3demo','marylanddemo','countydemo','platformdemo');
UPDATE r SET isDeleted=1 FROM auth.mfaRecoveryCode r JOIN auth.appUser u ON u.appUserId=r.appUserId
WHERE u.userName IN ('region3demo','marylanddemo','countydemo','platformdemo') AND r.isDeleted=0;
UPDATE s SET revokedUtc=SYSUTCDATETIME() FROM auth.sessionScope s JOIN auth.appUser u ON u.appUserId=s.appUserId
WHERE u.userName IN ('region3demo','marylanddemo','countydemo','platformdemo') AND s.revokedUtc IS NULL;
COMMIT;
