SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName: dbo.udfMarylandEblConfirmed
Author: MERIT build
CreateDate: 2026-11-08
========================================================================================================================
Description:
Checks scoped final/corrected blood-result evidence for an explicitly reviewed Maryland notification policy.
========================================================================================================================
Requirements and Key Dependencies:
SQL Server 2022 OPENJSON. The rule writer validates days, dates and calendar approval.
========================================================================================================================
Notes:
Uncertain or discordant capillary evidence remains unconfirmed; qualifying venous evidence can confirm it.
========================================================================================================================
Example Usage and Performance:
SELECT dbo.udfMarylandEblConfirmed (@labResultId, 3.5);
Bounded daily iteration over one rule's explicit dates.
========================================================================================================================
Modification History:
Date: 2026-11-08
Author: MERIT build
Ticket: 202
Description: Maryland EBL source confirmation for reviewed notices.
***********************************************************************************************************************/
CREATE OR ALTER FUNCTION dbo.udfMarylandEblConfirmed (@labResultId UNIQUEIDENTIFIER, @minimum DECIMAL (18,6))
RETURNS BIT
AS
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.labResult AS r WHERE r.labResultId=@labResultId AND r.isDeleted=0
      AND r.resultRowStatus='valid' AND r.resultStatus IN ('final','corrected') AND r.ageAtCollectionMonths IS NOT NULL
      AND r.operator IN ('=','>') AND r.valueCanonical>=@minimum
      AND (r.sampleType='venous' OR (r.sampleType='capillary' AND EXISTS
        (SELECT 1 FROM dbo.labResult AS p WHERE p.personId=r.personId AND p.analyteSpecimenId=r.analyteSpecimenId
          AND p.labResultId<>r.labResultId AND p.isDeleted=0 AND p.resultRowStatus='valid' AND p.resultStatus IN ('final','corrected')
          AND p.sampleType='capillary' AND p.operator IN ('=','>') AND p.valueCanonical>=@minimum
          AND p.collectedDate BETWEEN DATEADD(DAY,-84,r.collectedDate) AND r.collectedDate)
        AND NOT EXISTS (SELECT 1 FROM dbo.labResult AS d WHERE d.personId=r.personId AND d.analyteSpecimenId=r.analyteSpecimenId
          AND d.isDeleted=0 AND d.resultRowStatus='valid' AND d.resultStatus IN ('final','corrected') AND d.sampleType='capillary'
          AND d.collectedDate BETWEEN DATEADD(DAY,-84,r.collectedDate) AND r.collectedDate
          AND (d.valueCanonical IS NULL OR d.operator NOT IN ('=','>') OR d.valueCanonical<@minimum)))))
        RETURN 1;
    RETURN 0;
END;
GO
EXEC util.uspSetObjectDescription @SchemaName=N'dbo',@ObjectType=N'FUNCTION',@ObjectName=N'udfMarylandEblConfirmed',
 @Description=N'Scoped conservative Maryland EBL source confirmation: qualifying venous or two capillary tests within 84 days without discordant/uncertain capillary evidence. Does not replace legal recipient/property review.';
GO
