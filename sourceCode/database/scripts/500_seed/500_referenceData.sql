SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

/*
    Shipped reference data: permissions, administrator lookup values with their extract codes, analytes, analyte
    specimens, accepted units, LOINC mappings, action codes and federal extract layouts.

    Converges rather than re-applies: every row has a deterministic key (MD5 of a stable name, cast to
    UNIQUEIDENTIFIER), new rows are inserted, rows whose shipped wording changed are updated, and rows that are already
    right are not touched, so a second run moves no audit column. Nothing is deleted.

    LOINC mappings are a starting set for the four shipped analytes. Verify them against the current LOINC release and
    your laboratories' compendia before go-live; unknown codes go to the held-records queue, so a missing mapping
    stops nothing.
*/

-- ------------------------------------------------------------------------------------------------ permissions
MERGE auth.permission AS t
USING (VALUES
    ('person.view',               N'View persons, their places, jobs, relationships and history', 'read'),
    ('person.edit',               N'Create and change persons and their linked records', 'write'),
    ('person.view_identifiers',   N'See external identifiers such as medical record and Medicaid numbers', 'read'),
    ('person.merge',              N'Merge duplicate person records', 'write'),
    ('result.view',               N'View lab results', 'read'),
    ('result.edit',               N'Enter and correct lab results', 'write'),
    ('result.release_hold',       N'Fix, release and reject held intake records', 'write'),
    ('case.view',                 N'View cases and investigations', 'read'),
    ('case.open',                 N'Open cases (also needed by anyone whose saves can open one automatically)', 'write'),
    ('case.edit',                 N'Manage cases: assign, log activities, override classification, close', 'write'),
    ('investigation.edit',        N'Create and manage investigations and environmental samples', 'write'),
    ('task.view',                 N'View work queues', 'read'),
    ('task.edit',                 N'Assign, complete and dismiss tasks', 'write'),
    ('intake.upload',             N'Upload intake files and view batches', 'write'),
    ('intake.configure',          N'Maintain intake profiles', 'write'),
    ('data.export',               N'Export lists to CSV and Excel', 'read'),
    ('matching.configure',        N'Change person matching configuration', 'write'),
    ('extract.run',               N'Generate and download federal extracts', 'write'),
    ('extract.submit',            N'Mark federal extracts submitted', 'write'),
    ('config.edit',               N'Change thresholds, analytes and jurisdiction settings', 'write'),
    ('audit.view',                N'Search the audit log', 'admin'),
    ('tenant.administer',         N'Manage users, their master profiles and child tenants at the tenants they administer', 'admin'),
    ('platform.administer',       N'Platform administration: create data roots and manage every account (no access to records)', 'admin')
) AS s (permissionCode, permissionName, accessLevel)
   ON t.permissionCode = s.permissionCode AND t.isDeleted = 0
WHEN MATCHED AND (t.permissionName <> s.permissionName OR t.accessLevel <> s.accessLevel) THEN
    UPDATE SET permissionName = s.permissionName, accessLevel = s.accessLevel
WHEN NOT MATCHED THEN
    INSERT (permissionId, permissionCode, permissionName, accessLevel)
    VALUES (CAST (HASHBYTES ('MD5', N'permission:' + s.permissionCode) AS UNIQUEIDENTIFIER), s.permissionCode, s.permissionName,
            s.accessLevel);
GO

