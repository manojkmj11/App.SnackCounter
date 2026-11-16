using System.Security.Claims;
using API.SnackCounter.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Tests;

[TestFixture]
public sealed class EmployeeOfficeVisitsTests
{
    private const string EmployeeId = "blr-visitor";

    [Test]
    public async Task ScheduledVisitGrantsDestinationAccessOnlyWithinItsLocalDateRange()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 15, 22, 30, 0, TimeSpan.Zero));
        var factory = await CreateFactory();
        var people = CreatePeopleController(factory, clock);

        var result = await people.ScheduleOfficeVisit(
            new ScheduleOfficeVisitRequest(OfficeLocation.Oslo, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 29)),
            CancellationToken.None);

        Assert.That(result.Result, Is.TypeOf<CreatedAtActionResult>());
        var offices = CreateOfficesController(factory, clock);
        var before = await offices.List(CancellationToken.None);
        Assert.That(((IReadOnlyList<OfficeResponse>)((OkObjectResult)before.Result!).Value!).Select(x => x.Office),
            Is.EquivalentTo([OfficeLocation.Bengaluru]));

        clock.Set(new DateTimeOffset(2026, 11, 16, 0, 30, 0, TimeSpan.Zero));
        var during = await offices.List(CancellationToken.None);
        Assert.That(((IReadOnlyList<OfficeResponse>)((OkObjectResult)during.Result!).Value!).Select(x => x.Office),
            Is.EquivalentTo([OfficeLocation.Bengaluru, OfficeLocation.Oslo]));

        clock.Set(new DateTimeOffset(2026, 11, 29, 23, 30, 0, TimeSpan.Zero));
        var after = await offices.List(CancellationToken.None);
        Assert.That(((IReadOnlyList<OfficeResponse>)((OkObjectResult)after.Result!).Value!).Select(x => x.Office),
            Is.EquivalentTo([OfficeLocation.Bengaluru]));
    }

    [Test]
    public async Task OfficeVisitsRejectOverlapsAndDurationsLongerThanTwoWeeks()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
        var factory = await CreateFactory();
        var people = CreatePeopleController(factory, clock);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.EmployeeOfficeVisits.Add(new EmployeeOfficeVisit
            {
                Id = Guid.NewGuid(), EmployeeId = EmployeeId, Office = OfficeLocation.Oslo,
                StartDate = new DateOnly(2026, 11, 20), EndDate = new DateOnly(2026, 11, 26),
                CreatedAt = clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }

        var overlap = await people.ScheduleOfficeVisit(
            new ScheduleOfficeVisitRequest(OfficeLocation.Oslo, new DateOnly(2026, 11, 25), new DateOnly(2026, 11, 27)),
            CancellationToken.None);
        var tooLong = await people.ScheduleOfficeVisit(
            new ScheduleOfficeVisitRequest(OfficeLocation.Oslo, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15)),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(overlap.Result, Is.TypeOf<ConflictObjectResult>());
            Assert.That(tooLong.Result, Is.TypeOf<BadRequestObjectResult>());
        });
    }

    [Test]
    public async Task RefillWorkerCanOfferOsloWorkToBengaluruEmployeeDuringVisit()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
        var factory = await CreateFactory();
        var refillId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await using (var setup = await factory.CreateDbContextAsync())
        {
            var employee = await setup.Volunteers.SingleAsync(x => x.EmployeeId == EmployeeId);
            setup.EmployeeRoles.Add(new EmployeeRole { EmployeeId = EmployeeId, RoleName = "Employee" });
            setup.EmployeeOfficeVisits.Add(new EmployeeOfficeVisit
            {
                Id = Guid.NewGuid(), EmployeeId = EmployeeId, Office = OfficeLocation.Oslo,
                StartDate = new DateOnly(2026, 11, 16), EndDate = new DateOnly(2026, 11, 22),
                CreatedAt = clock.GetUtcNow()
            });
            setup.SnackItems.Add(new SnackItem
            {
                Id = itemId, Office = OfficeLocation.Oslo, Name = "Coffee", Unit = "bag",
                QuantityOnHand = 0, ReorderThreshold = 1, IsActive = true
            });
            setup.Refills.Add(new Refill(refillId, OfficeLocation.Oslo, "Coffee", clock.GetUtcNow())
            {
                SnackItemId = itemId, Status = RefillStatus.Open
            });
            await setup.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SnackCounter:OfferTimeoutMinutes"] = "2"
        }).Build();
        var worker = new SnackCounterAutomationWorker(factory, clock,
            new TableCurrencyConverter(new Dictionary<(Currency From, Currency To), decimal>()),
            new TestExternalHrClient(), configuration,
            NullLogger<SnackCounterAutomationWorker>.Instance);
        await worker.RunRefillOfferCycleAsync();

        await using var verify = await factory.CreateDbContextAsync();
        var refill = await verify.Refills.Include(x => x.AssignedVolunteer).SingleAsync(x => x.Id == refillId);
        Assert.Multiple(() =>
        {
            Assert.That(refill.Status, Is.EqualTo(RefillStatus.Assigned));
            Assert.That(refill.AssignedVolunteer?.EmployeeId, Is.EqualTo(EmployeeId));
            Assert.That(refill.AssignedVolunteer?.PayrollCurrency, Is.EqualTo(Currency.INR));
        });

        var refillOperations = new RefillOperationsController(factory, clock, configuration);
        SetUser(refillOperations, "Employee");
        var queueResult = await refillOperations.OfficeRefillQueue(OfficeLocation.Oslo, CancellationToken.None);
        var queueItem = ((IReadOnlyList<OfficeRefillQueueItemResponse>)((OkObjectResult)queueResult.Result!).Value!)
            .Single(row => row.Refill.Id == refillId);
        var acceptedResult = await refillOperations.AcceptRefill(refillId,
            new AcceptRefillRequest(queueItem.Refill.Version, WorkingMode.WFO, OfficeLocation.Oslo), CancellationToken.None);
        Assert.That(acceptedResult.Result, Is.TypeOf<OkObjectResult>());
        var accepted = (RefillActionResponse)((OkObjectResult)acceptedResult.Result!).Value!;
        Assert.That(accepted.Accepted, Is.True);
        var purchaseResult = await refillOperations.CompleteRefill(refillId,
            new CompleteRefillRequest(accepted.Refill.Version, 5m, 125m, "Oslo office snacks", null), CancellationToken.None);
        Assert.That(purchaseResult.Result, Is.TypeOf<OkObjectResult>());

        clock.Set(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        var employeeOperations = CreatePeopleController(factory, clock);
        var claimResult = await employeeOperations.SubmitClaim(
            new CreateClaimRequest(refillId.ToString("D"), refillId, OfficeLocation.Oslo, 125m, Currency.NOK,
                "Oslo office snacks", refillId), CancellationToken.None);
        Assert.That(claimResult.Result, Is.TypeOf<CreatedAtActionResult>());
        var claim = (ClaimResponse)((CreatedAtActionResult)claimResult.Result!).Value!;

        var hrOperations = CreatePeopleController(factory, clock);
        SetUser(hrOperations, "Employee", "HR");
        var hrQueue = await hrOperations.HrClaims(null, CancellationToken.None);
        var hrClaim = ((IReadOnlyList<ClaimResponse>)((OkObjectResult)hrQueue.Result!).Value!).Single();
        Assert.That(hrClaim.EmployeePayrollCurrency, Is.EqualTo(Currency.INR));
        var claimedHr = await hrOperations.ClaimForProcessing(claim.Id,
            new ClaimForProcessingRequest(hrClaim.RowVersion), CancellationToken.None);
        var processing = (ClaimResponse)((OkObjectResult)claimedHr.Result!).Value!;
        var decision = await hrOperations.DecideClaim(claim.Id,
            new DecideClaimRequest(processing.RowVersion, true, null), CancellationToken.None);
        var approved = (ClaimResponse)((OkObjectResult)decision.Result!).Value!;
        var reimbursed = await hrOperations.ReimburseClaim(claim.Id,
            new ReimburseClaimRequest(approved.RowVersion, 1_250m, Currency.INR, 10m, clock.GetUtcNow(), "approved test FX"),
            CancellationToken.None);
        Assert.That(reimbursed.Result, Is.TypeOf<OkObjectResult>());

        await using var finalState = await factory.CreateDbContextAsync();
        var completedRefill = await finalState.Refills.SingleAsync(x => x.Id == refillId);
        var completedClaim = await finalState.ReimbursementClaims.SingleAsync(x => x.Id == claim.Id);
        Assert.Multiple(() =>
        {
            Assert.That(completedRefill.PurchaseCurrency, Is.EqualTo(Currency.NOK));
            Assert.That(completedClaim.Currency, Is.EqualTo(Currency.NOK));
            Assert.That(completedClaim.PayrollCurrency, Is.EqualTo(Currency.INR));
            Assert.That(completedClaim.PayrollAmount, Is.EqualTo(1_250m));
            Assert.That(completedClaim.ExchangeRate, Is.EqualTo(10m));
            Assert.That(completedClaim.ExchangeRateSource, Is.EqualTo("approved test FX"));
        });
    }

    [Test]
    public async Task RefillWorkerAssignsEachVisitOfficeToItsVisitorNotTheirHomeOfficeRoster()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
        var factory = await CreateFactory();
        var osloVisitorId = "oslo-visitor";
        var osloHomeId = "oslo-home";
        var bengaluruHomeId = "blr-home";
        var osloRefillId = Guid.NewGuid();
        var bengaluruRefillId = Guid.NewGuid();
        var priorOsloRefillId = Guid.NewGuid();
        var priorBengaluruRefillId = Guid.NewGuid();

        await using (var setup = await factory.CreateDbContextAsync())
        {
            setup.Volunteers.AddRange(
                new Volunteer(osloVisitorId, OfficeLocation.Oslo, Currency.NOK),
                new Volunteer(osloHomeId, OfficeLocation.Oslo, Currency.NOK),
                new Volunteer(bengaluruHomeId, OfficeLocation.Bengaluru, Currency.INR));
            setup.EmployeeRoles.AddRange(
                new EmployeeRole { EmployeeId = EmployeeId, RoleName = "Employee" },
                new EmployeeRole { EmployeeId = osloVisitorId, RoleName = "Employee" },
                new EmployeeRole { EmployeeId = osloHomeId, RoleName = "Employee" },
                new EmployeeRole { EmployeeId = bengaluruHomeId, RoleName = "Employee" });
            setup.EmployeeOffices.AddRange(
                new OfficeVolunteerEligibility { EmployeeId = EmployeeId, Office = OfficeLocation.Bengaluru },
                new OfficeVolunteerEligibility { EmployeeId = osloVisitorId, Office = OfficeLocation.Oslo },
                new OfficeVolunteerEligibility { EmployeeId = osloHomeId, Office = OfficeLocation.Oslo },
                new OfficeVolunteerEligibility { EmployeeId = bengaluruHomeId, Office = OfficeLocation.Bengaluru });
            setup.EmployeeOfficeVisits.AddRange(
                new EmployeeOfficeVisit
                {
                    Id = Guid.NewGuid(), EmployeeId = EmployeeId, Office = OfficeLocation.Oslo,
                    StartDate = new DateOnly(2026, 11, 16), EndDate = new DateOnly(2026, 11, 17), CreatedAt = clock.GetUtcNow()
                },
                new EmployeeOfficeVisit
                {
                    Id = Guid.NewGuid(), EmployeeId = osloVisitorId, Office = OfficeLocation.Bengaluru,
                    StartDate = new DateOnly(2026, 11, 16), EndDate = new DateOnly(2026, 11, 17), CreatedAt = clock.GetUtcNow()
                });

            var osloItemId = Guid.NewGuid();
            var bengaluruItemId = Guid.NewGuid();
            setup.SnackItems.AddRange(
                new SnackItem { Id = osloItemId, Office = OfficeLocation.Oslo, Name = "Oslo coffee", Unit = "bag", QuantityOnHand = 0, ReorderThreshold = 1, IsActive = true },
                new SnackItem { Id = bengaluruItemId, Office = OfficeLocation.Bengaluru, Name = "Bengaluru tea", Unit = "box", QuantityOnHand = 0, ReorderThreshold = 1, IsActive = true });
            setup.Refills.AddRange(
                new Refill(osloRefillId, OfficeLocation.Oslo, "Oslo coffee", clock.GetUtcNow()) { SnackItemId = osloItemId },
                new Refill(bengaluruRefillId, OfficeLocation.Bengaluru, "Bengaluru tea", clock.GetUtcNow()) { SnackItemId = bengaluruItemId });
            setup.Refills.AddRange(
                new Refill(priorOsloRefillId, OfficeLocation.Oslo, "Prior Oslo refill", clock.GetUtcNow())
                { Status = RefillStatus.Purchased, AssignedVolunteer = setup.Volunteers.Local.Single(x => x.EmployeeId == osloHomeId), PurchasedAt = clock.GetUtcNow(), PurchaseAmount = 1m },
                new Refill(priorBengaluruRefillId, OfficeLocation.Bengaluru, "Prior Bengaluru refill", clock.GetUtcNow())
                { Status = RefillStatus.Purchased, AssignedVolunteer = setup.Volunteers.Local.Single(x => x.EmployeeId == bengaluruHomeId), PurchasedAt = clock.GetUtcNow(), PurchaseAmount = 1m });
            setup.RefillAssignmentAttempts.AddRange(
                new RefillAssignmentAttempt
                {
                    Id = Guid.NewGuid(), RefillId = priorBengaluruRefillId, EmployeeId = EmployeeId,
                    LocalDate = new DateOnly(2026, 11, 16), AttemptNumber = 1, Outcome = RefillAttemptOutcome.SkippedWfh,
                    WorkingMode = WorkingMode.WFH, AttemptedAt = clock.GetUtcNow()
                },
                new RefillAssignmentAttempt
                {
                    Id = Guid.NewGuid(), RefillId = priorOsloRefillId, EmployeeId = osloVisitorId,
                    LocalDate = new DateOnly(2026, 11, 16), AttemptNumber = 1, Outcome = RefillAttemptOutcome.SkippedWfh,
                    WorkingMode = WorkingMode.WFH, AttemptedAt = clock.GetUtcNow()
                });
            await setup.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SnackCounter:OfferTimeoutMinutes"] = "2"
        }).Build();
        var worker = new SnackCounterAutomationWorker(factory, clock,
            new TableCurrencyConverter(new Dictionary<(Currency From, Currency To), decimal>()),
            new TestExternalHrClient(), configuration,
            NullLogger<SnackCounterAutomationWorker>.Instance);
        await worker.RunRefillOfferCycleAsync();

        await using var verify = await factory.CreateDbContextAsync();
        var osloRefill = await verify.Refills.Include(refill => refill.AssignedVolunteer).SingleAsync(refill => refill.Id == osloRefillId);
        var bengaluruRefill = await verify.Refills.Include(refill => refill.AssignedVolunteer).SingleAsync(refill => refill.Id == bengaluruRefillId);
        Assert.Multiple(() =>
        {
            Assert.That(osloRefill.AssignedVolunteer?.EmployeeId, Is.EqualTo(EmployeeId));
            Assert.That(bengaluruRefill.AssignedVolunteer?.EmployeeId, Is.EqualTo(osloVisitorId));
        });
    }

    private static async Task<TestDbContextFactory> CreateFactory()
    {
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"office-visits-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.Volunteers.Add(new Volunteer(EmployeeId, OfficeLocation.Bengaluru, Currency.INR));
        await db.SaveChangesAsync();
        return factory;
    }

    private static PeopleOperationsController CreatePeopleController(TestDbContextFactory factory, TimeProvider clock)
    {
        var controller = new PeopleOperationsController(factory, clock, new ConfigurationBuilder().Build(), new TestExternalHrClient());
        SetUser(controller);
        return controller;
    }

    private static OfficesController CreateOfficesController(TestDbContextFactory factory, TimeProvider clock)
    {
        var controller = new OfficesController(factory, clock);
        SetUser(controller);
        return controller;
    }

    private static void SetUser(ControllerBase controller, params string[] roles)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, EmployeeId) }
            .Concat(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset value) => _now = value;
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
