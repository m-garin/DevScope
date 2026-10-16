namespace DevScope.Api.Integrations.Mcp;

public sealed class McpIntegrationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
