SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName: dbo.udfCaseClosureReviewStamp
Author: MERIT build
CreateDate: 2026-11-08
Description:
Hashes the current open task identifiers and row versions so closure cannot silently dismiss newly changed work.
Requirements and Key Dependencies:
Scoped patient/case/task tables and auth.udfSessionToday; no dynamic SQL or elevated execution context.
Modification History:
2026-11-08 MERIT build - Program task definitions and explicit case-closure review (#205-207).
***********************************************************************************************************************/
CREATE OR ALTER FUNCTION dbo.udfCaseClosureReviewStamp(@exposureCaseId UNIQUEIDENTIFIER)
RETURNS VARCHAR(64)
AS
BEGIN
 DECLARE @items NVARCHAR(MAX)=(SELECT STRING_AGG(CONVERT(NVARCHAR(MAX),CONCAT(CONVERT(VARCHAR(36),workTaskId),':',CONVERT(VARCHAR(16),CAST(rowVersion AS BINARY(8)),2))),N'|') WITHIN GROUP(ORDER BY workTaskId)
 FROM dbo.workTask WHERE exposureCaseId=@exposureCaseId AND taskStatus='open' AND isDeleted=0);
 RETURN CONVERT(VARCHAR(64),HASHBYTES('SHA2_256',COALESCE(@items,N'')),2);
END;
GO
