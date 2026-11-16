using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class IdentityController(
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    IWebHostEnvironment environment,
    TimeProvider clock) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("demo-identities")]
    public async Task<ActionResult<IReadOnlyList<DemoIdentityResponse>>> DemoIdentities(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) return NotFound();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var employees = await database.Volunteers.AsNoTracking().Where(person => person.IsActive && person.Email != null)
            .OrderBy(person => person.HomeOffice).ThenBy(person => person.EmployeeId)
            .Select(person => new { person.EmployeeId, person.DisplayName, person.Email, person.HomeOffice, person.PayrollCurrency })
            .ToArrayAsync(cancellationToken);
        var employeeIds = employees.Select(person => person.EmployeeId).ToArray();
        var roles = await database.EmployeeRoles.AsNoTracking().Where(role => employeeIds.Contains(role.EmployeeId))
            .OrderBy(role => role.RoleName).ToArrayAsync(cancellationToken);
        return Ok(employees.Select(person => new DemoIdentityResponse(person.EmployeeId, person.DisplayName,
            person.Email!, person.HomeOffice, person.PayrollCurrency,
            roles.Where(role => role.EmployeeId == person.EmployeeId).Select(role => role.RoleName).ToArray())).ToArray());
    }

    [AllowAnonymous]
    [HttpPost("dev-login")]
    [ProducesResponseType<DemoLoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DemoLoginResponse>> DevelopmentLogin(DemoLoginRequest request, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new ProblemDetails { Title = "Email is required." });
        }
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var email = request.Email.Trim();
        var employee = await database.Volunteers.AsNoTracking()
            .SingleOrDefaultAsync(person => person.Email == email && person.IsActive, cancellationToken);
        if (employee is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Demo employee not found.",
                Detail = "Use one of the seeded @snackcounter.local email addresses."
            });
        }

        var now = clock.GetUtcNow();
        var accessibleOffices = await FrontendApiSupport.GetAccessibleOfficesAsync(
            database, employee.EmployeeId, now, cancellationToken);
        var selectedOffice = await FrontendApiSupport.GetCurrentOfficeAsync(
            database, employee.EmployeeId, employee.HomeOffice, now, cancellationToken);
        if (!accessibleOffices.Contains(selectedOffice))
        {
            selectedOffice = employee.HomeOffice;
        }
        if (!accessibleOffices.Contains(selectedOffice))
        {
            return Conflict(new ProblemDetails
            {
                Title = "No active office is available to this account.",
                Detail = "Contact an administrator to restore office access."
            });
        }

        var roles = await database.EmployeeRoles.AsNoTracking()
            .Where(role => role.EmployeeId == employee.EmployeeId)
            .OrderBy(role => role.RoleName)
            .Select(role => role.RoleName)
            .ToArrayAsync(cancellationToken);

        return Ok(new DemoLoginResponse(
            new CurrentUserResponse(employee.EmployeeId, employee.DisplayName, employee.Email!, employee.HomeOffice,
                employee.PayrollCurrency, roles),
            DevelopmentEmailAuthenticationHandler.HeaderName,
            employee.Email!,
            selectedOffice));
    }

    [Authorize]
    [HttpGet("~/api/me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        var employeeId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (employeeId is null)
        {
            return Unauthorized();
        }

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var employee = await database.Volunteers.AsNoTracking()
            .SingleOrDefaultAsync(person => person.EmployeeId == employeeId, cancellationToken);
        if (employee is null)
        {
            return Unauthorized();
        }

        var roles = await database.EmployeeRoles.AsNoTracking()
            .Where(role => role.EmployeeId == employeeId)
            .OrderBy(role => role.RoleName)
            .Select(role => role.RoleName)
            .ToArrayAsync(cancellationToken);
        return Ok(new CurrentUserResponse(employee.EmployeeId, employee.DisplayName, employee.Email ?? string.Empty,
            employee.HomeOffice, employee.PayrollCurrency, roles));
    }
}

public sealed record DemoLoginRequest(string Email);
public sealed record DemoIdentityResponse(string EmployeeId, string DisplayName, string Email,
    OfficeLocation HomeOffice, Currency PayrollCurrency, IReadOnlyList<string> Roles);
public sealed record CurrentUserResponse(
    string EmployeeId,
    string DisplayName,
    string Email,
    OfficeLocation HomeOffice,
    Currency PayrollCurrency,
    IReadOnlyList<string> Roles);
public sealed record DemoLoginResponse(
    CurrentUserResponse User,
    string HeaderName,
    string HeaderValue,
    OfficeLocation CurrentOffice);
