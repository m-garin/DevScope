namespace DevScope.Api.Domain;

public sealed record Employee(
    string Id,
    string DisplayName,
    string Team,
    string Role,
    string YouTrackLogin,
    string GitLabUsername);
