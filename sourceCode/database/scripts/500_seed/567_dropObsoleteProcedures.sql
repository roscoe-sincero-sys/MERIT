SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Issue #313: auth.uspContinueUsageSession was superseded when auth.uspCreateSessionScope began
-- carrying sessionReference forward atomically. Drop it on upgraded databases.
DROP PROCEDURE IF EXISTS auth.uspContinueUsageSession;
GO