-- ------------------------------------------------------------------------------------------------ lookup values
MERGE config.lookupValue AS t
USING (VALUES
    -- relationshipRole: role is what the related person is to the person; inverseLabel names the other person on the
    -- related person's page.
    ('relationshipRole', 'parent',            N'Parent',               N'Child',                    1, NULL, NULL),
    ('relationshipRole', 'legalGuardian',     N'Legal guardian',       N'Ward',                     2, NULL, NULL),
    ('relationshipRole', 'fosterParent',      N'Foster parent',        N'Foster child',             3, NULL, NULL),
    ('relationshipRole', 'otherCaregiver',    N'Other caregiver',      N'Person cared for',         4, NULL, NULL),
    ('relationshipRole', 'spouseOrPartner',   N'Spouse or partner',    N'Spouse or partner',        5, NULL, NULL),
    ('relationshipRole', 'sibling',           N'Sibling',              N'Sibling',                  6, NULL, NULL),
    ('relationshipRole', 'householdMember',   N'Household member',     N'Household member',         7, NULL, NULL),
    ('relationshipRole', 'emergencyContact',  N'Emergency contact',    N'Is emergency contact for', 8, NULL, NULL),
    ('source', 'labReport',   N'Lab report',     NULL, 1, NULL, NULL),
    ('source', 'eicr',        N'eICR',           NULL, 2, NULL, NULL),
    ('source', 'interview',   N'Interview',      NULL, 3, NULL, NULL),
    ('source', 'caseManager', N'Case manager',   NULL, 4, NULL, NULL),
    ('source', 'otherAgency', N'Other agency',   NULL, 5, NULL, NULL),
    ('source', 'provider',    N'Provider',       NULL, 6, NULL, NULL),
    ('source', 'employer',    N'Employer',       NULL, 7, NULL, NULL),
    ('source', 'staff',       N'Staff',          NULL, 8, NULL, NULL),
    ('aliasType', 'asReported',  N'As reported',  NULL, 1, NULL, NULL),
    ('aliasType', 'nickname',    N'Nickname',     NULL, 2, NULL, NULL),
    ('aliasType', 'formerName',  N'Former name',  NULL, 3, NULL, NULL),
    ('aliasType', 'placeholder', N'Placeholder',  NULL, 4, NULL, NULL),
    ('aliasType', 'misspelling', N'Misspelling',  NULL, 5, NULL, NULL),
    ('locationRole', 'residence',    N'Residence',               NULL, 1, NULL, NULL),
    ('locationRole', 'childCare',    N'Child care',              NULL, 2, NULL, NULL),
    ('locationRole', 'school',       N'School',                  NULL, 3, NULL, NULL),
    ('locationRole', 'relativeHome', N'Relative or sitter home', NULL, 4, NULL, NULL),
    ('locationRole', 'otherSite',    N'Other regular site',      NULL, 5, NULL, NULL),
    ('primaryReason', 'custodyOrder',        N'Custody order',         NULL, 1, NULL, NULL),
    ('primaryReason', 'mostOvernights',      N'Most overnights',       NULL, 2, NULL, NULL),
    ('primaryReason', 'guardianDesignation', N'Guardian designation',  NULL, 3, NULL, NULL),
    ('primaryReason', 'labReportedAddress',  N'Lab-reported address',  NULL, 4, NULL, NULL),
    ('primaryReason', 'staffDetermination',  N'Staff determination',   NULL, 5, NULL, NULL),
    ('scheduleType', 'fullTime',         N'Full time',         NULL, 1, NULL, NULL),
    ('scheduleType', 'weekdays',         N'Weekdays',          NULL, 2, NULL, NULL),
    ('scheduleType', 'weekends',         N'Weekends',          NULL, 3, NULL, NULL),
    ('scheduleType', 'alternatingWeeks', N'Alternating weeks', NULL, 4, NULL, NULL),
    ('scheduleType', 'partTime',         N'Part time',         NULL, 5, NULL, NULL),
    ('scheduleType', 'seasonal',         N'Seasonal',          NULL, 6, NULL, NULL),
    -- race: ablesCode is the Race1..Race8 flag the value sets.
    ('race', 'americanIndianAlaskaNative',   N'American Indian or Alaska Native',          NULL, 1, '3', '1'),
    ('race', 'asian',                        N'Asian',                                     NULL, 2, '4', '2'),
    ('race', 'blackAfricanAmerican',         N'Black or African American',                 NULL, 3, '2', '3'),
    ('race', 'nativeHawaiianPacificIslander',N'Native Hawaiian or Other Pacific Islander', NULL, 4, '5', '4'),
    ('race', 'white',                        N'White',                                     NULL, 5, '1', '5'),
    ('race', 'other',                        N'Other race',                                NULL, 6, '6', '6'),
    ('race', 'refused',                      N'Refused',                                   NULL, 7, '7', '7'),
    ('race', 'unknown',                      N'Unknown',                                   NULL, 8, '9', '8'),
    ('language', 'en', N'English', NULL, 1, NULL, NULL),
    ('language', 'es', N'Spanish', NULL, 2, NULL, NULL),
    ('language', 'zh', N'Chinese', NULL, 3, NULL, NULL),
    ('language', 'vi', N'Vietnamese', NULL, 4, NULL, NULL),
    ('language', 'ko', N'Korean', NULL, 5, NULL, NULL),
    ('language', 'fr', N'French', NULL, 6, NULL, NULL),
    ('language', 'am', N'Amharic', NULL, 7, NULL, NULL),
    ('language', 'ar', N'Arabic', NULL, 8, NULL, NULL),
    -- exposureSourceType: ablesCode is the ABLES Process code used when no narrative is available.
    ('exposureSourceType', 'renovation',        N'Renovation',                 NULL, 1,  NULL, '01'),
    ('exposureSourceType', 'hobby',             N'Hobby',                      NULL, 2,  NULL, '02'),
    ('exposureSourceType', 'casting',           N'Casting (bullets, sinkers)', NULL, 3,  NULL, '03'),
    ('exposureSourceType', 'firearms',          N'Firearms or range',          NULL, 4,  NULL, '04'),
    ('exposureSourceType', 'consumerProduct',   N'Consumer product',           NULL, 5,  NULL, '05'),
    ('exposureSourceType', 'spiceFood',         N'Spice or food',              NULL, 6,  NULL, '06'),
    ('exposureSourceType', 'traditionalRemedy', N'Traditional remedy',         NULL, 7,  NULL, '07'),
    ('exposureSourceType', 'cosmetic',          N'Cosmetic',                   NULL, 8,  NULL, '08'),
    ('exposureSourceType', 'cookware',          N'Cookware',                   NULL, 9,  NULL, '09'),
    ('exposureSourceType', 'pica',              N'Pica',                       NULL, 10, NULL, '10'),
    ('exposureSourceType', 'retainedBullet',    N'Retained bullet fragment',   NULL, 11, NULL, '11'),
    ('exposureSourceType', 'travel',            N'Travel',                     NULL, 12, NULL, '12'),
    ('exposureSourceType', 'water',             N'Water',                      NULL, 13, NULL, '13'),
    ('exposureSourceType', 'other',             N'Other',                      NULL, 14, NULL, '99'),
    ('closureReason', 'belowLevelOnRetest', N'Below level on retest',  NULL, 1, NULL, NULL),
    ('closureReason', 'lostToFollowUp',     N'Lost to follow-up',      NULL, 2, NULL, NULL),
    ('closureReason', 'movedOutOfState',    N'Moved out of state',     NULL, 3, NULL, NULL),
    ('closureReason', 'declined',           N'Declined',               NULL, 4, NULL, NULL),
    ('closureReason', 'deceased',           N'Deceased',               NULL, 5, NULL, NULL),
    ('closureReason', 'duplicate',          N'Duplicate',              NULL, 6, NULL, NULL),
    ('closureReason', 'other',              N'Other',                  NULL, 7, NULL, NULL),
    ('activityType', 'contactAttempt',    N'Contact attempt',     NULL, 1, NULL, NULL),
    ('activityType', 'education',         N'Education',           NULL, 2, NULL, NULL),
    ('activityType', 'referral',          N'Referral',            NULL, 3, NULL, NULL),
    ('activityType', 'medicalManagement', N'Medical management',  NULL, 4, NULL, NULL),
    ('activityType', 'letter',            N'Letter',              NULL, 5, NULL, NULL),
    ('activityType', 'note',              N'Note',                NULL, 6, NULL, NULL),
    ('activityOutcome', 'reached',     N'Reached',      NULL, 1, NULL, NULL),
    ('activityOutcome', 'noAnswer',    N'No answer',    NULL, 2, NULL, NULL),
    ('activityOutcome', 'leftMessage', N'Left message', NULL, 3, NULL, NULL),
    ('activityOutcome', 'wrongNumber', N'Wrong number', NULL, 4, NULL, NULL),
    ('activityOutcome', 'completed',   N'Completed',    NULL, 5, NULL, NULL),
    ('activityOutcome', 'other',       N'Other',        NULL, 6, NULL, NULL),
    ('environmentalSampleType', 'xrfPaint',    N'XRF paint',    NULL, 1, NULL, NULL),
    ('environmentalSampleType', 'dustWipe',    N'Dust wipe',    NULL, 2, NULL, NULL),
    ('environmentalSampleType', 'soil',        N'Soil',         NULL, 3, NULL, NULL),
    ('environmentalSampleType', 'water',       N'Water',        NULL, 4, NULL, NULL),
    ('environmentalSampleType', 'product',     N'Product',      NULL, 5, NULL, NULL),
    ('environmentalSampleType', 'air',         N'Air',          NULL, 6, NULL, NULL),
    ('environmentalSampleType', 'surfaceWipe', N'Surface wipe', NULL, 7, NULL, NULL),
    ('regulatoryAction', 'none',                N'None',                                NULL, 1, NULL, NULL),
    ('regulatoryAction', 'notice',              N'Notice',                              NULL, 2, NULL, NULL),
    ('regulatoryAction', 'order',               N'Order',                               NULL, 3, NULL, NULL),
    ('regulatoryAction', 'referralOsha',        N'Referral to OSHA or state plan',      NULL, 4, NULL, NULL),
    ('regulatoryAction', 'referralOtherAgency', N'Referral to another agency',          NULL, 5, NULL, NULL),
    ('organizationType', 'employer',         N'Employer',           NULL, 1, NULL, NULL),
    ('organizationType', 'childCare',        N'Child-care provider',NULL, 2, NULL, NULL),
    ('organizationType', 'school',           N'School',             NULL, 3, NULL, NULL),
    ('organizationType', 'laboratory',       N'Laboratory',         NULL, 4, NULL, NULL),
    ('organizationType', 'providerPractice', N'Provider practice',  NULL, 5, NULL, NULL),
    ('organizationType', 'healthDepartment', N'Health department',  NULL, 6, NULL, NULL),
    ('organizationType', 'selfEmployed',     N'Self-employed',      NULL, 7, NULL, NULL),
    ('testMethod', 'icpMs',                       N'ICP-MS',                         NULL, 1, '1', NULL),
    ('testMethod', 'graphiteFurnaceAa',           N'Graphite furnace AA',            NULL, 2, '2', NULL),
    ('testMethod', 'anodicStrippingVoltammetry',  N'Anodic stripping voltammetry',   NULL, 3, '3', NULL),
    ('testMethod', 'pointOfCare',                 N'Point-of-care analyzer',         NULL, 4, '4', NULL),
    ('testMethod', 'other',                       N'Other',                          NULL, 5, '8', NULL),
    ('testMethod', 'unknown',                     N'Unknown',                        NULL, 6, '9', NULL),
    ('sampleType', 'venous',    N'Venous',    NULL, 1, '1', NULL),
    ('sampleType', 'capillary', N'Capillary', NULL, 2, '2', NULL),
    ('sampleType', 'unknown',   N'Unknown',   NULL, 3, '9', NULL),
    ('reasonForTest', 'screening',              N'Screening',               NULL, 1, NULL, NULL),
    ('reasonForTest', 'confirmation',           N'Confirmation',            NULL, 2, NULL, NULL),
    ('reasonForTest', 'followUp',               N'Follow-up',               NULL, 3, NULL, NULL),
    ('reasonForTest', 'occupationalMonitoring', N'Occupational monitoring', NULL, 4, NULL, NULL),
    ('payerType', 'medicaid',  N'Medicaid',  NULL, 1, '1', NULL),
    ('payerType', 'private',   N'Private',   NULL, 2, '2', NULL),
    ('payerType', 'uninsured', N'Uninsured', NULL, 3, '3', NULL),
    ('payerType', 'unknown',   N'Unknown',   NULL, 4, '9', NULL),
    ('housingType', 'singleFamily',  N'Single-family',  NULL, 1, NULL, NULL),
    ('housingType', 'multiUnit',     N'Multi-unit',     NULL, 2, NULL, NULL),
    ('housingType', 'mobileHome',    N'Mobile home',    NULL, 3, NULL, NULL),
    ('housingType', 'institutional', N'Institutional',  NULL, 4, NULL, NULL),
    ('housingType', 'commercial',    N'Commercial',     NULL, 5, NULL, NULL),
    ('tenure', 'ownerOccupied', N'Owner-occupied', NULL, 1, NULL, NULL),
    ('tenure', 'rental',        N'Rental',         NULL, 2, NULL, NULL),
    ('tenure', 'unknown',       N'Unknown',        NULL, 3, NULL, NULL),
    ('identifierType', 'mrn',      N'Medical record number', NULL, 1, NULL, NULL),
    ('identifierType', 'medicaid', N'Medicaid ID',           NULL, 2, NULL, NULL),
    ('identifierType', 'stateId',  N'State ID',              NULL, 3, NULL, NULL),
    ('identifierType', 'other',    N'Other',                 NULL, 4, NULL, NULL),
    ('productType', 'spice',            N'Spice',             NULL, 1, NULL, NULL),
    ('productType', 'remedy',           N'Remedy',            NULL, 2, NULL, NULL),
    ('productType', 'cosmetic',         N'Cosmetic',          NULL, 3, NULL, NULL),
    ('productType', 'cookware',         N'Cookware',          NULL, 4, NULL, NULL),
    ('productType', 'ceremonialPowder', N'Ceremonial powder', NULL, 5, NULL, NULL),
    ('productType', 'other',            N'Other',             NULL, 6, NULL, NULL)
) AS s (lookupType, code, displayName, inverseLabel, sortOrder, cblsCode, ablesCode)
   ON t.lookupType = s.lookupType AND t.code = s.code AND t.isDeleted = 0
