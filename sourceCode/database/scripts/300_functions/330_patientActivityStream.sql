SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName: dbo.vwPatientActivityStream
Author: MERIT build
CreateDate: 2026-11-08
========================================================================================================================
Description:
One canonical stream of existing case events and new patient events. Linking a patient event changes its association,
not its identifier, event date or original provenance. Case-related events require visible case access.
========================================================================================================================
Requirements and Key Dependencies:
dbo.caseActivity, personActivity, exposureCase and their row-level security predicates; auth.udfHasPermission.
========================================================================================================================
Notes:
UNION ALL joins disjoint identifiers enforced by the patient activity writer. No copy or migration of legacy evidence.
========================================================================================================================
Example Usage and Performance:
SELECT * FROM dbo.vwPatientActivityStream WHERE personId = @personId;
Underlying patient/case indexes support scoped histories.
========================================================================================================================
Modification History:
Date: 2026-11-08
Author: MERIT build
Ticket: 203
Description: Add patient-only history while retaining existing case provenance.
***********************************************************************************************************************/
CREATE OR ALTER VIEW dbo.vwPatientActivityStream
AS
SELECT a.caseActivityId, c.personId, a.exposureCaseId, CAST (NULL AS UNIQUEIDENTIFIER) AS labResultId,
       a.rootTenantId, a.owningTenantId, a.activityType, a.activityDate, a.outcome, a.note, a.workerUserId,
       a.contactPersonId, a.createdDateUtc, a.isDeleted, CAST (0 AS BIT) AS isVoided,
       CAST (CASE WHEN a.activityType IN ('note','statusChange','classificationChange') THEN 'internal' WHEN a.contactPersonId IS NOT NULL THEN 'other' ELSE 'unknown' END AS VARCHAR (12)) AS audience,
       CAST (NULL AS VARCHAR (12)) AS channel, CAST (NULL AS NVARCHAR (200)) AS recipientLabel,
       CAST ('case' AS VARCHAR (12)) AS sourceKind, CAST (NULL AS NVARCHAR (500)) AS voidReason
  FROM dbo.caseActivity AS a JOIN dbo.exposureCase AS c ON c.exposureCaseId = a.exposureCaseId AND c.isDeleted = 0
 WHERE a.isDeleted = 0 AND auth.udfHasPermission (N'case.view') = 1
UNION ALL
SELECT a.personActivityId, a.personId, a.exposureCaseId, a.labResultId, a.rootTenantId, a.owningTenantId,
       a.activityType, a.activityDate, a.outcome, a.note, a.workerUserId, a.contactPersonId, a.createdDateUtc,
       a.isDeleted, a.isVoided, a.audience, a.channel, a.recipientLabel, 'patient', a.voidReason
  FROM dbo.personActivity AS a
 WHERE a.isDeleted = 0 AND (a.exposureCaseId IS NULL OR (auth.udfHasPermission (N'case.view') = 1 AND EXISTS
       (SELECT 1 FROM dbo.exposureCase AS c WHERE c.exposureCaseId = a.exposureCaseId AND c.isDeleted = 0)));
GO
EXEC util.uspSetObjectDescription @SchemaName = N'dbo', @ObjectType = N'VIEW', @ObjectName = N'vwPatientActivityStream',
     @Description = N'Canonical patient and legacy case activity, without copying events. Case associations require case visibility; underlying RLS scopes every row.';
GO
