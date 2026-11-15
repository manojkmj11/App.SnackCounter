using SnackCounter.Application;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using API.SnackCounter.Controllers;

namespace API.SnackCounter.Tests;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class SnackCounterRulesTests
{
    [Test]
    public async Task ConcurrentEmployees_CannotBothClaimTheSameRefill()
    {
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"concurrent-refill-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
        var firstEmployee = new Volunteer("emp-1", OfficeLocation.Oslo, Currency.NOK);
        var secondEmployee = new Volunteer("emp-2", OfficeLocation.Oslo, Currency.NOK);
        var itemId = Guid.NewGuid();
        var refillId = Guid.NewGuid();
        string refillVersion;
        string itemVersion;
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Volunteers.AddRange(firstEmployee, secondEmployee);
            setup.SnackItems.Add(new SnackItem
            {
                Id = itemId, Office = OfficeLocation.Oslo, Name = "Coffee", Unit = "pack",
                QuantityOnHand = 0, ReorderThreshold = 1, IsClaimed = true, RowVersion = [1, 2, 3, 4, 5, 6, 7, 8]
            });
            var refill = new Refill(refillId, OfficeLocation.Oslo, "Coffee", clock.GetUtcNow())
            {
                SnackItemId = itemId, Status = RefillStatus.Assigned, AssignedVolunteer = firstEmployee,
                AssignmentExpiresAt = clock.GetUtcNow().AddMinutes(2),
                RowVersion = [8, 7, 6, 5, 4, 3, 2, 1]
            };
            setup.Refills.Add(refill);
            setup.RefillAssignmentAttempts.Add(new RefillAssignmentAttempt
            {
                Id = Guid.NewGuid(), RefillId = refillId, EmployeeId = firstEmployee.EmployeeId,
                LocalDate = new DateOnly(2026, 11, 14), AttemptNumber = 1, Outcome = RefillAttemptOutcome.Offered,
                AttemptedAt = clock.GetUtcNow(), AssignmentExpiresAt = refill.AssignmentExpiresAt
            });
            await setup.SaveChangesAsync();
            var refillSnapshot = await setup.Refills.AsNoTracking().SingleAsync(x => x.Id == refillId);
            var item = await setup.SnackItems.AsNoTracking().SingleAsync(x => x.Id == itemId);
            refillVersion = Convert.ToBase64String(refillSnapshot.RowVersion);
            itemVersion = Convert.ToBase64String(item.RowVersion);
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SnackCounter:AssignmentTimeoutMinutes"] = "30"
        }).Build();
        var firstController = new RefillOperationsController(factory, clock, configuration);
        var secondController = new RefillOperationsController(factory, clock, configuration);
        SetIdentity(firstController, firstEmployee.EmployeeId, "Employee");
        SetIdentity(secondController, secondEmployee.EmployeeId, "Employee");

        var results = await Task.WhenAll(
            firstController.ClaimRefill(OfficeLocation.Oslo, refillId,
                new ClaimRefillRequest(refillVersion, itemVersion), CancellationToken.None),
            secondController.ClaimRefill(OfficeLocation.Oslo, refillId,
                new ClaimRefillRequest(refillVersion, itemVersion), CancellationToken.None));

        Assert.That(results.Count(result => result.Result is OkObjectResult), Is.EqualTo(1));
        Assert.That(results.Count(result => result.Result is ConflictObjectResult), Is.EqualTo(1));
        await using var verify = await factory.CreateDbContextAsync();
        var claimed = await verify.Refills.Include(x => x.AssignedVolunteer).SingleAsync(x => x.Id == refillId);
        var itemAfterClaim = await verify.SnackItems.SingleAsync(x => x.Id == itemId);
        Assert.Multiple(() =>
        {
            Assert.That(claimed.Status, Is.EqualTo(RefillStatus.Assigned));
            Assert.That(claimed.AssignedVolunteer!.EmployeeId, Is.EqualTo(firstEmployee.EmployeeId));
            Assert.That(itemAfterClaim.IsClaimed, Is.True);
        });
    }

    [Test]
    public async Task ExpiredOffer_IsHandedToNextEmployee_AndDailyRosterExhaustionWaitsUntilTomorrow()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"expired-claim-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        var refillId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var first = new Volunteer("emp-1", OfficeLocation.Bengaluru, Currency.INR);
        var next = new Volunteer("emp-2", OfficeLocation.Bengaluru, Currency.INR);
        var visitorWithoutOfficeAccess = new Volunteer("emp-0", OfficeLocation.Oslo, Currency.NOK);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Volunteers.AddRange(first, next, visitorWithoutOfficeAccess);
            setup.EmployeeRoles.AddRange(
                new EmployeeRole { EmployeeId = first.EmployeeId, RoleName = "Employee" },
                new EmployeeRole { EmployeeId = next.EmployeeId, RoleName = "Employee" },
                new EmployeeRole { EmployeeId = visitorWithoutOfficeAccess.EmployeeId, RoleName = "Employee" });
            setup.EmployeeOffices.AddRange(
                new OfficeVolunteerEligibility { EmployeeId = first.EmployeeId, Office = OfficeLocation.Bengaluru },
                new OfficeVolunteerEligibility { EmployeeId = next.EmployeeId, Office = OfficeLocation.Bengaluru });
            var item = new SnackItem
            {
                Id = itemId, Office = OfficeLocation.Bengaluru, Name = "Tea", Unit = "box",
                QuantityOnHand = 0, ReorderThreshold = 1, IsClaimed = true, RowVersion = [1, 2, 3, 4, 5, 6, 7, 8]
            };
            var refill = new Refill(refillId, OfficeLocation.Bengaluru, "Tea", clock.GetUtcNow().AddHours(-2))
            {
                SnackItemId = itemId, Status = RefillStatus.Assigned, AssignedVolunteer = first,
                AssignmentExpiresAt = clock.GetUtcNow().AddMinutes(-1), Version = 7,
                RowVersion = [8, 7, 6, 5, 4, 3, 2, 1]
            };
            setup.SnackItems.Add(item);
            setup.Refills.Add(refill);
            setup.RefillAssignmentAttempts.Add(new RefillAssignmentAttempt
            {
                Id = Guid.NewGuid(), RefillId = refillId, EmployeeId = first.EmployeeId,
                LocalDate = new DateOnly(2026, 11, 14), AttemptNumber = 1, Outcome = RefillAttemptOutcome.Offered,
                AttemptedAt = clock.GetUtcNow().AddHours(-2), AssignmentExpiresAt = refill.AssignmentExpiresAt
            });
            await setup.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SnackCounter:AssignmentTimeoutMinutes"] = "30",
            ["SnackCounter:OfferTimeoutMinutes"] = "2"
        }).Build();
        var worker = new SnackCounterAutomationWorker(factory, clock, CurrencyConverter(), new TestExternalHrClient(),
            configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<SnackCounterAutomationWorker>.Instance);
        await worker.RunExpiredClaimCycleAsync();
        await worker.RunRefillOfferCycleAsync();

        await using (var verify = await factory.CreateDbContextAsync())
        {
            var refill = await verify.Refills.Include(x => x.AssignedVolunteer).SingleAsync(x => x.Id == refillId);
            Assert.That(refill.AssignedVolunteer!.EmployeeId, Is.EqualTo(next.EmployeeId));
            Assert.That(await verify.RefillAssignmentAttempts.AnyAsync(x => x.RefillId == refillId &&
                x.EmployeeId == first.EmployeeId && x.Outcome == RefillAttemptOutcome.Expired), Is.True);
            Assert.That((await verify.SnackItems.SingleAsync(x => x.Id == itemId)).IsClaimed, Is.True);
        }

        clock.Advance(TimeSpan.FromMinutes(3));
        await worker.RunExpiredClaimCycleAsync();
        await worker.RunRefillOfferCycleAsync();
        await using (var verify = await factory.CreateDbContextAsync())
        {
            var refill = await verify.Refills.SingleAsync(x => x.Id == refillId);
            Assert.That(refill.Status, Is.EqualTo(RefillStatus.Open), "each roster member has already received an offer today");
            Assert.That(refill.AssignedVolunteer, Is.Null);
        }

        clock.Advance(TimeSpan.FromHours(24));
        await worker.RunRefillOfferCycleAsync();
        await using var nextDay = await factory.CreateDbContextAsync();
        var offeredTomorrow = await nextDay.Refills.Include(x => x.AssignedVolunteer).SingleAsync(x => x.Id == refillId);
        Assert.That(offeredTomorrow.AssignedVolunteer!.EmployeeId, Is.EqualTo(first.EmployeeId));
    }

    [Test]
    public async Task RefillWithTimedOutWfhAndWrongOfficeRoster_ReturnsNextDay()
    {
        var today = new DateOnly(2026, 11, 15);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"exhausted-roster-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        var refillId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var meera = new Volunteer("blr-01", OfficeLocation.Bengaluru, Currency.INR);
        var kiran = new Volunteer("blr-02", OfficeLocation.Bengaluru, Currency.INR);
        var erik = new Volunteer("oslo-02", OfficeLocation.Oslo, Currency.NOK);

        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Volunteers.AddRange(meera, kiran, erik);
            setup.EmployeeRoles.AddRange(
                new EmployeeRole { EmployeeId = meera.EmployeeId, RoleName = "Employee" },
                new EmployeeRole { EmployeeId = kiran.EmployeeId, RoleName = "Employee" },
                new EmployeeRole { EmployeeId = erik.EmployeeId, RoleName = "Employee" });
            setup.EmployeeOffices.AddRange(
                new OfficeVolunteerEligibility { EmployeeId = meera.EmployeeId, Office = OfficeLocation.Bengaluru },
                new OfficeVolunteerEligibility { EmployeeId = kiran.EmployeeId, Office = OfficeLocation.Bengaluru },
                new OfficeVolunteerEligibility { EmployeeId = erik.EmployeeId, Office = OfficeLocation.Bengaluru });
            setup.SnackItems.Add(new SnackItem
            {
                Id = itemId, Office = OfficeLocation.Bengaluru, Name = "Coke", Unit = "can",
                QuantityOnHand = 0, ReorderThreshold = 1, RowVersion = [1, 2, 3, 4, 5, 6, 7, 8]
            });
            setup.Refills.Add(new Refill(refillId, OfficeLocation.Bengaluru, "Coke", clock.GetUtcNow().AddHours(-1))
            {
                SnackItemId = itemId, Status = RefillStatus.Open, Version = 3,
                RowVersion = [8, 7, 6, 5, 4, 3, 2, 1]
            });
            setup.RefillAssignmentAttempts.AddRange(
                new RefillAssignmentAttempt
                {
                    Id = Guid.NewGuid(), RefillId = refillId, EmployeeId = meera.EmployeeId,
                    LocalDate = today, AttemptNumber = 1, Outcome = RefillAttemptOutcome.Expired,
                    AttemptedAt = clock.GetUtcNow().AddMinutes(-20),
                    AssignmentExpiresAt = clock.GetUtcNow().AddMinutes(-18)
                },
                new RefillAssignmentAttempt
                {
                    Id = Guid.NewGuid(), RefillId = refillId, EmployeeId = kiran.EmployeeId,
                    LocalDate = today, AttemptNumber = 2, Outcome = RefillAttemptOutcome.SkippedWfh,
                    WorkingMode = WorkingMode.WFH, WorkingOffice = OfficeLocation.Bengaluru,
                    AttemptedAt = clock.GetUtcNow().AddMinutes(-15),
                    AssignmentExpiresAt = clock.GetUtcNow().AddMinutes(-13)
                },
                new RefillAssignmentAttempt
                {
                    Id = Guid.NewGuid(), RefillId = refillId, EmployeeId = erik.EmployeeId,
                    LocalDate = today, AttemptNumber = 3, Outcome = RefillAttemptOutcome.WrongLocation,
                    WorkingMode = WorkingMode.WFO, WorkingOffice = OfficeLocation.Oslo,
                    AttemptedAt = clock.GetUtcNow().AddMinutes(-10),
                    AssignmentExpiresAt = clock.GetUtcNow().AddMinutes(-8)
                });
            await setup.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SnackCounter:OfferTimeoutMinutes"] = "2"
        }).Build();
        var worker = new SnackCounterAutomationWorker(factory, clock, CurrencyConverter(), new TestExternalHrClient(),
            configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<SnackCounterAutomationWorker>.Instance);

        await worker.RunRefillOfferCycleAsync();
        await using (var sameDay = await factory.CreateDbContextAsync())
        {
            var refill = await sameDay.Refills.SingleAsync(x => x.Id == refillId);
            Assert.That(refill.Status, Is.EqualTo(RefillStatus.Open),
                "The roster has timed out, chosen WFH, or is at the wrong office for today's offers.");
            Assert.That(refill.AssignedVolunteer, Is.Null);
            Assert.That(await sameDay.RefillAssignmentAttempts.CountAsync(x => x.RefillId == refillId), Is.EqualTo(3));
        }

        clock.Advance(TimeSpan.FromDays(1));
        await worker.RunRefillOfferCycleAsync();

        await using var nextDay = await factory.CreateDbContextAsync();
        var assignedTomorrow = await nextDay.Refills.Include(x => x.AssignedVolunteer).SingleAsync(x => x.Id == refillId);
        var nextOffer = await nextDay.RefillAssignmentAttempts.SingleAsync(x =>
            x.RefillId == refillId && x.LocalDate == today.AddDays(1));
        Assert.Multiple(() =>
        {
            Assert.That(assignedTomorrow.Status, Is.EqualTo(RefillStatus.Assigned));
            Assert.That(assignedTomorrow.AssignedVolunteer!.EmployeeId, Is.EqualTo(kiran.EmployeeId));
            Assert.That(assignedTomorrow.AssignmentExpiresAt, Is.EqualTo(clock.GetUtcNow().AddMinutes(2)));
            Assert.That(nextOffer.Outcome, Is.EqualTo(RefillAttemptOutcome.Offered));
            Assert.That(nextOffer.EmployeeId, Is.EqualTo(kiran.EmployeeId));
        });
    }

    private static void SetIdentity(ControllerBase controller, string employeeId, string role)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, employeeId), new Claim(ClaimTypes.Role, role)], "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static TableCurrencyConverter CurrencyConverter() => new(new Dictionary<(Currency, Currency), decimal>
    {
        [(Currency.NOK, Currency.INR)] = 10m,
        [(Currency.INR, Currency.NOK)] = 0.1m
    });

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TestExternalHrClient : IExternalHrClaimClient
    {
        public Task SubmitClaimAsync(Guid claimId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DecideClaimAsync(Guid claimId, bool approve, string? reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestDbContextFactory(DbContextOptions<SnackCounterDbContext> options)
        : IDbContextFactory<SnackCounterDbContext>
    {
        public SnackCounterDbContext CreateDbContext() => new(options);
        public Task<SnackCounterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SnackCounterDbContext(options));
    }
}
