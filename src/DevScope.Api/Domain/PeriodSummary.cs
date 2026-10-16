namespace DevScope.Api.Domain;

public sealed record PeriodSummary(
    Employee Employee,
    DateOnly From,
    DateOnly To,
    int LoggedMinutes,
    int TaskCount,
    int MergeRequestCount,
    string Narrative,
    IReadOnlyList<TaskActivity> Tasks,
    IReadOnlyList<ActivityEvent> Events,
    IReadOnlyList<string> Limitations,
    DateTimeOffset? DataUpdatedAt = null,
    bool HasExactWorklogDurations = true);