WHEN MATCHED AND (t.displayName <> s.displayName OR t.inverseLabel IS DISTINCT FROM s.inverseLabel
                  OR t.sortOrder <> s.sortOrder OR t.cblsCode IS DISTINCT FROM s.cblsCode
                  OR t.ablesCode IS DISTINCT FROM s.ablesCode) THEN
    UPDATE SET displayName = s.displayName, inverseLabel = s.inverseLabel, sortOrder = s.sortOrder,
               cblsCode = s.cblsCode, ablesCode = s.ablesCode
WHEN NOT MATCHED THEN
    INSERT (lookupValueId, lookupType, code, displayName, inverseLabel, sortOrder, cblsCode, ablesCode)
    VALUES (CAST (HASHBYTES ('MD5', N'lookup:' + s.lookupType + N':' + s.code) AS UNIQUEIDENTIFIER), s.lookupType, s.code,
            s.displayName, s.inverseLabel, s.sortOrder, s.cblsCode, s.ablesCode);
GO

-- ------------------------------------------------------------------------------------------------ analytes
MERGE config.analyte AS t
USING (VALUES ('PB', N'Lead', 1, 1, 'CBLS,ABLES'),
              ('HG', N'Mercury', 1, 2, 'stateReport'),
              ('CD', N'Cadmium', 1, 3, 'stateReport'),
              ('AS', N'Arsenic', 1, 4, 'stateReport')) AS s (code, displayName, isActive, sortOrder, extractTargets)
   ON t.code = s.code AND t.isDeleted = 0
