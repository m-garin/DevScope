namespace DevScope.Api.Domain;

public sealed record WorkTask(
    string Id,
    string Summary,
    string Status,
    string? DeveloperId,
    int? EstimateMinutes,
    string Url,
    GitLabReference? GitLab,
    string? ParentSummary = null,
    string? Type = null,
    bool DigitalTwinStarted = false,
    string? Description = null);
