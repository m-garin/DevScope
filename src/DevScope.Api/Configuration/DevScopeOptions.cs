namespace DevScope.Api.Configuration;

public sealed class DevScopeOptions
{
    public const string SectionName = "DevScope";
    public string DataSource { get; set; } = "Mock";
    public string McpConfigPath { get; set; } = "config/mcp.json";
    public string TeamsConfigPath { get; set; } = "config/teams.json";
    public string ProjectKey { get; set; } = "FINTDR";
    public int McpTimeoutSeconds { get; set; } = 120;
    public int DailyRefreshHour { get; set; } = 3;
}
