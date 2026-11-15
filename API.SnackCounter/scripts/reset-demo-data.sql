/*
  Clear SnackCounter demo/operational data while keeping employee accounts,
  office definitions, role definitions, and employee office/role assignments.

  Stop API.SnackCounter and MockHr.Service before running this script.
  This script is destructive. It does not remove __EFMigrationsHistory.
*/
USE [SnackCounter];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'SnackCounter'
    THROW 51000, 'Run this reset only against the SnackCounter database.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    -- Remove dependent workflow rows before their referenced claims/messages.
    DELETE FROM dbo.ProcessedHrWebhooks;
    DELETE FROM dbo.Notifications;
    DELETE FROM dbo.OutboxMessages;
    DELETE FROM dbo.ReimbursementClaims;
    DELETE FROM dbo.ConversationMessages;
    DELETE FROM dbo.Conversations;

    -- Remove refill, inventory, budget, leave, and reporting state.
    DELETE FROM dbo.StockMovements;
    DELETE FROM dbo.RefillAssignmentAttempts;
    DELETE FROM dbo.RefillRotations;
    DELETE FROM dbo.EmployeeLeaves;
    DELETE FROM dbo.MonthlyReports;
    DELETE FROM dbo.MonthlyBudgets;
    DELETE FROM dbo.AuditRecords;
    DELETE FROM dbo.Refills;
    DELETE FROM dbo.SnackItems;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

-- Mock HR keeps its own claim and webhook tables in a separate database.
IF DB_ID(N'SnackCounterMockHr') IS NOT NULL
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;
        DELETE FROM [SnackCounterMockHr].dbo.WebhookOutbox;
        DELETE FROM [SnackCounterMockHr].dbo.Claims;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
