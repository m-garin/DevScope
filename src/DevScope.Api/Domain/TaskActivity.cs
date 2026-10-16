namespace DevScope.Api.Domain;

public sealed record TaskActivity(
    WorkTask Task,
    int LoggedMinutes,
    IReadOnlyList<Worklog> Worklogs,
    bool IsOwnTask);
