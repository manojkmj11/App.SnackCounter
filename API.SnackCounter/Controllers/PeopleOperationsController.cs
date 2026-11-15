using System.Security.Claims;
using System.Security.Cryptography;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

[ApiController]
[Authorize(Roles = "Employee,HR,Manager")]
[Route("api")]
public sealed class PeopleOperationsController(
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    TimeProvider clock,
    IConfiguration configuration,
    IExternalHrClaimClient externalHr) : ControllerBase
{
    [HttpGet("me/office-visits")]
    public async Task<ActionResult<IReadOnlyList<OfficeVisitResponse>>> MyOfficeVisits(CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var visits = await db.EmployeeOfficeVisits.AsNoTracking().Where(visit => visit.EmployeeId == employeeId)
            .OrderByDescending(visit => visit.StartDate)
            .Select(visit => new OfficeVisitResponse(visit.Id, visit.Office, visit.StartDate, visit.EndDate, visit.CancelledAt))
            .ToArrayAsync(cancellationToken);
        return Ok(visits);
    }

    [HttpPost("me/office-visits")]
    public async Task<ActionResult<OfficeVisitResponse>> ScheduleOfficeVisit(
        ScheduleOfficeVisitRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Office) || request.EndDate < request.StartDate ||
            request.EndDate.DayNumber - request.StartDate.DayNumber >= 14)
            return BadRequest(new ProblemDetails { Title = "Choose a valid visit of up to 14 calendar days." });

        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var employee = await db.Volunteers.AsNoTracking()
            .Where(person => person.EmployeeId == employeeId && person.IsActive)
            .Select(person => new { person.HomeOffice })
            .SingleOrDefaultAsync(cancellationToken);
        if (employee is null) return Forbid();
        if (request.Office == employee.HomeOffice)
            return BadRequest(new ProblemDetails { Title = "Office visits are only needed for another office." });

        var office = await db.Offices.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Location == request.Office && candidate.IsActive, cancellationToken);
        if (office is null) return NotFound(new ProblemDetails { Title = "The destination office is unavailable." });
        var today = LocalDate(now, office.TimeZoneId);
        if (request.StartDate < today)
            return BadRequest(new ProblemDetails { Title = "An office visit cannot start in the past." });

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var overlaps = await db.EmployeeOfficeVisits.AnyAsync(visit => visit.EmployeeId == employeeId &&
            visit.CancelledAt == null && visit.StartDate <= request.EndDate && visit.EndDate >= request.StartDate,
            cancellationToken);
        if (overlaps) return Conflict(new ProblemDetails { Title = "This visit overlaps another scheduled office visit." });
        var overlapsLeave = await db.EmployeeLeaves.AnyAsync(leave => leave.EmployeeId == employeeId &&
            leave.Office == request.Office && leave.Status != EmployeeLeaveStatus.Cancelled &&
            leave.StartDate <= request.EndDate && leave.EndDate >= request.StartDate, cancellationToken);
        if (overlapsLeave) return Conflict(new ProblemDetails { Title = "This visit overlaps planned leave at the destination office." });

        var visit = new EmployeeOfficeVisit
        {
            Id = Guid.NewGuid(), EmployeeId = employeeId, Office = request.Office,
            StartDate = request.StartDate, EndDate = request.EndDate, CreatedAt = now
        };
        db.EmployeeOfficeVisits.Add(visit);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(new ProblemDetails { Title = "The visit could not be saved. Refresh your visit calendar and try again." });
        }
        return CreatedAtAction(nameof(MyOfficeVisits), new OfficeVisitResponse(
            visit.Id, visit.Office, visit.StartDate, visit.EndDate, visit.CancelledAt));
    }

    [HttpDelete("me/office-visits/{id:guid}")]
    public async Task<IActionResult> CancelOfficeVisit(Guid id, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var visit = await db.EmployeeOfficeVisits.SingleOrDefaultAsync(
            item => item.Id == id && item.EmployeeId == employeeId, cancellationToken);
        if (visit is null) return NotFound();
        if (visit.CancelledAt is not null) return NoContent();
        var office = await db.Offices.AsNoTracking().SingleAsync(item => item.Location == visit.Office, cancellationToken);
        if (LocalDate(now, office.TimeZoneId) > visit.EndDate)
            return Conflict(new ProblemDetails { Title = "A completed office visit cannot be cancelled." });
        visit.CancelledAt = now;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new ProblemDetails { Title = "The visit changed while it was being cancelled. Refresh and try again." });
        }
        return NoContent();
    }

    [HttpGet("me/leaves")]
    public async Task<ActionResult<IReadOnlyList<LeaveResponse>>> MyLeaves(
        [FromQuery] OfficeLocation? office,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return Ok(await db.EmployeeLeaves.AsNoTracking().Where(x => x.EmployeeId == employeeId && (office == null || x.Office == office))
            .OrderBy(x => x.StartDate).Select(x => new LeaveResponse(x.Id, x.Office, x.StartDate, x.EndDate, x.Status))
            .ToArrayAsync(cancellationToken));
    }

    [HttpPost("me/leaves")]
    public async Task<ActionResult<LeaveResponse>> CreateLeave(CreateLeaveRequest request, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(db, employeeId, request.Office, cancellationToken, now)) return Forbid();
        var office = await db.Offices.AsNoTracking().SingleAsync(x => x.Location == request.Office, cancellationToken);
        var today = LocalDate(now, office.TimeZoneId);
        if (request.StartDate < today || request.EndDate < request.StartDate)
            return BadRequest(new ProblemDetails { Title = "Invalid leave dates.", Detail = "Leave must be a valid future date range." });
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var overlaps = await db.EmployeeLeaves.AnyAsync(x => x.EmployeeId == employeeId && x.Office == request.Office &&
            x.Status != EmployeeLeaveStatus.Cancelled && x.StartDate <= request.EndDate && x.EndDate >= request.StartDate, cancellationToken);
        if (overlaps) return Conflict(new ProblemDetails { Title = "Leave dates overlap an existing entry." });
        var leave = new EmployeeLeave { Id = Guid.NewGuid(), EmployeeId = employeeId, Office = request.Office,
            StartDate = request.StartDate, EndDate = request.EndDate, Status = EmployeeLeaveStatus.Planned, SubmittedAt = now };
        db.EmployeeLeaves.Add(leave);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(new ProblemDetails { Title = "Leave changed while it was being saved.", Detail = "Refresh your calendar and check for overlapping leave." });
        }
        var response = new LeaveResponse(leave.Id, leave.Office, leave.StartDate, leave.EndDate, leave.Status);
        return CreatedAtAction(nameof(MyLeaves), response);
    }

    [HttpPut("me/leaves/{id:guid}")]
    public async Task<ActionResult<LeaveResponse>> UpdateLeave(Guid id, UpdateLeaveRequest request, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var leave = await db.EmployeeLeaves.SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == employeeId, cancellationToken);
        if (leave is null) return NotFound();
        if (leave.Status == EmployeeLeaveStatus.Cancelled) return Conflict(new ProblemDetails { Title = "Cancelled leave cannot be edited." });
        if (!await FrontendApiSupport.HasOfficeAccessAsync(db, employeeId, leave.Office, cancellationToken, now)) return Forbid();
        var office = await db.Offices.AsNoTracking().SingleAsync(x => x.Location == leave.Office, cancellationToken);
        if (request.EndDate < request.StartDate || request.StartDate < LocalDate(now, office.TimeZoneId))
            return BadRequest(new ProblemDetails { Title = "Invalid leave dates." });
        var overlap = await db.EmployeeLeaves.AnyAsync(x => x.Id != id && x.EmployeeId == employeeId && x.Office == leave.Office &&
            x.Status != EmployeeLeaveStatus.Cancelled && x.StartDate <= request.EndDate && x.EndDate >= request.StartDate, cancellationToken);
        if (overlap) return Conflict(new ProblemDetails { Title = "Leave dates overlap an existing entry." });
        leave.StartDate = request.StartDate; leave.EndDate = request.EndDate; leave.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new ProblemDetails { Title = "Leave changed while it was being edited.", Detail = "Reload the calendar and try again." });
        }
        return Ok(new LeaveResponse(leave.Id, leave.Office, leave.StartDate, leave.EndDate, leave.Status));
    }

    [HttpDelete("me/leaves/{id:guid}")]
    public async Task<IActionResult> CancelLeave(Guid id, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var leave = await db.EmployeeLeaves.SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == employeeId, cancellationToken);
        if (leave is null) return NotFound();
        if (leave.Status == EmployeeLeaveStatus.Cancelled) return NoContent();
        var now = clock.GetUtcNow();
        var office = await db.Offices.AsNoTracking().SingleAsync(x => x.Location == leave.Office, cancellationToken);
        if (leave.StartDate < LocalDate(now, office.TimeZoneId)) return Conflict(new ProblemDetails { Title = "Past leave cannot be cancelled." });
        leave.Status = EmployeeLeaveStatus.Cancelled; leave.UpdatedAt = now;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new ProblemDetails { Title = "Leave changed while it was being cancelled.", Detail = "Reload the calendar and try again." }); }
        return NoContent();
    }

    [HttpGet("me/purchases")]
    public async Task<ActionResult<IReadOnlyList<EmployeePurchaseResponse>>> MyPurchases(CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var purchases = await db.Refills.AsNoTracking()
            .Where(purchase => EF.Property<string?>(purchase, "AssignedVolunteerEmployeeId") == employeeId &&
                purchase.PurchasedAt != null && purchase.PurchaseAmount != null && purchase.PurchaseCurrency != null)
            .OrderByDescending(purchase => purchase.PurchasedAt)
            .Select(purchase => new EmployeePurchaseResponse(
                purchase.Id, purchase.Office, purchase.Item, purchase.PurchaseAmount!.Value,
                purchase.PurchaseCurrency!.Value, purchase.PurchasedAt!.Value,
                purchase.PurchaseDescription ?? purchase.Item, null, null, null, null))
            .ToArrayAsync(cancellationToken);
        if (purchases.Length == 0) return Ok(purchases);

        var purchaseIds = purchases.Select(purchase => purchase.PurchaseId).ToArray();
        var claims = await db.ReimbursementClaims.AsNoTracking()
            .Where(claim => claim.EmployeeId == employeeId &&
                (purchaseIds.Contains(claim.PurchaseId) || claim.RefillId != null && purchaseIds.Contains(claim.RefillId.Value)))
            .OrderByDescending(claim => claim.SubmissionAttempt).ThenByDescending(claim => claim.SubmittedAt)
            .ToArrayAsync(cancellationToken);
        var latestClaimByPurchase = claims.GroupBy(claim => claim.RefillId ?? claim.PurchaseId)
            .ToDictionary(group => group.Key, group => group.First());
        return Ok(purchases.Select(purchase => latestClaimByPurchase.TryGetValue(purchase.PurchaseId, out var claim)
            ? purchase with { ClaimId = claim.Id, ClaimStatus = claim.Status, SubmissionAttempt = claim.SubmissionAttempt, DecisionReason = claim.DecisionReason }
            : purchase).ToArray());
    }

    [HttpGet("me/claims")]
    public async Task<ActionResult<IReadOnlyList<ClaimResponse>>> MyClaims(
        [FromQuery] OfficeLocation? office,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return Ok(await db.ReimbursementClaims.AsNoTracking().Where(x => x.EmployeeId == employeeId && (office == null || x.Office == office))
            .OrderByDescending(x => x.SubmittedAt).Select(x => ClaimResponse.From(x)).ToArrayAsync(cancellationToken));
    }

    [HttpPost("me/claims")]
    [RequestSizeLimit(7_000_000)]
    public async Task<ActionResult<ClaimResponse>> SubmitClaim(CreateClaimRequest request, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        if (request.Amount <= 0 || request.PurchaseId == Guid.Empty || string.IsNullOrWhiteSpace(request.Description) ||
            request.Description.Trim().Length > 2_000 || !Enum.IsDefined(request.Office) || !Enum.IsDefined(request.Currency) ||
            request.ReceiptMediaType?.Length > 100 ||
            !string.Equals(request.IdempotencyKey, request.PurchaseId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ProblemDetails { Title = "Claim details are incomplete.", Detail = "A valid purchase ID must be used as the idempotency key, along with amount and description." });
        byte[]? receipt = null;
        if (!string.IsNullOrWhiteSpace(request.ReceiptBase64))
        {
            try { receipt = Convert.FromBase64String(request.ReceiptBase64); }
            catch (FormatException) { return BadRequest(new ProblemDetails { Title = "Receipt must be valid base64." }); }
            if (receipt.Length > 5 * 1024 * 1024) return BadRequest(new ProblemDetails { Title = "Receipt exceeds 5 MB." });
            var detectedMediaType = DetectReceiptMediaType(receipt);
            if (detectedMediaType is null)
                return BadRequest(new ProblemDetails { Title = "Unsupported receipt format.", Detail = "Upload a JPEG, PNG, WebP, or PDF receipt." });
            if (!string.IsNullOrWhiteSpace(request.ReceiptMediaType) &&
                !string.Equals(request.ReceiptMediaType.Trim(), detectedMediaType, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new ProblemDetails { Title = "Receipt media type does not match its contents." });
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var purchase = await db.Refills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.PurchaseId &&
            x.PurchasedAt != null && x.PurchaseAmount != null && x.PurchaseCurrency != null &&
            EF.Property<string?>(x, "AssignedVolunteerEmployeeId") == employeeId, cancellationToken);
        if (purchase is null) return NotFound(new ProblemDetails { Title = "Purchase not found.", Detail = "This completed purchase is not available to your account." });
        if (request.Office != purchase.Office)
            return BadRequest(new ProblemDetails { Title = "Purchase details do not match.", Detail = "Submit the office recorded for this purchase." });
        if (request.RefillId is not null && request.RefillId != purchase.Id)
            return BadRequest(new ProblemDetails { Title = "Purchase details do not match." });
        if (request.Amount != purchase.PurchaseAmount || request.Currency != purchase.PurchaseCurrency)
            return BadRequest(new ProblemDetails { Title = "Claim amount does not match the purchase.", Detail = "Submit the amount and currency recorded for this purchase." });
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var prior = await db.ReimbursementClaims.Where(x => x.EmployeeId == employeeId &&
                (x.PurchaseId == request.PurchaseId || x.RefillId == request.PurchaseId))
            .OrderByDescending(x => x.SubmissionAttempt).ThenByDescending(x => x.SubmittedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var attempt = 1;
        Guid? previousClaimId = null;
        if (prior is not null)
        {
            if (prior.Status != ReimbursementClaimStatus.Denied)
            {
                return Ok(ClaimResponse.From(prior));
            }
            attempt = prior.SubmissionAttempt + 1;
            previousClaimId = prior.Id;
        }
        var now = clock.GetUtcNow();
        var claim = new ReimbursementClaim { Id = Guid.NewGuid(), EmployeeId = employeeId, Office = purchase.Office,
            PurchaseId = purchase.Id, SubmissionAttempt = attempt, PreviousClaimId = previousClaimId,
            RefillId = purchase.Id, IdempotencyKey = purchase.Id.ToString("D"), Amount = request.Amount, Currency = request.Currency,
            Description = request.Description.Trim(), SubmittedAt = now, ReceiptContent = receipt,
            ReceiptMediaType = receipt is null ? null : DetectReceiptMediaType(receipt), ReceiptSizeBytes = receipt?.LongLength,
            ReceiptSha256 = receipt is null ? null : Convert.ToHexString(SHA256.HashData(receipt)) };
        db.ReimbursementClaims.Add(claim);
        var conversation = await FrontendApiSupport.GetOrCreateHrConversationAsync(db, now, cancellationToken);
        var payload = FrontendApiSupport.Serialize(new { claimId = claim.Id, office = claim.Office, amount = claim.Amount, currency = claim.Currency, employeeId });
        var message = FrontendApiSupport.AddMessage(db, conversation, employeeId, null, null, MessageAudience.Hr,
            ConversationMessageKind.Notification, $"New reimbursement claim submitted ({claim.Amount} {claim.Currency}).", payload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, "ClaimSubmitted", $"claim:{claim.Id}:submitted", null, null, true, message, payload, now);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(), EventId = Guid.NewGuid(), EventType = "ExternalHrClaimSubmit",
            PayloadJson = FrontendApiSupport.Serialize(new { claimId = claim.Id }),
            OccurredAt = now, AvailableAt = now
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            return Conflict(new ProblemDetails { Title = "Claim already exists.", Detail = "A claim for this purchase was submitted at the same time. Refresh your claims before retrying." });
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return CreatedAtAction(nameof(MyClaims), ClaimResponse.From(claim));
    }

    [HttpGet("me/claims/{id:guid}")]
    public async Task<ActionResult<ClaimResponse>> MyClaim(Guid id, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == employeeId, cancellationToken);
        return claim is null ? NotFound() : Ok(ClaimResponse.From(claim));
    }

    [Authorize(Roles = "HR")]
    [HttpGet("hr/claims")]
    public async Task<ActionResult<IReadOnlyList<ClaimResponse>>> HrClaims([FromQuery] ReimbursementClaimStatus? status, CancellationToken cancellationToken)
    {
        if (status.HasValue && !Enum.IsDefined(status.Value))
            return BadRequest(new ProblemDetails { Title = "Invalid claim status." });
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ReimbursementClaims.AsNoTracking().AsQueryable();
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var claims = await query.OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.Id)
            .ToArrayAsync(cancellationToken);
        var payrollCurrencies = await GetPayrollCurrenciesAsync(db, claims.Select(x => x.EmployeeId).Distinct().ToArray(), cancellationToken);
        return Ok(claims.Select(x => ClaimResponse.From(x, payrollCurrencies[x.EmployeeId])).ToArray());
    }

    [Authorize(Roles = "HR")]
    [HttpGet("hr/claims/{id:guid}")]
    public async Task<ActionResult<ClaimResponse>> HrClaim(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (claim is null) return NotFound();
        var payrollCurrencies = await GetPayrollCurrenciesAsync(db, [claim.EmployeeId], cancellationToken);
        return Ok(ClaimResponse.From(claim, payrollCurrencies[claim.EmployeeId]));
    }

    [Authorize(Roles = "HR")]
    [HttpPost("hr/claims/{id:guid}/claim")]
    public async Task<ActionResult<ClaimResponse>> ClaimForProcessing(Guid id, ClaimForProcessingRequest request, CancellationToken cancellationToken)
    {
        var actor = FrontendApiSupport.EmployeeId(User); var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (claim is null) return NotFound();
        if (configuration.GetValue<bool>("MockHr:Enabled") && claim.ExternalHrClaimId is null)
            return Conflict(new ProblemDetails { Title = "Claim is still being submitted.", Detail = "Mock HR has not accepted this claim yet. Refresh the queue and try again shortly." });
        if (!VersionMatches(claim.RowVersion, request.RowVersion)) return Conflict(new ProblemDetails { Title = "Claim changed.", Detail = "Reload it and try again." });
        if (claim.Status == ReimbursementClaimStatus.Processing && claim.ProcessingLeaseExpiresAt > now && claim.ProcessingByEmployeeId != actor)
            return Conflict(new ProblemDetails { Title = "Another HR user is processing this claim." });
        if (claim.Status is not (ReimbursementClaimStatus.Submitted or ReimbursementClaimStatus.Processing))
            return Conflict(new ProblemDetails { Title = "This claim is already decided." });
        claim.Status = ReimbursementClaimStatus.Processing; claim.ProcessingByEmployeeId = actor; claim.ProcessingAt = now;
        claim.ProcessingLeaseExpiresAt = now.AddMinutes(15);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new ProblemDetails { Title = "Another HR user claimed this reimbursement." }); }
        return Ok(ClaimResponse.From(claim));
    }

    [Authorize(Roles = "HR")]
    [HttpPost("hr/claims/{id:guid}/decision")]
    public async Task<ActionResult<ClaimResponse>> DecideClaim(Guid id, DecideClaimRequest request, CancellationToken cancellationToken)
    {
        if ((!request.Approve && string.IsNullOrWhiteSpace(request.Reason)) || request.Reason?.Length > 2_000)
            return BadRequest(new ProblemDetails { Title = "A denial reason of at most 2,000 characters is required." });

        var actor = FrontendApiSupport.EmployeeId(User); var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (claim is null) return NotFound();
        if (!VersionMatches(claim.RowVersion, request.RowVersion)) return Conflict(new ProblemDetails { Title = "Claim changed.", Detail = "Reload it and try again." });
        if (claim.Status != ReimbursementClaimStatus.Processing || claim.ProcessingByEmployeeId != actor || claim.ProcessingLeaseExpiresAt <= now)
            return Conflict(new ProblemDetails { Title = "Claim must be actively claimed by this HR user." });
        if (configuration.GetValue<bool>("MockHr:Enabled"))
        {
            try
            {
                await externalHr.DecideClaimAsync(id, request.Approve, request.Reason?.Trim(), cancellationToken);
            }
            catch (ExternalHrClaimPendingException exception)
            {
                return Conflict(new ProblemDetails { Title = "Claim is still being submitted.", Detail = exception.Message });
            }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                return Conflict(new ProblemDetails { Title = "Mock HR already decided this claim.", Detail = "Refresh the queue to see its latest status." });
            }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Conflict(new ProblemDetails { Title = "Mock HR claim was not found.", Detail = "Refresh the queue and check that the claim was submitted." });
            }
            catch (HttpRequestException)
            {
                return StatusCode(StatusCodes.Status502BadGateway,
                    new ProblemDetails { Title = "Mock HR is unavailable.", Detail = "The claim decision could not be sent. Try again after Mock HR is available." });
            }
            return Accepted(new { claimId = claim.Id, status = claim.Status, awaitingWebhook = true });
        }

        claim.Status = request.Approve ? ReimbursementClaimStatus.Approved : ReimbursementClaimStatus.Denied;
        claim.DecisionReason = request.Reason?.Trim(); claim.DecidedAt = now;
        claim.ProcessingByEmployeeId = null; claim.ProcessingAt = null; claim.ProcessingLeaseExpiresAt = null;
        var employeeChat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(db, claim.EmployeeId, now, cancellationToken);
        var text = request.Approve ? $"Your reimbursement claim was approved ({claim.Amount} {claim.Currency})." : $"Your reimbursement claim was denied. {claim.DecisionReason}";
        var payload = FrontendApiSupport.Serialize(new { claimId = claim.Id, status = claim.Status, amount = claim.Amount, currency = claim.Currency, reason = claim.DecisionReason });
        var message = FrontendApiSupport.AddMessage(db, employeeChat, actor, claim.EmployeeId, null, MessageAudience.Employee,
            ConversationMessageKind.Notification, text, payload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, "ClaimDecided", $"claim:{claim.Id}:decision", claim.EmployeeId, null, false, message, payload, now);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new ProblemDetails { Title = "Claim was updated by another HR user." }); }
        return Ok(ClaimResponse.From(claim));
    }

    [Authorize(Roles = "HR")]
    [HttpPost("hr/claims/{id:guid}/reimburse")]
    public async Task<ActionResult<ClaimResponse>> ReimburseClaim(Guid id, ReimburseClaimRequest request, CancellationToken cancellationToken)
    {
        var actor = FrontendApiSupport.EmployeeId(User); var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (claim is null) return NotFound();
        if (!VersionMatches(claim.RowVersion, request.RowVersion)) return Conflict(new ProblemDetails { Title = "Claim changed.", Detail = "Reload it and try again." });
        if (claim.Status != ReimbursementClaimStatus.Approved) return Conflict(new ProblemDetails { Title = "Only an approved claim can be reimbursed." });
        if (request.PayrollAmount <= 0 || !Enum.IsDefined(request.PayrollCurrency))
            return BadRequest(new ProblemDetails { Title = "Payroll amount and currency must be valid." });
        var employeePayrollCurrency = await db.Volunteers.AsNoTracking()
            .Where(employee => employee.EmployeeId == claim.EmployeeId)
            .Select(employee => (Currency?)employee.PayrollCurrency)
            .SingleOrDefaultAsync(cancellationToken);
        if (employeePayrollCurrency is null || request.PayrollCurrency != employeePayrollCurrency)
            return BadRequest(new ProblemDetails { Title = "Payroll currency must match the employee's configured payroll currency." });
        if (request.ExchangeRate is > 99_999_999_999m || request.ExchangeRateSource?.Length > 200 ||
            request.ExchangeRateAt > now.AddMinutes(5))
            return BadRequest(new ProblemDetails { Title = "Exchange rate details are outside the supported range." });
        if ((claim.Currency != request.PayrollCurrency && (request.ExchangeRate is null || request.ExchangeRate <= 0 || string.IsNullOrWhiteSpace(request.ExchangeRateSource))) ||
            (request.ExchangeRate is not null && request.ExchangeRate <= 0))
            return BadRequest(new ProblemDetails { Title = "Currency conversion requires a positive rate and source." });
        if (claim.Currency == request.PayrollCurrency && request.ExchangeRate is not null && request.ExchangeRate != 1m)
            return BadRequest(new ProblemDetails { Title = "Same-currency reimbursement must use an exchange rate of 1." });
        if (claim.Currency == request.PayrollCurrency &&
            decimal.Round(claim.Amount, 2, MidpointRounding.AwayFromZero) !=
            decimal.Round(request.PayrollAmount, 2, MidpointRounding.AwayFromZero))
            return BadRequest(new ProblemDetails { Title = "Payroll amount must match the approved claim amount in the same currency." });
        if (claim.Currency != request.PayrollCurrency)
        {
            try
            {
                if (decimal.Round(claim.Amount * request.ExchangeRate!.Value, 2, MidpointRounding.AwayFromZero) !=
                    decimal.Round(request.PayrollAmount, 2, MidpointRounding.AwayFromZero))
                    return BadRequest(new ProblemDetails { Title = "Payroll amount does not match the exchange rate." });
            }
            catch (OverflowException)
            {
                return BadRequest(new ProblemDetails { Title = "Payroll amount exceeds the supported range." });
            }
        }
        claim.Status = ReimbursementClaimStatus.Reimbursed; claim.ReimbursedAt = now;
        claim.PayrollAmount = request.PayrollAmount; claim.PayrollCurrency = request.PayrollCurrency;
        claim.ExchangeRate = request.ExchangeRate; claim.ExchangeRateAt = request.ExchangeRate is null ? null : request.ExchangeRateAt ?? now;
        claim.ExchangeRateSource = request.ExchangeRate is null ? null : request.ExchangeRateSource;
        var employeeChat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(db, claim.EmployeeId, now, cancellationToken);
        var payload = FrontendApiSupport.Serialize(new { claimId = claim.Id, status = claim.Status, payrollAmount = claim.PayrollAmount, payrollCurrency = claim.PayrollCurrency });
        var message = FrontendApiSupport.AddMessage(db, employeeChat, actor, claim.EmployeeId, null, MessageAudience.Employee,
            ConversationMessageKind.Notification, $"Your claim was reimbursed: {claim.PayrollAmount} {claim.PayrollCurrency}.", payload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, "ClaimReimbursed", $"claim:{claim.Id}:reimbursed", claim.EmployeeId, null, false, message, payload, now);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new ProblemDetails { Title = "Claim was updated by another HR user." }); }
        return Ok(ClaimResponse.From(claim));
    }

    [Authorize(Roles = "HR")]
    [HttpGet("hr/claims/{id:guid}/receipt")]
    public async Task<IActionResult> ClaimReceipt(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var receipt = await db.ReimbursementClaims.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.ReceiptContent, x.ReceiptMediaType }).SingleOrDefaultAsync(cancellationToken);
        return receipt?.ReceiptContent is null
            ? NotFound()
            : File(receipt.ReceiptContent, receipt.ReceiptMediaType ?? "application/octet-stream");
    }

    private static DateOnly LocalDate(DateTimeOffset now, string timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime);

    private static Task<Dictionary<string, Currency>> GetPayrollCurrenciesAsync(
        SnackCounterDbContext db,
        string[] employeeIds,
        CancellationToken cancellationToken) => db.Volunteers.AsNoTracking()
        .Where(x => employeeIds.Contains(x.EmployeeId))
        .ToDictionaryAsync(x => x.EmployeeId, x => x.PayrollCurrency, cancellationToken);

    private static bool VersionMatches(byte[] current, string provided)
    {
        try { return CryptographicOperations.FixedTimeEquals(current, Convert.FromBase64String(provided)); }
        catch (FormatException) { return false; }
    }

    private static string? DetectReceiptMediaType(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 5 && content[..5].SequenceEqual("%PDF-"u8)) return "application/pdf";
        if (content.Length >= 8 && content[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (content.Length >= 3 && content[0] == 0xff && content[1] == 0xd8 && content[2] == 0xff) return "image/jpeg";
        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}

public sealed record CreateLeaveRequest(OfficeLocation Office, DateOnly StartDate, DateOnly EndDate);
public sealed record ScheduleOfficeVisitRequest(OfficeLocation Office, DateOnly StartDate, DateOnly EndDate);
public sealed record OfficeVisitResponse(Guid Id, OfficeLocation Office, DateOnly StartDate, DateOnly EndDate, DateTimeOffset? CancelledAt);
public sealed record UpdateLeaveRequest(DateOnly StartDate, DateOnly EndDate);
public sealed record LeaveResponse(Guid Id, OfficeLocation Office, DateOnly StartDate, DateOnly EndDate, EmployeeLeaveStatus Status);
public sealed record EmployeePurchaseResponse(Guid PurchaseId, OfficeLocation Office, string Item, decimal Amount, Currency Currency,
    DateTimeOffset PurchasedAt, string Description, Guid? ClaimId, ReimbursementClaimStatus? ClaimStatus,
    int? SubmissionAttempt, string? DecisionReason);
public sealed record CreateClaimRequest(string IdempotencyKey, Guid PurchaseId, OfficeLocation Office, decimal Amount, Currency Currency, string Description,
    Guid? RefillId = null, string? ReceiptBase64 = null, string? ReceiptMediaType = null);
public sealed record ClaimForProcessingRequest(string RowVersion);
public sealed record DecideClaimRequest(string RowVersion, bool Approve, string? Reason);
public sealed record ReimburseClaimRequest(string RowVersion, decimal PayrollAmount, Currency PayrollCurrency,
    decimal? ExchangeRate = null, DateTimeOffset? ExchangeRateAt = null, string? ExchangeRateSource = null);
public sealed record ClaimResponse(Guid Id, string EmployeeId, OfficeLocation Office, Guid PurchaseId, int SubmissionAttempt, Guid? PreviousClaimId, Guid? RefillId, decimal Amount, Currency Currency,
    string Description, ReimbursementClaimStatus Status, string? ProcessingByEmployeeId, string? DecisionReason, Guid? ExternalHrClaimId,
    DateTimeOffset SubmittedAt, DateTimeOffset? DecidedAt, bool HasReceipt, string RowVersion, Currency? EmployeePayrollCurrency = null)
{
    public static ClaimResponse From(ReimbursementClaim x, Currency? employeePayrollCurrency = null) => new(x.Id, x.EmployeeId, x.Office, x.PurchaseId, x.SubmissionAttempt, x.PreviousClaimId, x.RefillId, x.Amount, x.Currency,
        x.Description, x.Status, x.ProcessingByEmployeeId, x.DecisionReason, x.ExternalHrClaimId, x.SubmittedAt, x.DecidedAt,
        x.ReceiptSizeBytes > 0, Convert.ToBase64String(x.RowVersion), employeePayrollCurrency);
}
