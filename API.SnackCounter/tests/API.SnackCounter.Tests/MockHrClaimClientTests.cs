using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Tests;

[TestFixture]
public sealed class MockHrClaimClientTests
{
    [Test]
    public async Task SubmitClaim_UsesClaimIdAsMockHrIdempotencyKey()
    {
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"mock-hr-submit-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        var claimId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var externalClaimId = Guid.NewGuid();
        var submittedAt = DateTimeOffset.UtcNow;

        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Volunteers.Add(new Volunteer("employee-1", OfficeLocation.Oslo, Currency.NOK));
            setup.ReimbursementClaims.Add(new ReimbursementClaim
            {
                Id = claimId,
                EmployeeId = "employee-1",
                Office = OfficeLocation.Oslo,
                PurchaseId = purchaseId,
                RefillId = purchaseId,
                IdempotencyKey = purchaseId.ToString("D"),
                Amount = 120m,
                Currency = Currency.NOK,
                Description = "Office snacks",
                Status = ReimbursementClaimStatus.Submitted,
                SubmittedAt = submittedAt
            });
            await setup.SaveChangesAsync();
        }

        var handler = new CaptureSubmissionHandler(externalClaimId, submittedAt);
        var clients = new TestHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://mock-hr/")
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MockHr:ServiceKey"] = "test-service-key"
        }).Build();
        var client = new MockHrClaimClient(clients, factory, configuration);

        await client.SubmitClaimAsync(claimId, CancellationToken.None);

        using var request = JsonDocument.Parse(handler.RequestJson!);
        Assert.Multiple(() =>
        {
            Assert.That(handler.RequestUri, Is.EqualTo("http://mock-hr/api/claims"));
            Assert.That(handler.ServiceKey, Is.EqualTo("test-service-key"));
            Assert.That(request.RootElement.GetProperty("claimId").GetGuid(), Is.EqualTo(claimId));
            Assert.That(request.RootElement.GetProperty("idempotencyKey").GetString(), Is.EqualTo(claimId.ToString("D")));
        });

        await using var verify = await factory.CreateDbContextAsync();
        var savedClaim = await verify.ReimbursementClaims.SingleAsync(x => x.Id == claimId);
        Assert.Multiple(() =>
        {
            Assert.That(savedClaim.IdempotencyKey, Is.EqualTo(purchaseId.ToString("D")),
                "SnackCounter keeps purchase-scoped idempotency for employee resubmissions.");
            Assert.That(savedClaim.ExternalHrClaimId, Is.EqualTo(externalClaimId));
            Assert.That(savedClaim.ExternalHrSubmittedAt, Is.EqualTo(submittedAt));
        });
    }

    private sealed class CaptureSubmissionHandler(Guid externalClaimId, DateTimeOffset submittedAt) : HttpMessageHandler
    {
        public string? RequestJson { get; private set; }
        public string? RequestUri { get; private set; }
        public string? ServiceKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            ServiceKey = request.Headers.GetValues("X-Mock-HR-Key").Single();
            RequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { id = externalClaimId, submittedAt })
            };
        }
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestDbContextFactory(DbContextOptions<SnackCounterDbContext> options)
        : IDbContextFactory<SnackCounterDbContext>
    {
        public SnackCounterDbContext CreateDbContext() => new(options);
        public Task<SnackCounterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SnackCounterDbContext(options));
    }
}
