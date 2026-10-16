using System.Text.Json.Serialization;

namespace DevScope.Api.Integrations.Mcp;

public sealed class OpenCodeMcpConfiguration
{
    [JsonPropertyName("mcp")]
    public Dictionary<string, OpenCodeMcpServer> Servers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OpenCodeMcpServer
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "remote";

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record McpServerStatus(
    string Name,
    string Url,
    string Type,
    bool Configured,
    IReadOnlyList<string> HeaderNames);
