using System.Text.Json;
using DevScope.Api.Configuration;
using DevScope.Api.Integrations.Mcp;
using DevScope.Api.Services.Interfaces;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace DevScope.Api.Services.Implementations;

public sealed class McpGatewayClient(
    IOptions<DevScopeOptions> options,
    ILoggerFactory loggerFactory) : IMcpGatewayClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<string>> GetServerNamesAsync(CancellationToken cancellationToken) =>
        (await LoadConfigurationAsync(cancellationToken)).Servers.Keys.Order().ToArray();

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        string serverName,
        CancellationToken cancellationToken)
    {
        try
        {
            var server = await GetServerAsync(serverName, cancellationToken);
            await using var transport = CreateTransport(serverName, server);
            await using var client = await McpClient.CreateAsync(
                transport,
                loggerFactory: loggerFactory,
                cancellationToken: cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return tools.Select(tool => new McpToolInfo(
                tool.Name,
                tool.Description,
                tool.JsonSchema.Clone())).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpIntegrationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new McpIntegrationException($"Не удалось подключиться к MCP '{serverName}'.", exception);
        }
    }

    public async Task<JsonElement> CallToolAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var server = await GetServerAsync(serverName, cancellationToken);
            await using var transport = CreateTransport(serverName, server);
            await using var client = await McpClient.CreateAsync(
                transport,
                loggerFactory: loggerFactory,
                cancellationToken: cancellationToken);

            IReadOnlyDictionary<string, object?> normalized = arguments.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            var result = await client.CallToolAsync(
                toolName,
                normalized,
                cancellationToken: cancellationToken);

            return ExtractPayload(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpIntegrationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new McpIntegrationException(
                $"Ошибка вызова MCP tool '{toolName}' на сервере '{serverName}'.",
                exception);
        }
    }

    private HttpClientTransport CreateTransport(string serverName, OpenCodeMcpServer server)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https"))
        {
            throw new McpIntegrationException($"MCP '{serverName}': указан некорректный URL.");
        }

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = $"DevScope/{serverName}",
            Endpoint = endpoint,
            AdditionalHeaders = new Dictionary<string, string>(server.Headers, StringComparer.OrdinalIgnoreCase),
            ConnectionTimeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.McpTimeoutSeconds, 5, 300))
        }, loggerFactory);
    }

    private async Task<OpenCodeMcpServer> GetServerAsync(string serverName, CancellationToken cancellationToken)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken);
        if (!configuration.Servers.TryGetValue(serverName, out var server))
        {
            throw new McpIntegrationException($"MCP-сервер '{serverName}' отсутствует в конфиге.");
        }

        if (!server.Type.Equals("remote", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpIntegrationException($"MCP '{serverName}': поддерживается только type=remote.");
        }

        return server;
    }

    private async Task<OpenCodeMcpConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.McpConfigPath;
        if (!File.Exists(path))
        {
            throw new McpIntegrationException($"MCP-конфиг не найден: {path}");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<OpenCodeMcpConfiguration>(stream, JsonOptions, cancellationToken)
                ?? throw new McpIntegrationException("MCP-конфиг пуст.");
        }
        catch (JsonException exception)
        {
            throw new McpIntegrationException("MCP-конфиг содержит некорректный JSON.", exception);
        }
        catch (IOException exception)
        {
            throw new McpIntegrationException("Не удалось прочитать MCP-конфиг.", exception);
        }
    }

    private static JsonElement ExtractPayload(object result)
    {
        var envelope = JsonSerializer.SerializeToElement(result, JsonOptions);
        if (TryGetProperty(envelope, "isError", out var isError) &&
            isError.ValueKind is JsonValueKind.True)
        {
            throw new McpIntegrationException($"MCP tool вернул ошибку: {ExtractText(envelope) ?? "без описания"}");
        }

        if (TryGetProperty(envelope, "structuredContent", out var structured) &&
            structured.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return structured.Clone();
        }

        var text = ExtractText(envelope);
        if (string.IsNullOrWhiteSpace(text))
        {
            return envelope.Clone();
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { text });
        }
    }

    private static string? ExtractText(JsonElement envelope)
    {
        if (!TryGetProperty(envelope, "content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return content.EnumerateArray()
            .Select(item => TryGetProperty(item, "text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
