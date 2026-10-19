namespace DevScope.Api.Configuration;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://code-generator.sovcombank.group/api/v1";
    public string Endpoint { get; set; } = "/chat/completions";
    public string? Token { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxInputChars { get; set; } = 8_000;
    public int MaxOutputTokens { get; set; } = 600;
}
