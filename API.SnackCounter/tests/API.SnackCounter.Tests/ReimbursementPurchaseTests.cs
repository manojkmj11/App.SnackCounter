using System.Security.Claims;
using API.SnackCounter.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Tests;

[TestFixture]
public sealed class ReimbursementPurchaseTests
{
    private const string EmployeeId = "employee-1";

    [Test]
    public async Task SamePurchaseRequestIsIdempotentWhileClaimIsActive()
    {
        var purchaseId = Guid.NewGuid();
        var (factory, controller) = await CreateController(purchaseId);
        var request = Request(purchaseId);
        Assert.That((await controller.SubmitClaim(request, CancellationToken.None)).Result,
            Is.TypeOf<CreatedAtActionResult>());

        var duplicate = await controller.SubmitClaim(request, CancellationToken.None);

        Assert.That(duplicate.Result, Is.TypeOf<OkObjectResult>());
        await using var db = await factory.CreateDbContextAsync();
        Assert.That(await db.ReimbursementClaims.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task DeniedPurchaseCanBeResubmittedAsNewAttemptButReimbursedPurchaseCannot()
    {
        var purchaseId = Guid.NewGuid();
        var (factory, controller) = await CreateController(purchaseId);
        await controller.SubmitClaim(Request(purchaseId), CancellationToken.None);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var first = await db.ReimbursementClaims.SingleAsync();
            first.Status = ReimbursementClaimStatus.Denied;
            await db.SaveChangesAsync();
        }

        var resubmission = await controller.SubmitClaim(Request(purchaseId), CancellationToken.None);
        Assert.That(resubmission.Result, Is.TypeOf<CreatedAtActionResult>());
        var second = (ClaimResponse)((CreatedAtActionResult)resubmission.Result!).Value!;
        Assert.Multiple(() =>
        {
            Assert.That(second.PurchaseId, Is.EqualTo(purchaseId));
            Assert.That(second.SubmissionAttempt, Is.EqualTo(2));
        });

        await using (var db = await factory.CreateDbContextAsync())
        {
            var secondRow = await db.ReimbursementClaims.SingleAsync(x => x.Id == second.Id);
            Assert.That(secondRow.PreviousClaimId, Is.Not.Null);
            secondRow.Status = ReimbursementClaimStatus.Reimbursed;
            await db.SaveChangesAsync();
        }
        var third = await controller.SubmitClaim(Request(purchaseId), CancellationToken.None);
        Assert.That(third.Result, Is.TypeOf<OkObjectResult>());
        Assert.That(((ClaimResponse)((OkObjectResult)third.Result!).Value!).Id, Is.EqualTo(second.Id));

        var purchases = await controller.MyPurchases(CancellationToken.None);
        Assert.That(purchases.Result, Is.TypeOf<OkObjectResult>());
        var listed = (IReadOnlyList<EmployeePurchaseResponse>)((OkObjectResult)purchases.Result!).Value!;
        Assert.Multiple(() =>
        {
            Assert.That(listed, Has.Count.EqualTo(1));
            Assert.That(listed[0].ClaimStatus, Is.EqualTo(ReimbursementClaimStatus.Reimbursed));
            Assert.That(listed[0].SubmissionAttempt, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ClaimRejectsAKeyThatDoesNotMatchThePurchaseId()
    {
        var purchaseId = Guid.NewGuid();
        var (_, controller) = await CreateController(purchaseId);

        var result = await controller.SubmitClaim(Request(purchaseId) with { IdempotencyKey = Guid.NewGuid().ToString("D") }, CancellationToken.None);

        Assert.That(result.Result, Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task ReceiptEndpointReturnsInlineImageContentWithoutDownloadFilename()
    {
        var purchaseId = Guid.NewGuid();
        var (factory, controller) = await CreateController(purchaseId);
        var claimId = Guid.NewGuid();
        var receipt = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ReimbursementClaims.Add(new ReimbursementClaim
            {
                Id = claimId,
                EmployeeId = EmployeeId,
                Office = OfficeLocation.Oslo,
                PurchaseId = purchaseId,
                RefillId = purchaseId,
                SubmissionAttempt = 1,
                IdempotencyKey = purchaseId.ToString("D"),
                Amount = 120m,
                Currency = Currency.NOK,
                Description = "Office snacks",
                Status = ReimbursementClaimStatus.Submitted,
                SubmittedAt = DateTimeOffset.UtcNow,
                ReceiptContent = receipt,
                ReceiptMediaType = "image/jpeg",
                ReceiptSizeBytes = receipt.Length
            });
            await db.SaveChangesAsync();
        }

        var result = await controller.ClaimReceipt(claimId, CancellationToken.None);

        Assert.That(result, Is.TypeOf<FileContentResult>());
        var file = (FileContentResult)result;
        Assert.Multiple(() =>
        {
            Assert.That(file.ContentType, Is.EqualTo("image/jpeg"));
            Assert.That(file.FileDownloadName, Is.Empty);
            Assert.That(file.FileContents, Is.EqualTo(receipt));
        });
    }

    private static CreateClaimRequest Request(Guid purchaseId) =>
        new(purchaseId.ToString("D"), purchaseId, OfficeLocation.Oslo, 120m, Currency.NOK, "Office snacks", purchaseId);

    private static async Task<(TestDbContextFactory Factory, PeopleOperationsController Controller)> CreateController(Guid purchaseId)
    {
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"claims-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var employee = new Volunteer(EmployeeId, OfficeLocation.Oslo, Currency.NOK);
            var item = new SnackItem
            {
                Id = Guid.NewGuid(), Office = OfficeLocation.Oslo, Name = "Coffee", Unit = "bag",
                QuantityOnHand = 5m, ReorderThreshold = 1m
            };
            db.Volunteers.Add(employee);
            db.SnackItems.Add(item);
            db.Refills.Add(new Refill(purchaseId, OfficeLocation.Oslo, "Coffee", DateTimeOffset.UtcNow)
            {
                AssignedVolunteer = employee,
                Status = RefillStatus.Purchased,
                PurchaseAmount = 120m,
                PurchaseCurrency = Currency.NOK,
                PurchasedAt = DateTimeOffset.UtcNow,
                PurchaseDescription = "Office snacks"
            });
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(), SnackItem = item, SnackItemId = item.Id, Kind = StockMovementKind.Restock,
                Quantity = 5m, OccurredAt = DateTimeOffset.UtcNow, ActorEmployeeId = EmployeeId, RefillId = purchaseId,
                UnitCost = 24m, CostCurrency = Currency.NOK, Note = "Office snacks"
            });
            await db.SaveChangesAsync();
        }
        var controller = new PeopleOperationsController(factory, TimeProvider.System, new ConfigurationBuilder().Build(), new TestExternalHrClient());
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, EmployeeId), new Claim(ClaimTypes.Role, "Employee")], "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return (factory, controller);
    }

    private sealed class TestDbContextFactory(DbContextOptions<SnackCounterDbContext> options)
        : IDbContextFactory<SnackCounterDbContext>
    {
        public SnackCounterDbContext CreateDbContext() => new(options);
        public Task<SnackCounterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SnackCounterDbContext(options));
    }

    private sealed class TestExternalHrClient : IExternalHrClaimClient
    {
        public Task SubmitClaimAsync(Guid claimId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DecideClaimAsync(Guid claimId, bool approve, string? reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
