namespace DevScope.Api.Domain;

public sealed record Worklog(
    string Id,
    string TaskId,
    string AuthorId,
    DateOnly Date,
    int DurationMinutes,
    string Type,
    string? Description);
