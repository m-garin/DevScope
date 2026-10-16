using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;

namespace DevScope.Api.Services.Implementations;

public sealed class MockTaskAnalysisService : ITaskAnalysisService
{
    public async Task<TaskAnalysis> AnalyzeAsync(TaskActivity activity, string model, CancellationToken cancellationToken)
    {
        await Task.Delay(550, cancellationToken);

        var task = activity.Task;
        var workTypes = string.Join(", ", activity.Worklogs
            .Select(item => item.Type)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var workDetails = string.Join("; ", activity.Worklogs
            .Select(item => item.Description)
            .Where(item => !string.IsNullOrWhiteSpace(item)));
        var gitLab = task.GitLab;
        var context = string.IsNullOrWhiteSpace(task.ParentSummary)
            ? task.Summary
            : $"{task.ParentSummary} › {task.Summary}";

        return new TaskAnalysis(
            task.Id,
            context,
            task.Type ?? "Задача",
            PurposeFor(task.Id, task.Summary),
            $"Сотрудник списал {FormatDuration(activity.LoggedMinutes)}. Типы работ: {workTypes}. Выполненные действия: {workDetails}.",
            gitLab is null
                ? $"Работа зафиксирована в YouTrack. Связанный MR не найден; текущий статус задачи — {task.Status}."
                : $"Работа подтверждается связанной активностью GitLab. MR сейчас имеет статус {gitLab.MergeRequestState}; статус задачи — {task.Status}.",
            $"mock/{model}",
            DateTimeOffset.UtcNow);
    }

    private static string PurposeFor(string taskId, string summary) => taskId switch
    {
        "FINTDR-50102" => "Перевести backend API на .NET 10, сохранив совместимость контрактов и работу существующих сервисов.",
        "FINTDR-49880" => "Исправить сценарий OAuth-авторизации, из-за которого пользователь мог получить ошибку при возврате в приложение.",
        "FINTDR-50444" => "Проверить реализацию обработки платёжных поручений и убедиться, что замечания к поведению системы устранены до слияния изменений.",
        _ => $"Выполнить задачу «{summary}» и довести изменение до рабочего результата."
    };

    private static string FormatDuration(int minutes)
    {
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return remainder == 0 ? $"{hours} ч" : $"{hours} ч {remainder} мин";
    }
}
