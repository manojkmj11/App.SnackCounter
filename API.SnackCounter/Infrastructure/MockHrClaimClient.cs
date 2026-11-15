using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public interface IExternalHrClaimClient
{
    Task SubmitClaimAsync(Guid claimId, CancellationToken cancellationToken);
    Task DecideClaimAsync(Guid claimId, bool approve, string? reason, CancellationToken cancellationToken);
}

public sealed class MockHrClaimClient(
    IHttpClientFactory clients,
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    IConfiguration configuration) : IExternalHrClaimClient
{
    public async Task SubmitClaimAsync(Guid claimId, CancellationToken cancellationToken)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await database.ReimbursementClaims.SingleOrDefaultAsync(x => x.Id == claimId, cancellationToken)
            ?? throw new InvalidOperationException($"Claim {claimId} no longer exists.");
        if (claim.ExternalHrClaimId is not null) return;
        if (claim.Status is not (ReimbursementClaimStatus.Submitted or ReimbursementClaimStatus.Processing)) return;

        var request = new MockHrSubmission(
            claim.Id, claim.Id.ToString("D"), claim.EmployeeId, claim.Office.ToString(), claim.Amount,
            claim.Currency.ToString(), claim.Description,
            claim.ReceiptContent is null ? null : Convert.ToBase64String(claim.ReceiptContent), claim.ReceiptMediaType);
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/claims") { Content = JsonContent.Create(request) };
        message.Headers.Add("X-Mock-HR-Key", configuration["MockHr:ServiceKey"] ??
            throw new InvalidOperationException("MockHr:ServiceKey is required."));
        using var response = await clients.CreateClient("MockHr").SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<MockHrSubmissionResult>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Mock HR returned an empty submission response.");

        claim.ExternalHrClaimId = result.ExternalClaimId;
        claim.ExternalHrSubmittedAt = result.SubmittedAt;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task DecideClaimAsync(Guid claimId, bool approve, string? reason, CancellationToken cancellationToken)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var externalClaimId = await database.ReimbursementClaims.AsNoTracking()
            .Where(x => x.Id == claimId)
            .Select(x => x.ExternalHrClaimId)
            .SingleOrDefaultAsync(cancellationToken);
        if (externalClaimId is null)
            throw new ExternalHrClaimPendingException("This claim is still being submitted to Mock HR. Refresh and try again shortly.");

        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"api/claims/{externalClaimId.Value:D}/decision")
        {
            Content = JsonContent.Create(new MockHrDecision(approve ? "Approved" : "Denied", reason))
        };
        message.Headers.Add("X-Mock-HR-Key", configuration["MockHr:ServiceKey"] ??
            throw new InvalidOperationException("MockHr:ServiceKey is required."));
        using var response = await clients.CreateClient("MockHr").SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private sealed record MockHrSubmission(Guid ClaimId, string IdempotencyKey, string EmployeeId, string Office,
        decimal Amount, string Currency, string Description, string? ReceiptBase64, string? ReceiptMediaType);
    private sealed record MockHrDecision(string Decision, string? Reason);
    private sealed record MockHrSubmissionResult(
        [property: JsonPropertyName("id")] Guid ExternalClaimId,
        [property: JsonPropertyName("submittedAt")] DateTimeOffset SubmittedAt);
}

public sealed class ExternalHrClaimPendingException(string message) : Exception(message);
