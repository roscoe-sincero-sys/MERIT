SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName: config.udfReceiptField
Author: MERIT build
CreateDate: 2026-11-08
========================================================================================================================
Description:
Reads a canonical mapped intake field. An explicit correction, including blank/null, overrides the original mapped value.
========================================================================================================================
Requirements and Key Dependencies:
SQL Server 2022 OPENJSON. The rule writer validates days, dates and calendar approval.
========================================================================================================================
Notes:
Returns trimmed scalar text only; never executes a supplied path.
========================================================================================================================
Example Usage and Performance:
SELECT config.udfReceiptField (N'{}', N'{}', 'dob');
Bounded daily iteration over one rule's explicit dates.
========================================================================================================================
Modification History:
Date: 2026-11-08
Author: MERIT build
Ticket: 202
Description: Read a mapped receipt field while preserving explicit corrections.
***********************************************************************************************************************/
CREATE OR ALTER FUNCTION config.udfReceiptField (@original NVARCHAR (MAX), @patch NVARCHAR (MAX), @field VARCHAR (60))
RETURNS NVARCHAR (4000)
AS
BEGIN
    IF @field IS NULL OR @field LIKE '%[^a-zA-Z0-9_]%' RETURN NULL;
    DECLARE @value NVARCHAR (4000);
    IF EXISTS (SELECT 1 FROM OPENJSON (COALESCE (@patch,N'{}')) WHERE [key]=@field)
        SET @value = JSON_VALUE (@patch,CONCAT ('$.',@field));
    ELSE
    BEGIN
        DECLARE @alias VARCHAR(60)=CASE @field WHEN 'zip' THEN 'zip5' WHEN 'county' THEN 'countyFips' WHEN 'clia' THEN 'labCliaNumber' ELSE @field END;
        IF EXISTS(SELECT 1 FROM OPENJSON(COALESCE(@patch,N'{}')) WHERE [key]=@alias)
            SET @value=JSON_VALUE(@patch,CONCAT('$.',@alias));
        ELSE SET @value=COALESCE(JSON_VALUE(@original,CONCAT('$.',@field)),JSON_VALUE(@original,CONCAT('$.',@alias)));
    END;
    RETURN NULLIF (TRIM (@value),N'');
END;
GO
EXEC util.uspSetObjectDescription @SchemaName=N'config', @ObjectType=N'FUNCTION', @ObjectName=N'udfReceiptField',
 @Description=N'Mapped intake field with explicit correction precedence; null/blank corrections do not fall back to original values.';
GO
