namespace DevScope.Api.Integrations.Llm;

public sealed class LlmIntegrationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
