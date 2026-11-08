SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO
/***********************************************************************************************************************
ObjectName: config.udfNotificationDueDate
Author: MERIT build
CreateDate: 2026-11-08
========================================================================================================================
Description:
Calculates a program-approved deadline from its local receipt date, excluding the triggering day. Business days omit
Saturday, Sunday and explicit approved holiday dates; calendar days include them. No jurisdiction holiday list is inferred.
========================================================================================================================
Requirements and Key Dependencies:
SQL Server 2022 OPENJSON. The rule writer validates days, dates and calendar approval.
========================================================================================================================
Notes:
Weekday arithmetic is independent of DATEFIRST and session language. Invalid/unbounded calendars return NULL.
========================================================================================================================
Example Usage and Performance:
SELECT config.udfNotificationDueDate ('2026-11-06', 10, 'business', N'["2026-11-11"]');
Bounded daily iteration over one rule's explicit dates.
========================================================================================================================
Modification History:
Date: 2026-11-08
Author: MERIT build
Ticket: 202
Description: Receipt-based calendar/business day deadline calculation.
***********************************************************************************************************************/
CREATE OR ALTER FUNCTION config.udfNotificationDueDate (@anchor DATE, @days INT, @basis VARCHAR (12), @holidays NVARCHAR (MAX))
RETURNS DATE
AS
BEGIN
    IF @days NOT BETWEEN 1 AND 365 OR @basis NOT IN ('calendar','business') OR ISJSON (@holidays) <> 1 RETURN NULL;
    DECLARE @cursor DATE = @anchor, @remaining INT = @days, @steps INT = 0;
    WHILE @remaining > 0 AND @steps < 5000
    BEGIN
        SET @cursor = DATEADD (DAY, 1, @cursor);
        SET @steps += 1;
        IF @basis = 'calendar' OR (((DATEDIFF (DAY, '19000101', @cursor) % 7) + 7) % 7 < 5
            AND NOT EXISTS (SELECT 1 FROM OPENJSON (@holidays) WHERE TRY_CONVERT (DATE, [value]) = @cursor))
            SET @remaining -= 1;
    END;
    RETURN CASE WHEN @remaining = 0 THEN @cursor END;
END;
GO
EXEC util.uspSetObjectDescription @SchemaName = N'config', @ObjectType = N'FUNCTION', @ObjectName = N'udfNotificationDueDate',
     @Description = N'Explicit policy receipt-date deadline: trigger day excluded, business weekends and approved holidays omitted, independent of DATEFIRST. Not a statutory calendar.';
GO