WHEN MATCHED AND (t.displayName <> s.displayName OR t.extractTargets <> s.extractTargets) THEN
    UPDATE SET displayName = s.displayName, extractTargets = s.extractTargets
WHEN NOT MATCHED THEN
    INSERT (analyteId, code, displayName, isActive, sortOrder, extractTargets)
    VALUES (CAST (HASHBYTES ('MD5', N'analyte:' + s.code) AS UNIQUEIDENTIFIER), s.code, s.displayName, s.isActive,
            s.sortOrder, s.extractTargets);
GO

MERGE config.analyteSpecimen AS t
USING (SELECT CAST (HASHBYTES ('MD5', N'analyteSpecimen:' + v.code + N':' + v.specimen) AS UNIQUEIDENTIFIER) AS analyteSpecimenId,
              a.analyteId, v.specimen, v.canonicalUnit, v.usesSampleType, v.requiresCreatinine, v.speciationOptions
         FROM (VALUES ('PB', 'wholeBlood', N'ug/dL', 1, 0, NULL),
                      ('HG', 'wholeBlood', N'ug/L',  0, 0, 'total,methyl'),
                      ('HG', 'urine',      N'ug/L',  0, 0, NULL),
                      ('CD', 'wholeBlood', N'ug/L',  0, 0, NULL),
                      ('CD', 'urine',      N'ug/L',  0, 1, NULL),
                      ('AS', 'wholeBlood', N'ug/L',  0, 0, NULL),
                      ('AS', 'urine',      N'ug/L',  0, 0, 'total,inorganic,organic'))
              AS v (code, specimen, canonicalUnit, usesSampleType, requiresCreatinine, speciationOptions)
         JOIN config.analyte AS a ON a.code = v.code) AS s
   ON t.analyteSpecimenId = s.analyteSpecimenId
