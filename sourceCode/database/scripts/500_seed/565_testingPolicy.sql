SET XACT_ABORT ON;
GO
-- Upgrades expose the adoption setting without activating a policy for any existing program.
INSERT config.jurisdictionSetting(jurisdictionSettingId,rootTenantId,settingKey,settingValue,effectiveFrom,settingDescription)
SELECT NEWID(),t.tenantId,'testingPolicyVersion',N'none',CONVERT(DATE,SYSUTCDATETIME()),
 N'Adopted clinical testing policy: none (off), or MDH-2025-03 for Maryland. Activate only after program review; reference-level history must also be configured.'
FROM auth.tenant t WHERE t.tenantId=t.rootTenantId AND t.tenantType<>'platform' AND t.isDeleted=0
 AND NOT EXISTS(SELECT 1 FROM config.jurisdictionSetting s WHERE s.rootTenantId=t.tenantId AND s.settingKey='testingPolicyVersion' AND s.isDeleted=0);
GO
