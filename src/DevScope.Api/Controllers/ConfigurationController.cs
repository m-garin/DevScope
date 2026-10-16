using DevScope.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Controllers;

[ApiController]
[Route("api/configuration")]
public sealed class ConfigurationController(
    IMcpConfigurationReader configurationReader,
    IMcpGatewayClient mcpGatewayClient,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("mcp")]
    public async Task<IActionResult> GetMcp(CancellationToken cancellationToken) =>
        Ok(await configurationReader.GetStatusesAsync(cancellationToken));

    [HttpGet("runtime")]
    public IActionResult GetRuntime() => Ok(new
    {
        dataSource = configuration["DevScope:DataSource"] ?? "Mock",
        isMock = (configuration["DevScope:McpConfigPath"] ?? string.Empty)
            .Contains(".mock.", StringComparison.OrdinalIgnoreCase)
    });

    [HttpGet("mcp/tools")]
    public async Task<IActionResult> GetMcpTools(CancellationToken cancellationToken)
    {
        var result = new List<object>();
        IReadOnlyList<string> servers;
        try
        {
            servers = await mcpGatewayClient.GetServerNamesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Problem(
                title: "Не удалось прочитать MCP-конфиг",
                detail: exception.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }

        foreach (var server in servers)
        {
            try
            {
                var tools = await mcpGatewayClient.ListToolsAsync(server, cancellationToken);
                result.Add(new { server, connected = true, tools, error = (string?)null });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.Add(new { server, connected = false, tools = Array.Empty<object>(), error = exception.Message });
            }
        }

        return Ok(result);
    }
}
