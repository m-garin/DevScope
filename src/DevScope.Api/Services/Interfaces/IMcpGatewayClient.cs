using System.Text.Json;
using DevScope.Api.Integrations.Mcp;

namespace DevScope.Api.Services.Interfaces;

public interface IMcpGatewayClient
{
    Task<IReadOnlyList<string>> GetServerNamesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(string serverName, CancellationToken cancellationToken);
    Task<JsonElement> CallToolAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);
}
