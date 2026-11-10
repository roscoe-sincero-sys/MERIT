SET QUOTED_IDENTIFIER ON; SET XACT_ABORT ON; SET NOCOUNT ON;
IF DB_NAME() <> 'MERITRegion3Demo' THROW 51000, 'Run only on MERITRegion3Demo.', 1;
EXEC config.uspInstallRootTenant @tenantCode='EPA-R3', @tenantName=N'EPA Region 3 Demo', @tenantType='region', @timeZone='Eastern Standard Time';
DECLARE @region uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='EPA-R3');
DECLARE @md uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-LEAD');
UPDATE auth.tenant SET parentTenantId=@region WHERE tenantId=@md;
INSERT auth.tenantClosure (tenantClosureId,ancestorTenantId,descendantTenantId,depth)
SELECT NEWID(),@region,c.descendantTenantId,c.depth+1 FROM auth.tenantClosure c
WHERE c.ancestorTenantId=@md AND c.isDeleted=0 AND NOT EXISTS
(SELECT 1 FROM auth.tenantClosure x WHERE x.ancestorTenantId=@region AND x.descendantTenantId=c.descendantTenantId AND x.isDeleted=0);
UPDATE c SET depth=2+d.depth FROM auth.tenantClosure c JOIN auth.tenantClosure d ON d.descendantTenantId=c.descendantTenantId AND d.ancestorTenantId=@md
WHERE c.ancestorTenantId=(SELECT tenantId FROM auth.tenant WHERE tenantCode='PLATFORM');
GO
EXEC config.uspInstallRootTenant @tenantCode='R3-DE', @tenantName=N'Delaware', @tenantType='state', @statePostalCode='DE', @stateFips='10', @parentTenantCode='EPA-R3', @countiesJson=N'[{"fips": "003", "name": "New Castle County"}]', @timeZone='Eastern Standard Time';
EXEC config.uspInstallChildTenant @tenantCode='R3-DE-COUNTY', @tenantName=N'New Castle County Demo Health Department', @tenantType='jurisdiction', @parentTenantCode='R3-DE', @countyFipsList='003';
EXEC config.uspInstallRootTenant @tenantCode='R3-DC', @tenantName=N'District of Columbia', @tenantType='state', @statePostalCode='DC', @stateFips='11', @parentTenantCode='EPA-R3', @countiesJson=N'[{"fips": "001", "name": "District of Columbia"}]', @timeZone='Eastern Standard Time';
EXEC config.uspInstallChildTenant @tenantCode='R3-DC-COUNTY', @tenantName=N'District of Columbia Demo Health Department', @tenantType='jurisdiction', @parentTenantCode='R3-DC', @countyFipsList='001';
EXEC config.uspInstallRootTenant @tenantCode='R3-PA', @tenantName=N'Pennsylvania', @tenantType='state', @statePostalCode='PA', @stateFips='42', @parentTenantCode='EPA-R3', @countiesJson=N'[{"fips": "101", "name": "Philadelphia County"}]', @timeZone='Eastern Standard Time';
EXEC config.uspInstallChildTenant @tenantCode='R3-PA-COUNTY', @tenantName=N'Philadelphia County Demo Health Department', @tenantType='jurisdiction', @parentTenantCode='R3-PA', @countyFipsList='101';
EXEC config.uspInstallRootTenant @tenantCode='R3-VA', @tenantName=N'Virginia', @tenantType='state', @statePostalCode='VA', @stateFips='51', @parentTenantCode='EPA-R3', @countiesJson=N'[{"fips": "059", "name": "Fairfax County"}]', @timeZone='Eastern Standard Time';
EXEC config.uspInstallChildTenant @tenantCode='R3-VA-COUNTY', @tenantName=N'Fairfax County Demo Health Department', @tenantType='jurisdiction', @parentTenantCode='R3-VA', @countyFipsList='059';
EXEC config.uspInstallRootTenant @tenantCode='R3-WV', @tenantName=N'West Virginia', @tenantType='state', @statePostalCode='WV', @stateFips='54', @parentTenantCode='EPA-R3', @countiesJson=N'[{"fips": "037", "name": "Jefferson County"}]', @timeZone='Eastern Standard Time';
EXEC config.uspInstallChildTenant @tenantCode='R3-WV-COUNTY', @tenantName=N'Jefferson County Demo Health Department', @tenantType='jurisdiction', @parentTenantCode='R3-WV', @countyFipsList='037';
GO
DECLARE @home uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='EPA-R3');
EXEC auth.uspSaveUser @appUserId='c524cc14-5ddb-cd4b-4586-a9e5b7619fff', @userName=N'region3demo', @displayName=N'Region 3 demo administrator', @email=N'region3demo@merit.example', @passwordHash=N'AQAAAAIAAYagAAAAEA1u0ATl+DjI4nmIdjc1lvHD62Rzh7+rrK3OFEmA8a5HaElYifQ6/zguEQO4oGAJUQ==', @homeTenantId=@home;
DECLARE @role_administrator uniqueidentifier=(SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantCode='EPA-R3') AND roleCode='administrator' AND isDeleted=0);
EXEC auth.uspSaveUserAssignment @userAssignmentId='2666f64e-26e1-2bd3-5bec-2f70c85cfcf1', @appUserId='c524cc14-5ddb-cd4b-4586-a9e5b7619fff', @roleId=@role_administrator, @tenantId=@home, @includeDescendants=1, @accessLevel='full', @startDate='2026-01-01';
DECLARE @role_supervisor uniqueidentifier=(SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantCode='EPA-R3') AND roleCode='supervisor' AND isDeleted=0);
EXEC auth.uspSaveUserAssignment @userAssignmentId='7e4d2ff4-d2b6-9278-913c-5e3c30a243ce', @appUserId='c524cc14-5ddb-cd4b-4586-a9e5b7619fff', @roleId=@role_supervisor, @tenantId=@home, @includeDescendants=1, @accessLevel='full', @startDate='2026-01-01';
GO
DECLARE @home uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-LEAD');
EXEC auth.uspSaveUser @appUserId='7137584b-d812-474b-f3f9-dbdfd9b352d0', @userName=N'marylanddemo', @displayName=N'Maryland demo administrator', @email=N'marylanddemo@merit.example', @passwordHash=N'AQAAAAIAAYagAAAAEOhtkvRcsMo9QmjDbWfS9UINYkJejLIw1dq/jpwQD1T+Ah83yfTq8Yhyo3v/i2efbw==', @homeTenantId=@home;
DECLARE @role_administrator uniqueidentifier=(SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantCode='MD-LEAD') AND roleCode='administrator' AND isDeleted=0);
EXEC auth.uspSaveUserAssignment @userAssignmentId='b818f312-1fb0-62c7-b7bc-c84c53fa6329', @appUserId='7137584b-d812-474b-f3f9-dbdfd9b352d0', @roleId=@role_administrator, @tenantId=@home, @includeDescendants=1, @accessLevel='full', @startDate='2026-01-01';
DECLARE @role_supervisor uniqueidentifier=(SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantCode='MD-LEAD') AND roleCode='supervisor' AND isDeleted=0);
EXEC auth.uspSaveUserAssignment @userAssignmentId='9f3d383e-cb73-c4f8-ab00-41c0823080e6', @appUserId='7137584b-d812-474b-f3f9-dbdfd9b352d0', @roleId=@role_supervisor, @tenantId=@home, @includeDescendants=1, @accessLevel='full', @startDate='2026-01-01';
GO
DECLARE @home uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-BCO');
EXEC auth.uspSaveUser @appUserId='fab7d2a9-15bd-161e-7088-e167354fdee0', @userName=N'countydemo', @displayName=N'Baltimore County demo administrator', @email=N'countydemo@merit.example', @passwordHash=N'AQAAAAIAAYagAAAAEP10XHcWssk/slAC8ypupSe5R9maNCMA3Ape+bmvAVUWjamv8y7GothoOjc8EyXSrg==', @homeTenantId=@home;
DECLARE @role_administrator uniqueidentifier=(SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantCode='MD-LEAD') AND roleCode='administrator' AND isDeleted=0);
EXEC auth.uspSaveUserAssignment @userAssignmentId='90502add-1a4d-6c01-6216-dd6832bc99e6', @appUserId='fab7d2a9-15bd-161e-7088-e167354fdee0', @roleId=@role_administrator, @tenantId=@home, @includeDescendants=1, @accessLevel='full', @startDate='2026-01-01';
DECLARE @role_supervisor uniqueidentifier=(SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantCode='MD-LEAD') AND roleCode='supervisor' AND isDeleted=0);
EXEC auth.uspSaveUserAssignment @userAssignmentId='8885a2db-5c64-48d0-4156-598dc8d963fe', @appUserId='fab7d2a9-15bd-161e-7088-e167354fdee0', @roleId=@role_supervisor, @tenantId=@home, @includeDescendants=1, @accessLevel='full', @startDate='2026-01-01';
GO
DECLARE @home uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='PLATFORM');
EXEC auth.uspSaveUser @appUserId='53eabf64-692a-c5e4-e8df-f182aa0cff80', @userName=N'platformdemo', @displayName=N'Platform demo administrator', @email=N'platformdemo@merit.example', @passwordHash=N'AQAAAAIAAYagAAAAEOkGavUdZQQClo/ONHel3eqiFq0mPmiwYCSXd+TXSmLW+hJThcxU4/rrhb7Knp2Cew==', @homeTenantId=@home;
DECLARE @role_platformAdministrator uniqueidentifier=(SELECT roleId FROM auth.role WHERE rootTenantId=(SELECT rootTenantId FROM auth.tenant WHERE tenantCode='PLATFORM') AND roleCode='platformAdministrator' AND isDeleted=0);
EXEC auth.uspSaveUserAssignment @userAssignmentId='fad8bd03-983f-e152-444b-fc1654bc4477', @appUserId='53eabf64-692a-c5e4-e8df-f182aa0cff80', @roleId=@role_platformAdministrator, @tenantId=@home, @includeDescendants=1, @accessLevel='full', @startDate='2026-01-01';
GO
DECLARE @tenant uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-DE-COUNTY');
DECLARE @root uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-DE');
IF NOT EXISTS(SELECT 1 FROM dbo.person WHERE personId='bf75c7f1-c458-946c-ef2e-738e2a053e9d') INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,firstName,lastNameKey,firstNameKey,dob,sex) VALUES('bf75c7f1-c458-946c-ef2e-738e2a053e9d',@root,@tenant,N'DemoDE',N'Synthetic',N'DEMODE',N'SYNTHETIC','2019-03-15','female');
IF NOT EXISTS(SELECT 1 FROM dbo.workTask WHERE workTaskId='4512c0a3-e124-a6e4-1b43-e3d8f7466941') INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,queueCode,title,personId) VALUES('4512c0a3-e124-a6e4-1b43-e3d8f7466941',@root,@tenant,'actionLevel',N'Delaware synthetic follow-up','bf75c7f1-c458-946c-ef2e-738e2a053e9d');
GO
DECLARE @tenant uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-DC-COUNTY');
DECLARE @root uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-DC');
IF NOT EXISTS(SELECT 1 FROM dbo.person WHERE personId='25650d80-f1fb-6137-332a-a5b2e426e561') INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,firstName,lastNameKey,firstNameKey,dob,sex) VALUES('25650d80-f1fb-6137-332a-a5b2e426e561',@root,@tenant,N'DemoDC',N'Synthetic',N'DEMODC',N'SYNTHETIC','2019-03-15','female');
IF NOT EXISTS(SELECT 1 FROM dbo.workTask WHERE workTaskId='70f204c6-5efb-d62b-77c4-90860abaa18b') INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,queueCode,title,personId) VALUES('70f204c6-5efb-d62b-77c4-90860abaa18b',@root,@tenant,'actionLevel',N'District of Columbia synthetic follow-up','25650d80-f1fb-6137-332a-a5b2e426e561');
GO
DECLARE @tenant uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-PA-COUNTY');
DECLARE @root uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-PA');
IF NOT EXISTS(SELECT 1 FROM dbo.person WHERE personId='bac8661b-2433-6710-029e-c43c206a4488') INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,firstName,lastNameKey,firstNameKey,dob,sex) VALUES('bac8661b-2433-6710-029e-c43c206a4488',@root,@tenant,N'DemoPA',N'Synthetic',N'DEMOPA',N'SYNTHETIC','2019-03-15','female');
IF NOT EXISTS(SELECT 1 FROM dbo.workTask WHERE workTaskId='e05c3b29-f447-c580-ccfe-62ad5388897e') INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,queueCode,title,personId) VALUES('e05c3b29-f447-c580-ccfe-62ad5388897e',@root,@tenant,'actionLevel',N'Pennsylvania synthetic follow-up','bac8661b-2433-6710-029e-c43c206a4488');
GO
DECLARE @tenant uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-VA-COUNTY');
DECLARE @root uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-VA');
IF NOT EXISTS(SELECT 1 FROM dbo.person WHERE personId='a54de270-56e4-f62e-cb05-0771a62dc1c0') INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,firstName,lastNameKey,firstNameKey,dob,sex) VALUES('a54de270-56e4-f62e-cb05-0771a62dc1c0',@root,@tenant,N'DemoVA',N'Synthetic',N'DEMOVA',N'SYNTHETIC','2019-03-15','female');
IF NOT EXISTS(SELECT 1 FROM dbo.workTask WHERE workTaskId='39ea2c78-573c-83e8-2bf1-90f5908bd50e') INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,queueCode,title,personId) VALUES('39ea2c78-573c-83e8-2bf1-90f5908bd50e',@root,@tenant,'actionLevel',N'Virginia synthetic follow-up','a54de270-56e4-f62e-cb05-0771a62dc1c0');
GO
DECLARE @tenant uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-WV-COUNTY');
DECLARE @root uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='R3-WV');
IF NOT EXISTS(SELECT 1 FROM dbo.person WHERE personId='6e93cc52-3ffa-6cc9-44b1-77e98112e488') INSERT dbo.person(personId,rootTenantId,owningTenantId,lastName,firstName,lastNameKey,firstNameKey,dob,sex) VALUES('6e93cc52-3ffa-6cc9-44b1-77e98112e488',@root,@tenant,N'DemoWV',N'Synthetic',N'DEMOWV',N'SYNTHETIC','2019-03-15','female');
IF NOT EXISTS(SELECT 1 FROM dbo.workTask WHERE workTaskId='0143dc71-57cd-b522-ebfa-efff2bc02479') INSERT dbo.workTask(workTaskId,rootTenantId,owningTenantId,queueCode,title,personId) VALUES('0143dc71-57cd-b522-ebfa-efff2bc02479',@root,@tenant,'actionLevel',N'West Virginia synthetic follow-up','6e93cc52-3ffa-6cc9-44b1-77e98112e488');
GO
DECLARE @u uniqueidentifier='7137584b-d812-474b-f3f9-dbdfd9b352d0'; DECLARE @s uniqueidentifier=NEWID(); DECLARE @expires datetime2=DATEADD(hour,1,SYSUTCDATETIME());
EXEC auth.uspCreateSessionScope @sessionScopeId=@s,@appUserId=@u,@expiresUtc=@expires;
EXEC sys.sp_set_session_context @key=N'scopeKey',@value=@s; EXEC sys.sp_set_session_context @key=N'appUserId',@value=@u;
DECLARE @md uniqueidentifier=(SELECT tenantId FROM auth.tenant WHERE tenantCode='MD-LEAD'); EXEC sys.sp_set_session_context @key=N'actingTenantId',@value=@md;
GO
EXEC dbo.uspSaveOrganization @organizationId='c8bfbb2d-4ff5-2271-400b-78d1ca03c104', @organizationName=N'Potomac Synthetic Laboratory', @organizationNameKey=N'POTOMAC SYNTHETIC LABORATORY', @cliaNumber='21D0900001', @typesJson=N'["laboratory"]';
EXEC dbo.uspSaveIntakeProfile @intakeProfileId='b5ac401d-6d04-e934-0874-a520d42130e2', @senderOrganizationId='c8bfbb2d-4ff5-2271-400b-78d1ca03c104', @profileName=N'Region 3 Demo A - Potomac Synthetic Laboratory', @fileType='csv', @headerRow=1, @dateFormats=N'yyyy-MM-dd', @columnMapJson=N'{"PatientLast": "lastName", "PatientFirst": "firstName", "DOB": "dob", "Sex": "sex", "Street": "addressLine1", "Apt": "addressUnit", "City": "city", "State": "state", "Zip": "zip5", "County": "countyFips", "LOINC": "loinc", "Result": "value", "Units": "unit", "SampleType": "sampleType", "Method": "testMethod", "Collected": "collectedDate", "Reported": "reportedDate", "Accession": "accessionNumber", "Status": "resultStatus", "LabCLIA": "labCliaNumber"}', @valueTranslationsJson=N'{"sex": {"F": "female", "M": "male"}, "sampleType": {"V": "venous"}, "resultStatus": {"F": "final"}, "testMethod": {"ICPMS": "icpMs"}}', @constantsJson=N'{}', @headerSignature=N'PatientLast,PatientFirst,DOB,Sex,Street,Apt,City,State,Zip,County,LOINC,Result,Units,SampleType,Method,Collected,Reported,Accession,Status,LabCLIA';
GO
EXEC dbo.uspSaveOrganization @organizationId='6a2fd9e2-aedb-0ad4-7b9c-c85aa95b65d8', @organizationName=N'Allegheny Synthetic Laboratory', @organizationNameKey=N'ALLEGHENY SYNTHETIC LABORATORY', @cliaNumber='21D0900002', @typesJson=N'["laboratory"]';
EXEC dbo.uspSaveIntakeProfile @intakeProfileId='771cc208-66d1-5232-df24-8107fbad91c1', @senderOrganizationId='6a2fd9e2-aedb-0ad4-7b9c-c85aa95b65d8', @profileName=N'Region 3 Demo B - Allegheny Synthetic Laboratory', @fileType='csv', @headerRow=1, @dateFormats=N'yyyy-MM-dd', @columnMapJson=N'{"PatientLast": "lastName", "PatientFirst": "firstName", "DOB": "dob", "Sex": "sex", "Street": "addressLine1", "Apt": "addressUnit", "City": "city", "State": "state", "Zip": "zip5", "County": "countyFips", "LOINC": "loinc", "Result": "value", "Units": "unit", "SampleType": "sampleType", "Method": "testMethod", "Collected": "collectedDate", "Reported": "reportedDate", "Accession": "accessionNumber", "Status": "resultStatus", "LabCLIA": "labCliaNumber"}', @valueTranslationsJson=N'{"sex": {"F": "female", "M": "male"}, "sampleType": {"V": "venous"}, "resultStatus": {"F": "final"}, "testMethod": {"ICPMS": "icpMs"}}', @constantsJson=N'{}', @headerSignature=N'PatientLast,PatientFirst,DOB,Sex,Street,Apt,City,State,Zip,County,LOINC,Result,Units,SampleType,Method,Collected,Reported,Accession,Status,LabCLIA';
GO
EXEC dbo.uspSaveOrganization @organizationId='f5f77849-a343-7cdb-f71b-c8c2b0d85e6d', @organizationName=N'Chesapeake Synthetic Laboratory', @organizationNameKey=N'CHESAPEAKE SYNTHETIC LABORATORY', @cliaNumber='21D0900003', @typesJson=N'["laboratory"]';
EXEC dbo.uspSaveIntakeProfile @intakeProfileId='55c67e70-ee74-598e-df27-addf6ee9d181', @senderOrganizationId='f5f77849-a343-7cdb-f71b-c8c2b0d85e6d', @profileName=N'Region 3 Demo C - Chesapeake Synthetic Laboratory', @fileType='csv', @headerRow=1, @dateFormats=N'yyyy-MM-dd', @columnMapJson=N'{"PatientLast": "lastName", "PatientFirst": "firstName", "DOB": "dob", "Sex": "sex", "Street": "addressLine1", "Apt": "addressUnit", "City": "city", "State": "state", "Zip": "zip5", "County": "countyFips", "LOINC": "loinc", "Result": "value", "Units": "unit", "SampleType": "sampleType", "Method": "testMethod", "Collected": "collectedDate", "Reported": "reportedDate", "Accession": "accessionNumber", "Status": "resultStatus", "LabCLIA": "labCliaNumber"}', @valueTranslationsJson=N'{"sex": {"F": "female", "M": "male"}, "sampleType": {"V": "venous"}, "resultStatus": {"F": "final"}, "testMethod": {"ICPMS": "icpMs"}}', @constantsJson=N'{}', @headerSignature=N'PatientLast,PatientFirst,DOB,Sex,Street,Apt,City,State,Zip,County,LOINC,Result,Units,SampleType,Method,Collected,Reported,Accession,Status,LabCLIA';
GO
UPDATE auth.sessionScope SET revokedUtc=SYSUTCDATETIME() WHERE sessionScopeId=TRY_CAST(SESSION_CONTEXT(N'scopeKey') AS uniqueidentifier);
GO
