namespace DevScope.Api.Domain;

public sealed record TaskAnalysis(
    string TaskId,
    string Context,
    string Type,
    string TaskSummary,
    string EmployeeContribution,
    string Result,
    string Model,
    DateTimeOffset GeneratedAt);