WHEN MATCHED AND (t.canonicalUnit <> s.canonicalUnit OR t.usesSampleType <> s.usesSampleType
                  OR t.requiresCreatinine <> s.requiresCreatinine
                  OR t.speciationOptions IS DISTINCT FROM s.speciationOptions) THEN
    UPDATE SET canonicalUnit = s.canonicalUnit, usesSampleType = s.usesSampleType,
               requiresCreatinine = s.requiresCreatinine, speciationOptions = s.speciationOptions
WHEN NOT MATCHED THEN
    INSERT (analyteSpecimenId, analyteId, specimen, canonicalUnit, usesSampleType, requiresCreatinine, speciationOptions)
    VALUES (s.analyteSpecimenId, s.analyteId, s.specimen, s.canonicalUnit, s.usesSampleType, s.requiresCreatinine,
            s.speciationOptions);
GO

MERGE config.acceptedUnit AS t
USING (SELECT CAST (HASHBYTES ('MD5', N'unit:' + v.code + N':' + v.specimen + N':' + v.unitText) AS UNIQUEIDENTIFIER) AS acceptedUnitId,
              CAST (HASHBYTES ('MD5', N'analyteSpecimen:' + v.code + N':' + v.specimen) AS UNIQUEIDENTIFIER) AS analyteSpecimenId,
              v.unitText, v.multiplier, v.sortOrder
         FROM (VALUES ('PB', 'wholeBlood', N'ug/dL', 1.0, 0), ('PB', 'wholeBlood', N'mcg/dL', 1.0, 1),
                      ('PB', 'wholeBlood', N'µg/dL', 1.0, 2), ('PB', 'wholeBlood', N'ug/L', 0.1, 3),
                      ('PB', 'wholeBlood', N'umol/L', 20.7, 4),
                      ('HG', 'wholeBlood', N'ug/L', 1.0, 0), ('HG', 'wholeBlood', N'mcg/L', 1.0, 1),
                      ('HG', 'wholeBlood', N'µg/L', 1.0, 2), ('HG', 'wholeBlood', N'nmol/L', 0.2006, 3),
                      ('HG', 'urine', N'ug/L', 1.0, 0), ('HG', 'urine', N'mcg/L', 1.0, 1),
                      ('CD', 'wholeBlood', N'ug/L', 1.0, 0), ('CD', 'wholeBlood', N'mcg/L', 1.0, 1),
                      ('CD', 'wholeBlood', N'nmol/L', 0.1124, 2),
                      ('CD', 'urine', N'ug/L', 1.0, 0), ('CD', 'urine', N'mcg/L', 1.0, 1),
                      ('AS', 'wholeBlood', N'ug/L', 1.0, 0), ('AS', 'wholeBlood', N'mcg/L', 1.0, 1),
                      ('AS', 'urine', N'ug/L', 1.0, 0), ('AS', 'urine', N'mcg/L', 1.0, 1))
              AS v (code, specimen, unitText, multiplier, sortOrder)) AS s
   ON t.acceptedUnitId = s.acceptedUnitId
