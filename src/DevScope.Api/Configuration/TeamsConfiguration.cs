using System.Text.Json.Serialization;

namespace DevScope.Api.Configuration;

public sealed class TeamsConfiguration
{
    [JsonPropertyName("teams")]
    public IReadOnlyList<TeamConfiguration> Teams { get; init; } = [];
}

public sealed class TeamConfiguration
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<TeamMemberConfiguration> Members { get; init; } = [];
}

public sealed class TeamMemberConfiguration
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string YouTrackLogin { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string GitLabUsername { get; init; } = string.Empty;
    public IReadOnlyList<string> Roles { get; init; } = [];
}
