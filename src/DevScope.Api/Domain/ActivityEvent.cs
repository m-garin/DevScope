namespace DevScope.Api.Domain;

public sealed record ActivityEvent(
    string Id,
    string TaskId,
    string EmployeeId,
    DateTimeOffset OccurredAt,
    string Source,
    string Kind,
    string Title,
    string? Details,
    string? Url = null);