WHEN MATCHED AND (t.multiplierToCanonical <> s.multiplier OR t.sortOrder <> s.sortOrder) THEN
    UPDATE SET multiplierToCanonical = s.multiplier, sortOrder = s.sortOrder
WHEN NOT MATCHED THEN
    INSERT (acceptedUnitId, analyteSpecimenId, unitText, multiplierToCanonical, sortOrder)
    VALUES (s.acceptedUnitId, s.analyteSpecimenId, s.unitText, s.multiplier, s.sortOrder);
GO

MERGE config.loincCode AS t
USING (SELECT CAST (HASHBYTES ('MD5', N'loinc:' + v.loinc) AS UNIQUEIDENTIFIER) AS loincCodeId,
              CAST (HASHBYTES ('MD5', N'analyteSpecimen:' + v.code + N':' + v.specimen) AS UNIQUEIDENTIFIER) AS analyteSpecimenId,
              v.loinc, v.sampleType, v.speciation, v.discouraged, v.description
         FROM (VALUES ('PB', 'wholeBlood', '77307-7', 'venous',    NULL,        0, N'Lead [Mass/volume] in Venous blood'),
                      ('PB', 'wholeBlood', '10368-9', 'capillary', NULL,        0, N'Lead [Mass/volume] in Capillary blood'),
                      ('PB', 'wholeBlood', '5671-3',  NULL,        NULL,        1, N'Lead [Mass/volume] in Blood (discouraged; sample type unknown)'),
                      ('HG', 'wholeBlood', '5685-3',  NULL,        'total',     0, N'Mercury [Mass/volume] in Blood'),
                      ('HG', 'urine',      '5689-5',  NULL,        NULL,        0, N'Mercury [Mass/volume] in Urine'),
                      ('CD', 'wholeBlood', '5609-3',  NULL,        NULL,        0, N'Cadmium [Mass/volume] in Blood'),
                      ('CD', 'urine',      '5611-9',  NULL,        NULL,        0, N'Cadmium [Mass/volume] in Urine'),
                      ('AS', 'wholeBlood', '5585-5',  NULL,        NULL,        0, N'Arsenic [Mass/volume] in Blood'),
                      ('AS', 'urine',      '5586-3',  NULL,        'total',     0, N'Arsenic [Mass/volume] in Urine'))
              AS v (code, specimen, loinc, sampleType, speciation, discouraged, description)) AS s
   ON t.loincCodeId = s.loincCodeId
