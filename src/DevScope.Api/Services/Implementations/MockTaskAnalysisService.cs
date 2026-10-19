using System.Text.RegularExpressions;
using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;

namespace DevScope.Api.Services.Implementations;

public sealed partial class MockTaskAnalysisService : ITaskAnalysisService
{
    private const int SectionLimit = 240;

    public async Task<TaskAnalysis> AnalyzeAsync(TaskActivity activity, string model, CancellationToken cancellationToken)
    {
        await Task.Delay(550, cancellationToken);

        var task = activity.Task;
        var context = string.IsNullOrWhiteSpace(task.ParentSummary)
            ? task.Summary
            : $"{task.ParentSummary} › {task.Summary}";

        return new TaskAnalysis(
            task.Id,
            context,
            task.Type ?? "Задача",
            BuildTaskSummary(task),
            BuildMergeSummary(task.GitLab),
            BuildStatusAndChecks(task),
            $"mock/{model}",
            DateTimeOffset.UtcNow);
    }

    private static string BuildTaskSummary(WorkTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            return Compact(task.Description, SectionLimit);
        }

        return Compact(task.Summary.Trim().TrimEnd('.'), SectionLimit);
    }

    private static string BuildMergeSummary(GitLabReference? gitLab)
    {
        if (gitLab is null)
        {
            return "Связанный MR не найден; состав изменений недоступен.";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(gitLab.Title))
        {
            parts.Add($"MR !{gitLab.MergeRequestIid}: {gitLab.Title.Trim().TrimEnd('.')}");
        }
        else
        {
            parts.Add($"MR !{gitLab.MergeRequestIid}: {FormatCount(gitLab.CommitCount, "коммит", "коммита", "коммитов")}");
        }

        if (!string.IsNullOrWhiteSpace(gitLab.Description))
        {
            parts.Add(Compact(gitLab.Description, 140).TrimEnd('.'));
        }
        else if (gitLab.CommitTitles is { Count: > 0 })
        {
            parts.Add($"по коммитам: {string.Join("; ", gitLab.CommitTitles.Take(2))}");
        }
        else
        {
            parts.Add("описание изменений недоступно");
        }

        var employeeActions = BuildEmployeeActions(gitLab);
        if (employeeActions is not null)
        {
            parts.Add($"сотрудник: {employeeActions}");
        }

        return Compact(string.Join(". ", parts) + ".", SectionLimit);
    }

    private static string? BuildEmployeeActions(GitLabReference gitLab)
    {
        if (!gitLab.EmployeeActionFactsKnown)
        {
            return null;
        }

        var actions = new List<string>();
        if (gitLab.EmployeeCommitCount > 0)
        {
            actions.Add(FormatCount(gitLab.EmployeeCommitCount, "коммит", "коммита", "коммитов"));
        }
        if (gitLab.EmployeeCommentCount > 0)
        {
            actions.Add(FormatCount(gitLab.EmployeeCommentCount, "комментарий", "комментария", "комментариев"));
        }
        if (gitLab.EmployeeApproved)
        {
            actions.Add("одобрил MR");
        }
        if (gitLab.EmployeeResolvedThreads)
        {
            actions.Add("разрешил обсуждения");
        }

        return actions.Count > 0 ? string.Join(", ", actions) : "подтверждённых действий нет";
    }

    private static string BuildStatusAndChecks(WorkTask task)
    {
        var gitLab = task.GitLab;
        if (gitLab is null)
        {
            return Compact($"Задача: {task.Status}. Проверить реализацию без MR невозможно.", SectionLimit);
        }

        var facts = new List<string> { $"MR {TranslateState(gitLab.MergeRequestState)}" };
        if (!string.IsNullOrWhiteSpace(gitLab.PipelineStatus))
        {
            facts.Add($"pipeline: {gitLab.PipelineStatus}");
        }
        if (gitLab.UnresolvedDiscussionCount is { } unresolved)
        {
            facts.Add($"нерешённых обсуждений: {unresolved}");
        }
        else if (gitLab.DiscussionCount > 0)
        {
            facts.Add($"обсуждений: {gitLab.DiscussionCount}");
        }
        if (gitLab.HasConflicts is { } hasConflicts)
        {
            facts.Add(hasConflicts ? "есть конфликты" : "конфликтов нет");
        }

        var checks = new List<string>();
        if (string.IsNullOrWhiteSpace(gitLab.PipelineStatus))
        {
            checks.Add("pipeline");
        }
        if (gitLab.UnresolvedDiscussionCount is null)
        {
            checks.Add("замечания ревью");
        }
        if (string.IsNullOrWhiteSpace(gitLab.Description) && gitLab.CommitTitles is not { Count: > 0 })
        {
            checks.Add("соответствие MR задаче");
        }

        var result = string.Join("; ", facts) + ".";
        if (checks.Count > 0 && gitLab.MergeRequestState is not ("merged" or "closed"))
        {
            result += $" Проверить: {string.Join(", ", checks)}.";
        }

        return Compact(result, SectionLimit);
    }

    private static string TranslateState(string state) => state.Trim().ToLowerInvariant() switch
    {
        "open" or "opened" => "открыт",
        "merged" => "смержен",
        "closed" => "закрыт",
        "draft" => "в черновике",
        _ => $"в статусе {state}"
    };

    private static string Compact(string value, int limit)
    {
        var normalized = WhitespaceRegex().Replace(value, " ").Trim();
        if (normalized.Length <= limit)
        {
            return normalized;
        }

        var shortened = normalized[..(limit - 1)];
        var lastSpace = shortened.LastIndexOf(' ');
        return $"{shortened[..(lastSpace > limit / 2 ? lastSpace : shortened.Length)].TrimEnd(' ', ',', ';', ':')}…";
    }

    private static string FormatCount(int count, string one, string few, string many)
    {
        var mod100 = count % 100;
        var form = mod100 is >= 11 and <= 14
            ? many
            : (count % 10) switch
            {
                1 => one,
                2 or 3 or 4 => few,
                _ => many
            };
        return $"{count} {form}";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
