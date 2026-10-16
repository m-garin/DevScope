using System.Text.Json;

namespace DevScope.Api.Integrations.Mcp;

public sealed record McpToolInfo(string Name, string? Description, JsonElement InputSchema);

public sealed record McpServerToolsStatus(
    string Server,
    bool Connected,
    IReadOnlyList<McpToolInfo> Tools,
    string? Error);