WHEN MATCHED AND (t.impliedSampleType IS DISTINCT FROM s.sampleType OR t.impliedSpeciation IS DISTINCT FROM s.speciation
                  OR t.isDiscouraged <> s.discouraged OR t.loincDescription IS DISTINCT FROM s.description) THEN
    UPDATE SET impliedSampleType = s.sampleType, impliedSpeciation = s.speciation, isDiscouraged = s.discouraged,
               loincDescription = s.description
WHEN NOT MATCHED THEN
    INSERT (loincCodeId, analyteSpecimenId, loincCode, impliedSampleType, impliedSpeciation, isDiscouraged, loincDescription)
    VALUES (s.loincCodeId, s.analyteSpecimenId, s.loinc, s.sampleType, s.speciation, s.discouraged, s.description);
GO

MERGE config.actionCode AS t
USING (VALUES ('confirmatoryVenousDue', N'Confirmatory venous test due', 'confirmatoryTest'),
              ('retestDue',             N'Follow-up test due',           'confirmatoryTest'),
              ('openInvestigation',     N'Open environmental investigation', 'investigationRequest'),
              ('speciationRecommended', N'Speciation recommended',       'speciationRecommended'),
              ('medicalEvaluation',     N'Urgent medical evaluation',    'actionLevel')) AS s (code, displayName, queueCode)
   ON t.code = s.code AND t.isDeleted = 0
WHEN MATCHED AND (t.displayName <> s.displayName OR t.queueCode <> s.queueCode) THEN
    UPDATE SET displayName = s.displayName, queueCode = s.queueCode
WHEN NOT MATCHED THEN
    INSERT (actionCodeId, code, displayName, queueCode)
    VALUES (CAST (HASHBYTES ('MD5', N'action:' + s.code) AS UNIQUEIDENTIFIER), s.code, s.displayName, s.queueCode);
GO

-- ------------------------------------------------------------------------------------------------ extract layouts
-- Field order, widths and sources are configuration, so a federal layout change is a data update. These layouts follow
-- the field lists in the MERIT specification; confirm widths against the current CDC and NIOSH documents before the
-- first submission.
MERGE config.extractLayout AS t
USING (VALUES ('CBLS', '2024.1', 'fixedWidth', NULL, '2024-01-01'),
              ('ABLES', '2024.1', 'delimited', ',', '2024-01-01')) AS s (extractType, layoutVersion, fileFormat, delimiter, effectiveFrom)
   ON t.extractType = s.extractType AND t.layoutVersion = s.layoutVersion AND t.isDeleted = 0
WHEN NOT MATCHED THEN
    INSERT (extractLayoutId, extractType, layoutVersion, fileFormat, delimiter, effectiveFrom)
    VALUES (CAST (HASHBYTES ('MD5', N'layout:' + s.extractType + N':' + s.layoutVersion) AS UNIQUEIDENTIFIER),
            s.extractType, s.layoutVersion, s.fileFormat, s.delimiter, s.effectiveFrom);
GO

