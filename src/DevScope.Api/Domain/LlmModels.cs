namespace DevScope.Api.Domain;

public sealed record LlmModelOption(
    string Id,
    string Label,
    string Description,
    bool Recommended = false);

public static class LlmModels
{
    public const string Default = "smart";

    public static readonly IReadOnlyList<LlmModelOption> All =
    [
        new("basic", "basic", "Универсальная модель, проверена для DevScope"),
        new("smart", "smart", "Модель с reasoning для более сложного анализа", true),
        new("coding-ultima", "coding-ultima", "Кодовая модель с большим контекстом"),
        new("expert", "expert", "Экспертный профиль"),
        new("coding-expert", "coding-expert", "Экспертный профиль для кода"),
        new("coding-basic", "coding-basic", "Базовый профиль для кода"),
        new("autocomplete", "autocomplete", "Профиль автодополнения")
    ];

    public static bool IsSupported(string model) =>
        All.Any(item => string.Equals(item.Id, model, StringComparison.OrdinalIgnoreCase));
}
