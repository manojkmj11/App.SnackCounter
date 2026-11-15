using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

internal static class FrontendApiSupport
{
    public static string EmployeeId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The authenticated identity has no employee ID.");

    public static async Task<bool> HasOfficeAccessAsync(
        SnackCounterDbContext database,
        string employeeId,
        OfficeLocation office,
        CancellationToken cancellationToken,
        DateTimeOffset? asOf = null)
    {
        if (!Enum.IsDefined(office)) return false;
        var accessible = await GetAccessibleOfficesAsync(database, employeeId, asOf ?? DateTimeOffset.UtcNow, cancellationToken);
        return accessible.Contains(office);
    }

    public static async Task<OfficeLocation[]> GetAccessibleOfficesAsync(
        SnackCounterDbContext database,
        string employeeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var employee = await database.Volunteers.AsNoTracking()
            .Where(person => person.EmployeeId == employeeId && person.IsActive)
            .Select(person => new { person.HomeOffice })
            .SingleOrDefaultAsync(cancellationToken);
        if (employee is null) return [];

        var offices = await database.Offices.AsNoTracking().Where(office => office.IsActive)
            .Select(office => new { office.Location, office.TimeZoneId })
            .ToArrayAsync(cancellationToken);
        var memberships = await database.EmployeeOffices.AsNoTracking()
            .Where(entry => entry.EmployeeId == employeeId)
            .Select(entry => new { entry.Office, entry.ActiveFrom, entry.ActiveUntil })
            .ToArrayAsync(cancellationToken);
        var visits = await database.EmployeeOfficeVisits.AsNoTracking()
            .Where(visit => visit.EmployeeId == employeeId && visit.CancelledAt == null)
            .Select(visit => new { visit.Office, visit.StartDate, visit.EndDate })
            .ToArrayAsync(cancellationToken);

        return offices.Where(office => office.Location == employee.HomeOffice || memberships.Any(membership =>
                membership.Office == office.Location &&
                (membership.ActiveFrom is null || membership.ActiveFrom <= LocalDate(now, office.TimeZoneId)) &&
                (membership.ActiveUntil is null || membership.ActiveUntil >= LocalDate(now, office.TimeZoneId))) ||
                visits.Any(visit => visit.Office == office.Location && visit.StartDate <= LocalDate(now, office.TimeZoneId) &&
                    visit.EndDate >= LocalDate(now, office.TimeZoneId)))
            .Select(office => office.Location)
            .ToArray();
    }

    private static DateOnly LocalDate(DateTimeOffset now, string timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime);

    public static async Task<Conversation> GetOrCreateEmployeeConversationAsync(
        SnackCounterDbContext database,
        string employeeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conversation = await database.Conversations.SingleOrDefaultAsync(item =>
            item.Kind == ConversationKind.Employee && item.EmployeeId == employeeId, cancellationToken);
        if (conversation is not null)
        {
            return conversation;
        }

        conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Kind = ConversationKind.Employee,
            EmployeeId = employeeId,
            CreatedAt = now
        };
        database.Conversations.Add(conversation);
        return conversation;
    }

    public static async Task<Conversation> GetOrCreateOfficeConversationAsync(
        SnackCounterDbContext database,
        OfficeLocation office,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conversation = await database.Conversations.SingleOrDefaultAsync(item =>
            item.Kind == ConversationKind.Office && item.Office == office, cancellationToken);
        if (conversation is not null)
        {
            return conversation;
        }

        conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Kind = ConversationKind.Office,
            Office = office,
            CreatedAt = now
        };
        database.Conversations.Add(conversation);
        return conversation;
    }

    public static async Task<Conversation> GetOrCreateHrConversationAsync(
        SnackCounterDbContext database,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conversation = await database.Conversations.SingleOrDefaultAsync(
            item => item.Kind == ConversationKind.Hr, cancellationToken);
        if (conversation is not null)
        {
            return conversation;
        }

        conversation = new Conversation { Id = Guid.NewGuid(), Kind = ConversationKind.Hr, CreatedAt = now };
        database.Conversations.Add(conversation);
        return conversation;
    }

    public static ConversationMessage AddMessage(
        SnackCounterDbContext database,
        Conversation conversation,
        string? senderEmployeeId,
        string? recipientEmployeeId,
        OfficeLocation? office,
        MessageAudience audience,
        ConversationMessageKind kind,
        string text,
        string? payloadJson,
        DateTimeOffset now)
    {
        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Conversation = conversation,
            SenderEmployeeId = senderEmployeeId,
            RecipientEmployeeId = recipientEmployeeId,
            Office = office,
            Audience = audience,
            Kind = kind,
            Text = text,
            PayloadJson = payloadJson,
            CreatedAt = now
        };
        database.ConversationMessages.Add(message);
        return message;
    }

    public static void AddNotificationAndOutbox(
        SnackCounterDbContext database,
        string eventType,
        string deduplicationKey,
        string? recipientEmployeeId,
        OfficeLocation? office,
        bool isHrAudience,
        ConversationMessage message,
        string payloadJson,
        DateTimeOffset now)
    {
        database.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            RecipientEmployeeId = recipientEmployeeId,
            Office = office,
            IsHrAudience = isHrAudience,
            ConversationMessageId = message.Id,
            EventType = eventType,
            PayloadJson = payloadJson,
            DeduplicationKey = deduplicationKey,
            DeliveryState = NotificationDeliveryState.Pending,
            CreatedAt = now
        });
        database.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventId = message.Id,
            EventType = eventType,
            PayloadJson = payloadJson,
            OccurredAt = now,
            AvailableAt = now
        });
    }

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload);
}