MERGE config.extractLayoutField AS t
USING (SELECT CAST (HASHBYTES ('MD5', N'layoutField:' + v.extractType + N':' + CAST (v.fieldOrder AS NVARCHAR (4))) AS UNIQUEIDENTIFIER) AS id,
              CAST (HASHBYTES ('MD5', N'layout:' + v.extractType + N':2024.1') AS UNIQUEIDENTIFIER) AS extractLayoutId,
              v.fieldOrder, v.fieldName, v.fieldWidth, v.sourceKey, v.padWith, v.alignRight, v.isRequired
         FROM (VALUES ('CBLS', 1,  'STATE',    2,  'stateFips',        '0', 1, 1),
                      ('CBLS', 2,  'CHILDID',  15, 'surrogateId',      ' ', 0, 1),
                      ('CBLS', 3,  'DOB',      8,  'dob',              ' ', 0, 1),
                      ('CBLS', 4,  'SEX',      1,  'sexCode',          ' ', 0, 1),
                      ('CBLS', 5,  'RACE',     1,  'raceCode',         ' ', 0, 0),
                      ('CBLS', 6,  'ETHNIC',   1,  'ethnicityCode',    ' ', 0, 0),
                      ('CBLS', 7,  'ZIP',      5,  'zip5',             ' ', 0, 0),
                      ('CBLS', 8,  'COUNTY',   3,  'countyFips',       '0', 1, 1),
                      ('CBLS', 9,  'TRACT',    11, 'censusTract',      ' ', 0, 0),
                      ('CBLS', 10, 'SAMPDATE', 8,  'collectedDate',    ' ', 0, 1),
                      ('CBLS', 11, 'SAMPTYPE', 1,  'sampleTypeCode',   ' ', 0, 1),
                      ('CBLS', 12, 'BLLSIGN',  1,  'operator',         ' ', 0, 0),
                      ('CBLS', 13, 'BLL',      5,  'bllOneDecimal',    '0', 1, 1),
                      ('CBLS', 14, 'METHOD',   1,  'testMethodCode',   ' ', 0, 0),
                      ('CBLS', 15, 'LABCLIA',  10, 'labClia',          ' ', 0, 0),
                      ('CBLS', 16, 'PAYER',    1,  'payerCode',        ' ', 0, 0),
                      ('CBLS', 17, 'YRBUILT',  4,  'yearBuilt',        ' ', 0, 0),
                      ('ABLES', 1,  'StateRep',  2,  'statePostalCode', ' ', 0, 1),
                      ('ABLES', 2,  'StateRes',  2,  'stateRes',        ' ', 0, 1),
                      ('ABLES', 3,  'CountyRes', 3,  'countyRes',       ' ', 0, 1),
                      ('ABLES', 4,  'StateExp',  2,  'stateExp',        ' ', 0, 0),
                      ('ABLES', 5,  'CountyExp', 3,  'countyExp',       ' ', 0, 0),
                      ('ABLES', 6,  'ID',        15, 'surrogateId',     ' ', 0, 1),
                      ('ABLES', 7,  'Status',    1,  'ablesStatus',     ' ', 0, 1),
                      ('ABLES', 8,  'BLLDate',   8,  'collectedDate',   ' ', 0, 1),
                      ('ABLES', 9,  'DateType',  1,  'dateType',        ' ', 0, 1),
                      ('ABLES', 10, 'BLL',       3,  'bllWhole',        ' ', 0, 1),
                      ('ABLES', 11, 'DOB',       8,  'dob',             ' ', 0, 0),
                      ('ABLES', 12, 'Age',       3,  'ageYears',        ' ', 0, 0),
                      ('ABLES', 13, 'Sex',       1,  'sexCode',         ' ', 0, 1),
                      ('ABLES', 14, 'Ethnicity', 1,  'ethnicityCode',   ' ', 0, 0),
                      ('ABLES', 15, 'Race1',     1,  'race1',           ' ', 0, 0),
                      ('ABLES', 16, 'Race2',     1,  'race2',           ' ', 0, 0),
                      ('ABLES', 17, 'Race3',     1,  'race3',           ' ', 0, 0),
                      ('ABLES', 18, 'Race4',     1,  'race4',           ' ', 0, 0),
                      ('ABLES', 19, 'Race5',     1,  'race5',           ' ', 0, 0),
                      ('ABLES', 20, 'Race6',     1,  'race6',           ' ', 0, 0),
                      ('ABLES', 21, 'Race7',     1,  'race7',           ' ', 0, 0),
                      ('ABLES', 22, 'Race8',     1,  'race8',           ' ', 0, 0),
                      ('ABLES', 23, 'WorkRel',   1,  'workRel',         ' ', 0, 1),
                      ('ABLES', 24, 'NAICS',     6,  'naics',           ' ', 0, 0),
                      ('ABLES', 25, 'COC',       4,  'coc',             ' ', 0, 0),
                      ('ABLES', 26, 'Process',   200,'process',         ' ', 0, 0))
              AS v (extractType, fieldOrder, fieldName, fieldWidth, sourceKey, padWith, alignRight, isRequired)) AS s
   ON t.extractLayoutFieldId = s.id
WHEN MATCHED AND (t.fieldName <> s.fieldName OR t.fieldWidth <> s.fieldWidth OR t.sourceKey <> s.sourceKey
                  OR t.padWith <> s.padWith OR t.alignRight <> s.alignRight OR t.isRequired <> s.isRequired) THEN
    UPDATE SET fieldName = s.fieldName, fieldWidth = s.fieldWidth, sourceKey = s.sourceKey, padWith = s.padWith,
               alignRight = s.alignRight, isRequired = s.isRequired
WHEN NOT MATCHED THEN
    INSERT (extractLayoutFieldId, extractLayoutId, fieldOrder, fieldName, fieldWidth, sourceKey, padWith, alignRight,
            isRequired)
    VALUES (s.id, s.extractLayoutId, s.fieldOrder, s.fieldName, s.fieldWidth, s.sourceKey, s.padWith, s.alignRight,
            s.isRequired);
GO
