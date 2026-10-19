namespace DevScope.Api.Domain;

public sealed record TaskAnalysis(
    string TaskId,
    string Context,
    string Type,
    string TaskSummary,
    string MergeSummary,
    string StatusAndChecks,
    string Model,
    DateTimeOffset GeneratedAt);
