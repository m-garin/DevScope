using System.Text.Json;
using DevScope.Api.Configuration;
using DevScope.Api.Integrations.Mcp;
using DevScope.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace DevScope.Api.Services.Implementations;

public sealed class McpConfigurationReader(
    IOptions<DevScopeOptions> options,
    ILogger<McpConfigurationReader> logger) : IMcpConfigurationReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<McpServerStatus>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.McpConfigPath;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var configuration = await JsonSerializer.DeserializeAsync<OpenCodeMcpConfiguration>(stream, JsonOptions, cancellationToken);
            return configuration?.Servers.Select(pair => new McpServerStatus(
                pair.Key,
                MaskUrl(pair.Value.Url),
                pair.Value.Type,
                Uri.TryCreate(pair.Value.Url, UriKind.Absolute, out _),
                pair.Value.Headers.Keys.Order().ToArray())).ToArray() ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            logger.LogWarning(exception, "Cannot read MCP configuration from {Path}", path);
            return [];
        }
    }

    private static string MaskUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "invalid";
        }

        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
    }
}
