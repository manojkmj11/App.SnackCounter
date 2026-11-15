using SnackCounter.Domain;

namespace API.SnackCounter.Controllers;

public sealed record OfficeResponse(
    OfficeLocation Office,
    string Name,
    string TimeZoneId,
    Currency BudgetCurrency,
    decimal MonthlyBudget,
    decimal WarningThresholdPercent);

public sealed record RefillResponse(
    Guid Id,
    OfficeLocation Office,
    string Item,
    RefillStatus Status,
    string? AssignedEmployeeId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AssignmentExpiresAt,
    decimal? PurchaseAmount,
    Currency? PurchaseCurrency,
    DateTimeOffset? PurchasedAt,
    string? PurchaseDescription,
    bool HasReceipt,
    string? HrClaimId,
    long Version,
    string? RequesterEmployeeId,
    Guid? SnackItemId,
    string RowVersion)
{
    public static RefillResponse From(Refill refill) => new(
        refill.Id,
        refill.Office,
        refill.Item,
        refill.Status,
        refill.AssignedVolunteer?.EmployeeId,
        refill.CreatedAt,
        refill.AssignmentExpiresAt,
        refill.PurchaseAmount,
        refill.PurchaseCurrency,
        refill.PurchasedAt,
        refill.PurchaseDescription,
        refill.ReceiptImage is { Length: > 0 },
        refill.HrClaimId,
        refill.Version,
        refill.RequesterEmployeeId,
        refill.SnackItemId,
        Convert.ToBase64String(refill.RowVersion));
}
