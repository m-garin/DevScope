using DevScope.Api.Integrations.Mcp;

namespace DevScope.Api.Services.Interfaces;

public interface IMcpConfigurationReader
{
    Task<IReadOnlyList<McpServerStatus>> GetStatusesAsync(CancellationToken cancellationToken);
}
