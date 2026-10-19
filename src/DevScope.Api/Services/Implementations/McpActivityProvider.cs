using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevScope.Api.Configuration;
using DevScope.Api.Domain;
using DevScope.Api.Integrations.Mcp;
using DevScope.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace DevScope.Api.Services.Implementations;

public sealed partial class McpActivityProvider(
    IMcpGatewayClient mcp,
    ITeamDirectory teamDirectory,
    IOptions<DevScopeOptions> options,
    ILogger<McpActivityProvider> logger) : IActivityProvider
{
    private const string YouTrackServer = "youtrack";
    private const string GitLabServer = "gitlab";
    private const int PageSize = 20;
    private const int MaxDetailedPeriodDays = 31;
    private static readonly string[] WorkItemToolCandidates =
        ["youtrack_search_work_items", "search_work_items", "youtrack_search_worklogs", "search_worklogs"];
    private static readonly string[] SearchIssueToolCandidates = ["youtrack_search_issues", "search_issues"];
    private static readonly string[] IssueToolCandidates = ["youtrack_get_issue", "get_issue"];
    private static readonly string[] CommentToolCandidates = ["youtrack_get_issue_comments", "get_issue_comments"];
    private static readonly string[] MergeRequestListToolCandidates = ["gitlab_gitlab_mr_list", "gitlab_mr_list", "mr_list"];
    private static readonly string[] MergeRequestToolCandidates = ["gitlab_gitlab_mr_details", "gitlab_mr_details", "mr_details"];
    private static readonly string[] MergeRequestCommentsToolCandidates = ["gitlab_gitlab_mr_comments", "gitlab_mr_comments", "mr_comments"];
    private readonly ConcurrentDictionary<string, TaskActivity> _taskCache = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken) =>
        teamDirectory.GetEmployeesAsync(cancellationToken);

    public void ClearMemoryCache() => _taskCache.Clear();

    public async Task<PeriodSummary?> GetPeriodSummaryAsync(
        string employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var employee = await teamDirectory.FindEmployeeAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(employee.YouTrackLogin))
        {
            throw new McpIntegrationException($"Для сотрудника '{employee.DisplayName}' YouTrack не вернул login.");
        }

        var tools = await mcp.ListToolsAsync(YouTrackServer, cancellationToken);
        var toolNames = tools.Select(tool => tool.Name).ToArray();
        var issueTool = ResolveTool(toolNames, IssueToolCandidates);
        var commentTool = ResolveTool(toolNames, CommentToolCandidates);
        var mergeRequestTool = await ResolveMergeRequestToolAsync(cancellationToken);
        var mergeRequestCommentsTool = await ResolveMergeRequestCommentsToolAsync(cancellationToken);
        var mergeRequestListTool = await ResolveMergeRequestListToolAsync(cancellationToken);
        var workItemTool = ResolveTool(toolNames, WorkItemToolCandidates);
        if (workItemTool is not null)
        {
            return await GetExactPeriodSummaryAsync(
                workItemTool, issueTool, commentTool, mergeRequestListTool, mergeRequestTool, mergeRequestCommentsTool,
                employee, from, to, cancellationToken);
        }

        var periodDays = to.DayNumber - from.DayNumber + 1;
        if (periodDays > MaxDetailedPeriodDays)
        {
            throw new McpIntegrationException(
                $"Для дневной раскладки списаний выберите период не более {MaxDetailedPeriodDays} дней.");
        }

        var searchIssueTool = ResolveTool(toolNames, SearchIssueToolCandidates)
            ?? throw MissingSearchIssuesTool(toolNames);
        var periodIssues = await ReadAllIssuesAsync(searchIssueTool, employee, from, to, cancellationToken);
        var issueById = periodIssues
            .Select(item => (Id: GetTaskId(item), Item: item))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Item, StringComparer.OrdinalIgnoreCase);

        var datesByTask = new Dictionary<string, HashSet<DateOnly>>(StringComparer.OrdinalIgnoreCase);
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var dailyIssues = await ReadAllIssuesAsync(searchIssueTool, employee, date, date, cancellationToken);
            foreach (var issue in dailyIssues)
            {
                var taskId = GetTaskId(issue);
                if (string.IsNullOrWhiteSpace(taskId))
                {
                    continue;
                }
                if (!datesByTask.TryGetValue(taskId, out var dates))
                {
                    dates = [];
                    datesByTask[taskId] = dates;
                }
                dates.Add(date);
                issueById.TryAdd(taskId, issue);
            }
        }

        var limitations = new List<string>
        {
            "YouTrack MCP подтверждает наличие списаний по автору и дням, но не возвращает минуты, тип и текст отдельных worklog. Накопительные поля задачи не используются как часы выбранного периода."
        };

        var taskActivities = new List<TaskActivity>();
        var gitLabEvents = new List<ActivityEvent>();
        var currentReviewAssignments = new List<TaskActivity>();
        foreach (var pair in issueById)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskId = pair.Key;
            var issuePayload = pair.Value;
            var task = ParseIssue(pair.Value, employee);
            var issueAvailable = true;

            if (issueTool is not null)
            {
                var issue = await TryReadIssueAsync(issueTool, taskId, cancellationToken);
                issueAvailable = issue is not null;
                if (issue is not null)
                {
                    issuePayload = issue.Value;
                    task = ParseIssue(issue.Value, employee) ?? task;
                }
                else
                {
                    limitations.Add($"Задача {taskId} удалена или недоступна в YouTrack; показаны доступные данные worklog.");
                }
            }

            if (issueAvailable && task is not null && task.GitLab is null && commentTool is not null)
            {
                var comments = await mcp.CallToolAsync(
                    YouTrackServer,
                    commentTool,
                    new Dictionary<string, object?>
                    {
                        ["issueId"] = taskId,
                        ["offset"] = 0,
                        ["limit"] = 10
                    },
                    cancellationToken);
                task = task with { GitLab = ParseGitLabReferenceFromText(comments.GetRawText(), "comment") };
            }

            if (task is not null && task.GitLab is null && mergeRequestListTool is not null)
            {
                task = task with
                {
                    GitLab = await FindMergeRequestByTaskKeyAsync(
                        mergeRequestListTool, issuePayload, taskId, cancellationToken)
                };
            }

            if (task?.GitLab is not null && mergeRequestTool is not null)
            {
                var details = await mcp.CallToolAsync(
                    GitLabServer,
                    mergeRequestTool,
                    new Dictionary<string, object?>
                    {
                        ["project_id"] = task.GitLab.ProjectPath,
                        ["mr_iid"] = task.GitLab.MergeRequestIid,
                        ["include_changes"] = false,
                        ["include_commits"] = true,
                        ["include_discussions"] = true
                    },
                    cancellationToken);
                var supplementalNotes = await ReadMergeRequestCommentsAsync(
                    mergeRequestCommentsTool, task.GitLab, cancellationToken);
                task = task with { GitLab = EnrichGitLabReference(task.GitLab, details, employee, supplementalNotes) };
                gitLabEvents.AddRange(ParseGitLabEvents(details, task.Id, employee, task.GitLab, supplementalNotes));
            }

            if (task?.GitLab is { } gitLab)
            {
                task = task with { GitLab = EnsureMergeRequestUrl(gitLab, issuePayload) };
            }

            task ??= new WorkTask(taskId, taskId, "Неизвестно", null, null, string.Empty, null);
            var taskWorklogs = datesByTask.TryGetValue(taskId, out var taskDates)
                ? taskDates.OrderByDescending(date => date).Select(date => new Worklog(
                    $"presence:{employee.Id}:{taskId}:{date:yyyyMMdd}",
                    taskId,
                    employee.Id,
                    date,
                    0,
                    "Списание",
                    "Наличие списания подтверждено фильтрами work author и work date."))
                    .ToArray()
                : [];
            if (taskWorklogs.Length == 0)
            {
                continue;
            }
            var activity = new TaskActivity(
                task,
                0,
                taskWorklogs,
                IsEmployeeDeveloper(task.DeveloperId, employee));
            taskActivities.Add(activity);
            _taskCache[$"{employee.Id}:{task.Id}"] = activity;
        }

        var reviewerSearchAvailable = await AddReviewerAssignmentsAsync(
            taskActivities, currentReviewAssignments, gitLabEvents, issueTool, mergeRequestTool,
            mergeRequestCommentsTool, employee, from, to, cancellationToken);
        if (!reviewerSearchAvailable && !string.IsNullOrWhiteSpace(employee.GitLabUsername))
        {
            limitations.Add("GitLab MCP не предоставляет глобальный поиск MR по reviewer_username; задачи без списаний, где сотрудник только ревьюер, могут отсутствовать.");
        }

        var ordered = taskActivities
            .OrderByDescending(activity => activity.Worklogs.Select(worklog => worklog.Date).DefaultIfEmpty(DateOnly.MinValue).Max())
            .ThenBy(activity => activity.Task.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var worklogTaskCount = ordered.Count(item => item.Worklogs.Count > 0);
        var gitLabOnlyTaskCount = ordered.Length - worklogTaskCount;
        var activeDays = ordered.SelectMany(item => item.Worklogs).Select(item => item.Date).Distinct().Count();
        var narrative = $"Списания подтверждены по {worklogTaskCount} задачам в {activeDays} дн. выбранного периода." +
            (gitLabOnlyTaskCount > 0
                ? $" Ещё по {gitLabOnlyTaskCount} задачам подтверждена активность сотрудника в GitLab."
                : string.Empty);
        var events = ordered
            .SelectMany(activity => activity.Worklogs.Select(worklog => new ActivityEvent(
                worklog.Id,
                activity.Task.Id,
                employee.Id,
                new DateTimeOffset(worklog.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                "YouTrack",
                "WorklogPresence",
                $"Есть списание в {activity.Task.Id}",
                activity.Task.Summary)))
            .Concat(gitLabEvents)
            .Where(item => IsInPeriod(item, from, to))
            .OrderByDescending(item => item.OccurredAt)
            .ToArray();

        return new PeriodSummary(
            employee,
            from,
            to,
            0,
            ordered.Length,
            ordered.Count(item => item.Task.GitLab is not null),
            narrative,
            ordered,
            events,
            limitations.Distinct().ToArray(),
            HasExactWorklogDurations: false,
            CurrentReviewAssignments: currentReviewAssignments);
    }

    private async Task<PeriodSummary> GetExactPeriodSummaryAsync(
        string workItemTool,
        string? issueTool,
        string? commentTool,
        string? mergeRequestListTool,
        string? mergeRequestTool,
        McpToolInfo? mergeRequestCommentsTool,
        Employee employee,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var rawWorkItems = await ReadAllWorkItemsAsync(
            workItemTool, employee, from, to, cancellationToken);
        var worklogs = rawWorkItems
            .Select(item => ParseWorklog(item, employee))
            .Where(item => item is not null)
            .Cast<Worklog>()
            .Where(item => item.Date >= from && item.Date <= to)
            .ToArray();
        var limitations = rawWorkItems.Count > 0 && worklogs.Length == 0
            ? new List<string> { "MCP вернул work items, но их формат не удалось распознать." }
            : [];
        var activities = new List<TaskActivity>();
        var events = new List<ActivityEvent>();
        var currentReviewAssignments = new List<TaskActivity>();

        foreach (var group in worklogs.GroupBy(item => item.TaskId, StringComparer.OrdinalIgnoreCase))
        {
            var taskId = group.Key;
            var raw = rawWorkItems.FirstOrDefault(item =>
                string.Equals(GetTaskId(item), taskId, StringComparison.OrdinalIgnoreCase));
            var issuePayload = raw.ValueKind == JsonValueKind.Undefined ? default : raw;
            var task = raw.ValueKind == JsonValueKind.Undefined ? null : ParseEmbeddedTask(raw, employee);
            var issueAvailable = true;
            if (issueTool is not null)
            {
                var issue = await TryReadIssueAsync(issueTool, taskId, cancellationToken);
                issueAvailable = issue is not null;
                if (issue is not null)
                {
                    issuePayload = issue.Value;
                    task = ParseIssue(issue.Value, employee) ?? task;
                }
                else
                {
                    limitations.Add($"Задача {taskId} удалена или недоступна в YouTrack; показаны доступные данные worklog.");
                }
            }
            if (issueAvailable && task is not null && task.GitLab is null && commentTool is not null)
            {
                var comments = await mcp.CallToolAsync(
                    YouTrackServer, commentTool,
                    new Dictionary<string, object?>
                    {
                        ["issueId"] = taskId,
                        ["offset"] = 0,
                        ["limit"] = 10
                    },
                    cancellationToken);
                task = task with { GitLab = ParseGitLabReferenceFromText(comments.GetRawText(), "comment") };
            }
            if (task is not null && task.GitLab is null && mergeRequestListTool is not null &&
                issuePayload.ValueKind != JsonValueKind.Undefined)
            {
                task = task with
                {
                    GitLab = await FindMergeRequestByTaskKeyAsync(
                        mergeRequestListTool, issuePayload, taskId, cancellationToken)
                };
            }
            if (task?.GitLab is not null && mergeRequestTool is not null)
            {
                var details = await mcp.CallToolAsync(
                    GitLabServer, mergeRequestTool,
                    new Dictionary<string, object?>
                    {
                        ["project_id"] = task.GitLab.ProjectPath,
                        ["mr_iid"] = task.GitLab.MergeRequestIid,
                        ["include_changes"] = false,
                        ["include_commits"] = true,
                        ["include_discussions"] = true
                    },
                    cancellationToken);
                var supplementalNotes = await ReadMergeRequestCommentsAsync(
                    mergeRequestCommentsTool, task.GitLab, cancellationToken);
                task = task with { GitLab = EnrichGitLabReference(task.GitLab, details, employee, supplementalNotes) };
                events.AddRange(ParseGitLabEvents(details, task.Id, employee, task.GitLab, supplementalNotes));
            }

            if (task?.GitLab is { } gitLab)
            {
                task = task with { GitLab = EnsureMergeRequestUrl(gitLab, issuePayload) };
            }

            task ??= new WorkTask(taskId, taskId, "Неизвестно", null, null, string.Empty, null);
            var taskWorklogs = group
                .OrderByDescending(item => item.Date)
                .Select(item => item with
                {
                    Url = item.Url ?? BuildWorkItemUrl(task.Url, item.Id)
                })
                .ToArray();
            events.AddRange(taskWorklogs.Select(item => new ActivityEvent(
                item.Id,
                taskId,
                employee.Id,
                new DateTimeOffset(item.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                "YouTrack",
                "Worklog",
                $"Списано {item.DurationMinutes} мин в {taskId}",
                item.Description,
                item.Url)));
            var activity = new TaskActivity(
                task,
                taskWorklogs.Sum(item => item.DurationMinutes),
                taskWorklogs,
                IsEmployeeDeveloper(task.DeveloperId, employee));
            activities.Add(activity);
            _taskCache[$"{employee.Id}:{task.Id}"] = activity;
        }

        var reviewerSearchAvailable = await AddReviewerAssignmentsAsync(
            activities, currentReviewAssignments, events, issueTool, mergeRequestTool,
            mergeRequestCommentsTool, employee, from, to, cancellationToken);
        if (!reviewerSearchAvailable && !string.IsNullOrWhiteSpace(employee.GitLabUsername))
        {
            limitations.Add("GitLab MCP не предоставляет глобальный поиск MR по reviewer_username; задачи без списаний, где сотрудник только ревьюер, могут отсутствовать.");
        }

        var ordered = activities
            .OrderByDescending(item => item.Worklogs.Select(worklog => worklog.Date).DefaultIfEmpty(DateOnly.MinValue).Max())
            .ThenBy(item => item.Task.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var worklogTaskCount = ordered.Count(item => item.Worklogs.Count > 0);
        var gitLabOnlyTaskCount = ordered.Length - worklogTaskCount;
        return new PeriodSummary(
            employee,
            from,
            to,
            worklogs.Sum(item => item.DurationMinutes),
            ordered.Length,
            ordered.Count(item => item.Task.GitLab is not null),
            $"Списано время по {worklogTaskCount} задачам." +
            (gitLabOnlyTaskCount > 0
                ? $" Ещё по {gitLabOnlyTaskCount} задачам подтверждена активность сотрудника в GitLab."
                : string.Empty),
            ordered,
            events.Where(item => IsInPeriod(item, from, to)).OrderByDescending(item => item.OccurredAt).ToArray(),
            limitations,
            CurrentReviewAssignments: currentReviewAssignments);
    }

    private async Task<List<JsonElement>> ReadAllWorkItemsAsync(
        string toolName,
        Employee employee,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        var offset = 0;
        while (true)
        {
            var payload = await mcp.CallToolAsync(
                YouTrackServer,
                toolName,
                new Dictionary<string, object?>
                {
                    ["projectKey"] = options.Value.ProjectKey,
                    ["authorLogin"] = employee.YouTrackLogin,
                    ["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["offset"] = offset,
                    ["limit"] = 100
                },
                cancellationToken);
            var page = payload.FindArray("workItemsPage", "workItems", "worklogs", "items", "data")
                ?? throw new McpIntegrationException(
                    $"MCP tool '{toolName}' вернул ответ без массива workItemsPage/workItems/worklogs/items.");
            var items = page.EnumerateArray().Select(item => item.Clone()).ToArray();
            result.AddRange(items);
            var hasNext = payload.FirstProperty("hasNextPage", "hasMore") is { } next &&
                next.ValueKind == JsonValueKind.True;
            if (!hasNext || items.Length == 0)
            {
                break;
            }
            offset = payload.FirstProperty("nextPageOffset", "nextOffset").IntValue() ?? offset + items.Length;
        }
        return result;
    }

    public Task<TaskActivity?> GetTaskActivityAsync(
        string employeeId,
        string taskId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _taskCache.TryGetValue($"{employeeId}:{taskId}", out var activity);
        return Task.FromResult(activity);
    }

    private async Task<List<JsonElement>> ReadAllIssuesAsync(
        string toolName,
        Employee employee,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        var offset = 0;
        while (true)
        {
            var payload = await mcp.CallToolAsync(
                YouTrackServer,
                toolName,
                new Dictionary<string, object?>
                {
                    ["query"] = BuildWorklogIssueQuery(options.Value.ProjectKey, employee.YouTrackLogin, from, to),
                    ["customFieldsToReturn"] = Array.Empty<string>(),
                    ["offset"] = offset,
                    ["limit"] = PageSize
                },
                cancellationToken);

            var page = payload.FindArray("issuesPage", "issues", "items", "data");
            if (page is null)
            {
                throw new McpIntegrationException(
                    $"MCP tool '{toolName}' вернул ответ без массива issuesPage/issues/items.");
            }

            var items = page.Value.EnumerateArray().Select(item => item.Clone()).ToArray();
            result.AddRange(items);
            var hasNext = payload.FirstProperty("hasNextPage", "hasMore") is { } next &&
                next.ValueKind == JsonValueKind.True;
            if (!hasNext || items.Length == 0)
            {
                break;
            }

            offset = payload.FirstProperty("nextPageOffset", "nextOffset").IntValue() ?? offset + items.Length;
            if (result.Count > 10_000)
            {
                throw new McpIntegrationException("MCP вернул более 10 000 задач; уменьшите период.");
            }
        }

        return result;
    }

    private static string BuildWorklogIssueQuery(
        string projectKey,
        string authorLogin,
        DateOnly from,
        DateOnly to) =>
        $"project: {projectKey} work author: {authorLogin} work date: " +
        $"{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} .. " +
        $"{to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} sort by: updated desc";

    private static Worklog? ParseWorklog(JsonElement item, Employee employee)
    {
        var taskId = GetTaskId(item);
        var date = ParseDate(item.FirstProperty("date", "workDate", "created", "createdAt"));
        var minutes = ParseDurationMinutes(item);
        if (string.IsNullOrWhiteSpace(taskId) || date is null || minutes is null)
        {
            return null;
        }

        var authorLogin = item.FirstProperty("authorLogin").StringValue();
        if (item.FirstProperty("author") is { ValueKind: JsonValueKind.Object } author)
        {
            authorLogin ??= author.FirstProperty("login", "username", "name").StringValue();
        }
        if (!string.IsNullOrWhiteSpace(authorLogin) &&
            !authorLogin.Equals(employee.YouTrackLogin, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = item.FirstProperty("id", "workItemId").StringValue()
            ?? $"{taskId}:{date:yyyyMMdd}:{minutes}:{Guid.NewGuid():N}";
        var type = item.FirstProperty("type", "workType", "workItemType").StringValue();
        if (item.FirstProperty("type") is { ValueKind: JsonValueKind.Object } typeObject)
        {
            type = typeObject.FirstProperty("name", "value").StringValue();
        }

        return new Worklog(
            id,
            taskId,
            employee.Id,
            date.Value,
            minutes.Value,
            type ?? "Работа",
            item.FirstProperty("description", "text", "comment").StringValue(),
            item.FirstProperty("web_url", "webUrl", "url").StringValue());
    }

    private static WorkTask? ParseEmbeddedTask(JsonElement workItem, Employee employee)
    {
        if (workItem.FirstProperty("issue", "task") is not { ValueKind: JsonValueKind.Object } issue)
        {
            return null;
        }

        return ParseIssue(issue, employee);
    }

    private static WorkTask? ParseIssue(JsonElement issue, Employee employee)
    {
        var id = issue.FirstProperty("idReadable", "issueId", "id").StringValue();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var fields = issue.FirstProperty("customFields") is { ValueKind: JsonValueKind.Object } customFields
            ? customFields
            : default(JsonElement?);
        string? Field(params string[] names)
        {
            if (fields is null)
            {
                return null;
            }

            foreach (var name in names)
            {
                var value = fields.Value.FirstProperty(name);
                if (value is null)
                {
                    continue;
                }
                if (value.Value.ValueKind == JsonValueKind.Object)
                {
                    return value.Value.FirstProperty("name", "login", "value").StringValue();
                }
                return value.StringValue();
            }
            return null;
        }

        var summary = issue.FirstProperty("summary", "title").StringValue() ?? id;
        var status = issue.FirstProperty("status", "state").StringValue() ?? Field("State", "Состояние_FT", "Статус") ?? "Неизвестно";
        var developer = issue.FirstProperty("developerId", "developer", "assignee").StringValue()
            ?? Field("$Разработчик", "Разработчик", "Исполнитель FT", "Assignee");
        var estimate = ParseExecutionEstimateMinutes(issue, fields);
        var url = issue.FirstProperty("url", "webUrl").StringValue() ?? string.Empty;
        var parent = issue.FirstProperty("parentSummary").StringValue();
        if (issue.FirstProperty("parentIssue", "parent") is { ValueKind: JsonValueKind.Object } parentIssue)
        {
            parent ??= parentIssue.FirstProperty("summary", "title").StringValue();
        }

        return new WorkTask(
            id,
            summary,
            status,
            developer,
            estimate,
            url,
            ParseGitLabReference(issue),
            parent,
            issue.FirstProperty("type").StringValue() ?? Field("Type", "Тип1", "$Тип"),
            HasTag(issue, "SODA"));
    }

    private static bool HasTag(JsonElement issue, string expectedTag)
    {
        if (issue.FirstProperty("tags") is not { ValueKind: JsonValueKind.Array } tags)
        {
            return false;
        }

        foreach (var tag in tags.EnumerateArray())
        {
            var name = tag.ValueKind == JsonValueKind.Object
                ? tag.FirstProperty("name", "value").StringValue()
                : tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
            if (string.Equals(name, expectedTag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static GitLabReference? ParseGitLabReference(JsonElement issue)
    {
        if (issue.FirstProperty("gitLab", "gitlab") is { ValueKind: JsonValueKind.Object } gitLab)
        {
            var project = gitLab.FirstProperty("projectPath", "project_id").StringValue();
            var iid = gitLab.FirstProperty("mergeRequestIid", "mr_iid", "iid").IntValue();
            if (!string.IsNullOrWhiteSpace(project) && iid is not null)
            {
                return new GitLabReference(
                    project,
                    gitLab.FirstProperty("branch", "source_branch").StringValue() ?? string.Empty,
                    iid.Value,
                    gitLab.FirstProperty("mergeRequestState", "state").StringValue() ?? "unknown",
                    gitLab.FirstProperty("commitCount").IntValue() ?? 0,
                    "issue",
                    Url: gitLab.FirstProperty("web_url", "webUrl", "url").StringValue());
            }
        }

        var text = string.Join(' ', new[]
        {
            issue.FirstProperty("description").StringValue(),
            issue.FirstProperty("summary").StringValue(),
            issue.FirstProperty("customFields") is { } customFields ? customFields.GetRawText() : null
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return ParseGitLabReferenceFromText(text, "description");
    }

    private async Task<string?> ResolveMergeRequestToolAsync(CancellationToken cancellationToken)
    {
        var servers = await mcp.GetServerNamesAsync(cancellationToken);
        if (!servers.Contains(GitLabServer, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var tools = await mcp.ListToolsAsync(GitLabServer, cancellationToken);
        return ResolveTool(tools.Select(tool => tool.Name), MergeRequestToolCandidates);
    }

    private async Task<string?> ResolveMergeRequestListToolAsync(CancellationToken cancellationToken)
    {
        var servers = await mcp.GetServerNamesAsync(cancellationToken);
        if (!servers.Contains(GitLabServer, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var tools = await mcp.ListToolsAsync(GitLabServer, cancellationToken);
        return ResolveTool(tools.Select(tool => tool.Name), MergeRequestListToolCandidates);
    }

    private async Task<McpToolInfo?> ResolveMergeRequestCommentsToolAsync(CancellationToken cancellationToken)
    {
        var servers = await mcp.GetServerNamesAsync(cancellationToken);
        if (!servers.Contains(GitLabServer, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var tools = await mcp.ListToolsAsync(GitLabServer, cancellationToken);
        return tools.FirstOrDefault(tool =>
            MergeRequestCommentsToolCandidates.Contains(tool.Name, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<JsonElement>> ReadMergeRequestCommentsAsync(
        McpToolInfo? tool,
        GitLabReference reference,
        CancellationToken cancellationToken)
    {
        if (tool is null ||
            tool.InputSchema.FirstProperty("properties") is not { ValueKind: JsonValueKind.Object } properties)
        {
            return [];
        }

        const int pageSize = 20;
        var result = new List<JsonElement>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= 50; page++)
        {
            var arguments = new Dictionary<string, object?>();
            AddSupportedArgument(arguments, properties, "project_id", reference.ProjectPath);
            AddSupportedArgument(arguments, properties, "projectId", reference.ProjectPath);
            AddSupportedArgument(arguments, properties, "mr_iid", reference.MergeRequestIid);
            AddSupportedArgument(arguments, properties, "mrIid", reference.MergeRequestIid);
            AddSupportedArgument(arguments, properties, "page", page);
            AddSupportedArgument(arguments, properties, "per_page", pageSize);
            AddSupportedArgument(arguments, properties, "perPage", pageSize);

            var payload = await mcp.CallToolAsync(GitLabServer, tool.Name, arguments, cancellationToken);
            var items = payload.FindArray("comments", "notes", "items", "data");
            if (items is null)
            {
                break;
            }

            var pageItems = items.Value.EnumerateArray().ToArray();
            var added = 0;
            foreach (var item in pageItems)
            {
                var id = item.FirstProperty("id").StringValue() ?? item.GetRawText();
                if (seenIds.Add(id))
                {
                    result.Add(item.Clone());
                    added++;
                }
            }

            var hasNext = payload.FirstProperty("hasNextPage", "has_more", "hasMore") is { ValueKind: JsonValueKind.True };
            if (pageItems.Length == 0 || added == 0 || (!hasNext && pageItems.Length < pageSize))
            {
                break;
            }
        }
        return result;
    }

    private async Task<GitLabReference?> FindMergeRequestByTaskKeyAsync(
        string toolName,
        JsonElement issue,
        string taskId,
        CancellationToken cancellationToken)
    {
        var repository = FindRepository(issue);
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        var payload = await mcp.CallToolAsync(
            GitLabServer,
            toolName,
            new Dictionary<string, object?>
            {
                ["project_id"] = repository,
                ["state"] = "all",
                ["search"] = taskId,
                ["page"] = 1,
                ["per_page"] = 20
            },
            cancellationToken);
        var mergeRequests = payload.FindArray("merge_requests", "items", "data");
        if (mergeRequests is null)
        {
            return null;
        }

        foreach (var item in mergeRequests.Value.EnumerateArray())
        {
            var branch = item.FirstProperty("source_branch").StringValue();
            if (item.FirstProperty("branches") is { ValueKind: JsonValueKind.Object } branches)
            {
                branch ??= branches.FirstProperty("source").StringValue();
            }
            var title = item.FirstProperty("title").StringValue();
            if (!(branch?.Contains(taskId, StringComparison.OrdinalIgnoreCase) == true ||
                  title?.Contains(taskId, StringComparison.OrdinalIgnoreCase) == true))
            {
                continue;
            }
            var iid = item.FirstProperty("iid").IntValue();
            if (iid is null)
            {
                continue;
            }
            return new GitLabReference(
                repository,
                branch ?? string.Empty,
                iid.Value,
                item.FirstProperty("state").StringValue() ?? "unknown",
                0,
                "task-key",
                Url: item.FirstProperty("web_url", "webUrl", "url").StringValue());
        }
        return null;
    }

    private async Task<bool> AddReviewerAssignmentsAsync(
        List<TaskActivity> activities,
        List<TaskActivity> currentReviewAssignments,
        List<ActivityEvent> events,
        string? issueTool,
        string? mergeRequestTool,
        McpToolInfo? mergeRequestCommentsTool,
        Employee employee,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(employee.GitLabUsername))
        {
            return false;
        }

        var listTool = await ResolveReviewerMergeRequestListToolAsync(cancellationToken);
        if (listTool is null)
        {
            return false;
        }

        var knownProjectPaths = activities
            .Select(activity => activity.Task.GitLab?.ProjectPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var assignments = await ReadReviewerMergeRequestsAsync(
            listTool, employee, knownProjectPaths, cancellationToken);
        foreach (var assignment in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingIndex = activities.FindIndex(activity =>
                activity.Task.Id.Equals(assignment.TaskId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = activities[existingIndex];
                var gitLab = existing.Task.GitLab;
                if (gitLab is not null &&
                    gitLab.MergeRequestIid == assignment.Reference.MergeRequestIid &&
                    gitLab.ProjectPath.Equals(assignment.Reference.ProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    var updated = existing with
                    {
                        Task = existing.Task with
                        {
                            GitLab = gitLab with { EmployeeIsReviewer = true }
                        }
                    };
                    activities[existingIndex] = updated;
                    _taskCache[$"{employee.Id}:{updated.Task.Id}"] = updated;
                    currentReviewAssignments.Add(updated);
                    continue;
                }
            }

            WorkTask? task = null;
            if (issueTool is not null)
            {
                var issue = await TryReadIssueAsync(issueTool, assignment.TaskId, cancellationToken);
                if (issue is not null)
                {
                    task = ParseIssue(issue.Value, employee);
                }
            }
            task ??= new WorkTask(
                assignment.TaskId,
                assignment.Title ?? assignment.TaskId,
                "Назначено ревью",
                null,
                null,
                string.Empty,
                null);

            var gitLabReference = assignment.Reference;
            ActivityEvent[] employeeEvents = [];
            if (mergeRequestTool is not null)
            {
                var details = await mcp.CallToolAsync(
                    GitLabServer,
                    mergeRequestTool,
                    new Dictionary<string, object?>
                    {
                        ["project_id"] = gitLabReference.ProjectPath,
                        ["mr_iid"] = gitLabReference.MergeRequestIid,
                        ["include_changes"] = false,
                        ["include_commits"] = true,
                        ["include_discussions"] = true
                    },
                    cancellationToken);
                var supplementalNotes = await ReadMergeRequestCommentsAsync(
                    mergeRequestCommentsTool, gitLabReference, cancellationToken);
                gitLabReference = EnrichGitLabReference(gitLabReference, details, employee, supplementalNotes) with
                {
                    EmployeeIsReviewer = true
                };
                employeeEvents = ParseGitLabEvents(details, task.Id, employee, gitLabReference, supplementalNotes)
                    .Where(item => IsInPeriod(item, from, to))
                    .ToArray();
                events.AddRange(employeeEvents);
            }

            task = task with { GitLab = EnsureMergeRequestUrl(gitLabReference) };
            var activity = new TaskActivity(task, 0, [], IsEmployeeDeveloper(task.DeveloperId, employee));
            currentReviewAssignments.Add(activity);
            if (employeeEvents.Length > 0 && existingIndex < 0)
            {
                activities.Add(activity);
            }
            if (existingIndex < 0)
            {
                _taskCache[$"{employee.Id}:{task.Id}"] = activity;
            }
        }
        return true;
    }

    private async Task<JsonElement?> TryReadIssueAsync(
        string issueTool,
        string taskId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await mcp.CallToolAsync(
                YouTrackServer,
                issueTool,
                new Dictionary<string, object?> { ["issueId"] = taskId },
                cancellationToken);
        }
        catch (McpIntegrationException exception) when (IsIssueNotFound(exception))
        {
            logger.LogWarning(
                "YouTrack issue {TaskId} was referenced by activity data but is unavailable; continuing without issue details.",
                taskId);
            return null;
        }
    }

    private static bool IsIssueNotFound(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("Issue not found", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<McpToolInfo?> ResolveReviewerMergeRequestListToolAsync(CancellationToken cancellationToken)
    {
        var servers = await mcp.GetServerNamesAsync(cancellationToken);
        if (!servers.Contains(GitLabServer, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var tools = await mcp.ListToolsAsync(GitLabServer, cancellationToken);
        return tools.FirstOrDefault(tool =>
        {
            if (!MergeRequestListToolCandidates.Contains(tool.Name, StringComparer.OrdinalIgnoreCase) ||
                tool.InputSchema.FirstProperty("properties") is not { ValueKind: JsonValueKind.Object } properties ||
                properties.FirstProperty("reviewer_username", "reviewerUsername") is null)
            {
                return false;
            }

            return true;
        });
    }

    private async Task<IReadOnlyList<ReviewerMergeRequest>> ReadReviewerMergeRequestsAsync(
        McpToolInfo tool,
        Employee employee,
        IReadOnlyList<string> knownProjectPaths,
        CancellationToken cancellationToken)
    {
        var properties = tool.InputSchema.FirstProperty("properties")!.Value;
        var results = new List<ReviewerMergeRequest>();
        var required = tool.InputSchema.FindArray("required");
        var projectIsRequired = required is not null && required.Value.EnumerateArray().Any(item =>
            ((JsonElement?)item).StringValue()?.Equals("project_id", StringComparison.OrdinalIgnoreCase) == true);
        var projects = projectIsRequired
            ? knownProjectPaths.Cast<string?>().ToArray()
            : new string?[] { null };
        foreach (var projectPath in projects)
        {
            var page = 1;
            while (page <= 10)
            {
                var arguments = new Dictionary<string, object?>();
                if (projectPath is not null)
                {
                    AddSupportedArgument(arguments, properties, "project_id", projectPath);
                    AddSupportedArgument(arguments, properties, "projectId", projectPath);
                }
                AddSupportedArgument(arguments, properties, "reviewer_username", employee.GitLabUsername);
                AddSupportedArgument(arguments, properties, "reviewerUsername", employee.GitLabUsername);
                AddSupportedArgument(arguments, properties, "scope", "all");
                AddSupportedArgument(arguments, properties, "state", "opened");
                AddSupportedArgument(arguments, properties, "page", page);
                AddSupportedArgument(arguments, properties, "per_page", 100);
                AddSupportedArgument(arguments, properties, "perPage", 100);

                var payload = await mcp.CallToolAsync(
                    GitLabServer, tool.Name, arguments, cancellationToken);
                var mergeRequests = payload.FindArray("merge_requests", "mergeRequests", "items", "data");
                if (mergeRequests is null)
                {
                    break;
                }

                var items = mergeRequests.Value.EnumerateArray().ToArray();
                foreach (var item in items)
                {
                    var iid = item.FirstProperty("iid").IntValue();
                    var title = item.FirstProperty("title").StringValue();
                    var branch = item.FirstProperty("source_branch", "sourceBranch").StringValue() ?? string.Empty;
                    var taskId = ExtractTaskId($"{title} {branch}");
                    var url = item.FirstProperty("web_url", "webUrl", "url").StringValue();
                    var itemProjectPath = item.FirstProperty("project_path", "projectPath", "path_with_namespace").StringValue()
                        ?? ProjectPathFromMergeRequestUrl(url)
                        ?? projectPath;
                    if (iid is null || string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(itemProjectPath))
                    {
                        continue;
                    }

                    results.Add(new ReviewerMergeRequest(
                        taskId,
                        title,
                        new GitLabReference(
                            itemProjectPath,
                            branch,
                            iid.Value,
                            item.FirstProperty("state").StringValue() ?? "opened",
                            item.FirstProperty("commit_count", "commitCount", "commits_count").IntValue() ?? 0,
                            "reviewer",
                            Url: url,
                            EmployeeIsReviewer: true)));
                }

                var hasNext = payload.FirstProperty("hasNextPage", "has_more", "hasMore") is { ValueKind: JsonValueKind.True };
                if (!hasNext || items.Length == 0)
                {
                    break;
                }
                page++;
            }
        }

        return results
            .GroupBy(item => $"{item.Reference.ProjectPath}!{item.Reference.MergeRequestIid}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsInPeriod(ActivityEvent item, DateOnly from, DateOnly to)
    {
        var date = DateOnly.FromDateTime(item.OccurredAt.Date);
        return date >= from && date <= to;
    }

    private static void AddSupportedArgument(
        IDictionary<string, object?> arguments,
        JsonElement properties,
        string name,
        object value)
    {
        if (properties.FirstProperty(name) is not null)
        {
            arguments[name] = value;
        }
    }

    private string? ExtractTaskId(string value)
    {
        var match = Regex.Match(
            value,
            $@"\b{Regex.Escape(options.Value.ProjectKey)}-\d+\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string? ProjectPathFromMergeRequestUrl(string? url)
    {
        if (!TryAbsoluteHttpUri(url, out var uri))
        {
            return null;
        }
        var marker = "/-/merge_requests/";
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return markerIndex > 0 ? path[..markerIndex].Trim('/') : null;
    }

    private sealed record ReviewerMergeRequest(
        string TaskId,
        string? Title,
        GitLabReference Reference);

    private static string? FindRepository(JsonElement issue)
    {
        if (issue.FirstProperty("customFields") is not { ValueKind: JsonValueKind.Object } fields)
        {
            return null;
        }

        foreach (var property in fields.EnumerateObject())
        {
            if (!(property.Name.Contains("репозитор", StringComparison.OrdinalIgnoreCase) ||
                  property.Name.Contains("repository", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var value = ReadFieldValue(property.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return uri.AbsolutePath.Trim('/').Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            return value.Trim().Trim('/').Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    private static string? ReadFieldValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Object => value.FirstProperty("name", "value", "fullName", "url").StringValue(),
        JsonValueKind.Array => value.EnumerateArray().Select(ReadFieldValue)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
        _ => ((JsonElement?)value).StringValue()
    };

    private static int? ParseExecutionEstimateMinutes(JsonElement issue, JsonElement? fields)
    {
        var directEstimate = issue.FirstProperty("estimateMinutes").IntValue();
        if (directEstimate is not null)
        {
            return directEstimate;
        }

        if (fields is null)
        {
            return null;
        }

        foreach (var property in fields.Value.EnumerateObject())
        {
            var fieldName = property.Name.TrimStart('$');
            if (!fieldName.Equals("Оценка разработка БЭК ЧЧ", StringComparison.OrdinalIgnoreCase) &&
                !fieldName.Equals("Оценка разработка БЭК", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawEstimate = ReadFieldValue(property.Value);
            var match = ExecutionEstimateNumberRegex().Match(rawEstimate ?? string.Empty);
            if (match.Success && decimal.TryParse(
                    match.Value.Replace(',', '.'),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var hours))
            {
                return (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
            }
        }

        return null;
    }

    private static GitLabReference? ParseGitLabReferenceFromText(string text, string source)
    {
        var match = MergeRequestUrlRegex().Match(text);
        return match.Success && int.TryParse(match.Groups["iid"].Value, out var iid)
            ? new GitLabReference(
                match.Groups["project"].Value,
                string.Empty,
                iid,
                "unknown",
                0,
                source,
                Url: match.Groups["url"].Value)
            : null;
    }

    private GitLabReference EnrichGitLabReference(
        GitLabReference reference,
        JsonElement details,
        Employee employee,
        IReadOnlyList<JsonElement>? supplementalNotes = null)
    {
        var mergeRequest = FindMergeRequestDetails(details);
        var commits = details.FindArray("commits") ?? mergeRequest.FindArray("commits");
        var discussions = details.FindArray("discussions") ?? mergeRequest.FindArray("discussions");
        var allNotes = EnumerateDiscussionNotes(details)
            .Concat(supplementalNotes ?? [])
            .GroupBy(note => note.FirstProperty("id").StringValue() ?? note.GetRawText(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var employeeNotes = allNotes
            .Where(note => IsEmployeeGitLabAuthor(note, employee))
            .ToArray();
        var mergedAt = ParseTimestamp(mergeRequest.FirstProperty("merged_at", "mergedAt"));
        var commitItems = commits?.EnumerateArray().ToArray();
        var employeeCommitCount = commitItems?.Count(commit => IsCommitByEmployee(commit, employee)) ?? 0;
        var otherCommitAuthors = commitItems?
            .Where(commit => !IsCommitByEmployee(commit, employee))
            .GroupBy(GetCommitAuthorLabel, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key} ({group.Count()})")
            .ToArray() ?? [];
        var enriched = reference with
        {
            Branch = mergeRequest.FirstProperty("source_branch", "branch").StringValue() ?? reference.Branch,
            MergeRequestState = ResolveMergeRequestState(mergeRequest, reference.MergeRequestState),
            CommitCount = commits?.GetArrayLength() ?? mergeRequest.FirstProperty("commitCount", "commits_count").IntValue() ?? reference.CommitCount,
            DiscussionCount = discussions?.GetArrayLength() ?? reference.DiscussionCount,
            Url = mergeRequest.FirstProperty("web_url", "webUrl", "url").StringValue() ?? reference.Url,
            EmployeeApproved = IsEmployeeListedAsApprover(details, employee) || employeeNotes.Any(IsApprovalNote),
            EmployeeCommentCount = employeeNotes.Count(note => !IsApprovalNote(note) && !IsSystemNote(note)),
            EmployeeCommitCount = employeeCommitCount,
            EmployeeIsReviewer = reference.EmployeeIsReviewer || IsEmployeeListedAsReviewer(details, employee),
            CommitAuthorsKnown = commits is not null,
            OtherCommitAuthors = otherCommitAuthors,
            EmployeeCreatedMergeRequest = IsEmployeeInUserProperty(mergeRequest, employee, "author", "created_by", "createdBy"),
            EmployeeMergedMergeRequest = IsEmployeeInUserProperty(mergeRequest, employee, "merged_by", "mergedBy", "merge_user", "mergeUser") ||
                                         employeeNotes.Any(note => IsMergeNote(note, mergedAt)),
            EmployeeResolvedThreads = employeeNotes.Any(IsThreadResolutionNote),
            EmployeeActionFactsKnown = true
        };
        return EnsureMergeRequestUrl(enriched, details);
    }

    private GitLabReference EnsureMergeRequestUrl(GitLabReference reference, JsonElement? payload = null)
    {
        if (TryAbsoluteHttpUri(reference.Url, out _))
        {
            return reference;
        }

        var payloadUrls = payload is { } value
            ? EnumerateWebUrls(value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        var directUrl = payloadUrls.FirstOrDefault(url =>
            TryAbsoluteHttpUri(url, out var uri) &&
            uri.AbsolutePath.Contains($"/-/merge_requests/{reference.MergeRequestIid}", StringComparison.OrdinalIgnoreCase));
        if (directUrl is not null)
        {
            return reference with { Url = directUrl };
        }

        var projectPath = Uri.TryCreate(reference.ProjectPath, UriKind.Absolute, out var repositoryUri)
            ? repositoryUri.AbsolutePath
            : reference.ProjectPath;
        projectPath = projectPath.Trim().Trim('/');
        if (projectPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            projectPath = projectPath[..^4];
        }
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return reference;
        }

        Uri? baseUri = null;
        if (repositoryUri is not null &&
            repositoryUri.Scheme is "http" or "https")
        {
            baseUri = new Uri(repositoryUri.GetLeftPart(UriPartial.Authority));
        }
        baseUri ??= payloadUrls
            .Select(url => TryAbsoluteHttpUri(url, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsProjectUrl(uri, projectPath))
            .Select(uri => new Uri(uri!.GetLeftPart(UriPartial.Authority)))
            .FirstOrDefault();
        if (baseUri is null && TryAbsoluteHttpUri(options.Value.GitLabBaseUrl, out var configuredBaseUri))
        {
            baseUri = configuredBaseUri;
        }
        if (baseUri is null)
        {
            return reference;
        }

        var escapedProjectPath = string.Join('/', projectPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Uri.EscapeDataString(Uri.UnescapeDataString(segment))));
        var url = $"{baseUri.AbsoluteUri.TrimEnd('/')}/{escapedProjectPath}/-/merge_requests/{reference.MergeRequestIid}";
        return TryAbsoluteHttpUri(url, out _) ? reference with { Url = url } : reference;
    }

    private static bool IsProjectUrl(Uri uri, string projectPath)
    {
        var decodedPath = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        return decodedPath.Equals(projectPath, StringComparison.OrdinalIgnoreCase) ||
               decodedPath.StartsWith($"{projectPath}/", StringComparison.OrdinalIgnoreCase) ||
               decodedPath.Equals($"{projectPath}.git", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateWebUrls(JsonElement element, int depth = 0)
    {
        if (depth > 12)
        {
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { } url &&
                    TryAbsoluteHttpUri(url, out _))
                {
                    yield return url;
                }
                foreach (var nestedUrl in EnumerateWebUrls(property.Value, depth + 1))
                {
                    yield return nestedUrl;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nestedUrl in EnumerateWebUrls(item, depth + 1))
                {
                    yield return nestedUrl;
                }
            }
        }
    }

    private static bool TryAbsoluteHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static JsonElement FindMergeRequestDetails(JsonElement details)
    {
        foreach (var name in new[] { "merge_request", "mergeRequest", "data", "result" })
        {
            if (details.FirstProperty(name) is { ValueKind: JsonValueKind.Object } nested &&
                nested.FirstProperty("state", "iid", "merge_commit_sha", "merged_at") is not null)
            {
                return nested;
            }
        }

        return details;
    }

    private static string ResolveMergeRequestState(JsonElement details, string fallback)
    {
        var state = details.FirstProperty("state").StringValue()?.Trim().ToLowerInvariant();
        if (state is "open")
        {
            return "opened";
        }
        if (state is "opened" or "merged" or "closed" or "locked")
        {
            return state;
        }

        if (!string.IsNullOrWhiteSpace(details.FirstProperty("merged_at", "mergedAt").StringValue()) ||
            !string.IsNullOrWhiteSpace(details.FirstProperty("merge_commit_sha", "mergeCommitSha").StringValue()))
        {
            return "merged";
        }
        if (!string.IsNullOrWhiteSpace(details.FirstProperty("closed_at", "closedAt").StringValue()))
        {
            return "closed";
        }

        return fallback;
    }

    private static IEnumerable<ActivityEvent> ParseGitLabEvents(
        JsonElement details,
        string taskId,
        Employee employee,
        GitLabReference reference,
        IReadOnlyList<JsonElement>? supplementalNotes = null)
    {
        var mergeRequest = FindMergeRequestDetails(details);
        if ((details.FindArray("commits") ?? mergeRequest.FindArray("commits")) is { } commits)
        {
            foreach (var commit in commits.EnumerateArray().Take(100))
            {
                var occurredAt = ParseTimestamp(commit.FirstProperty("committed_date", "authored_date", "created_at"));
                if (occurredAt is null || !IsCommitByEmployee(commit, employee))
                {
                    continue;
                }
                var id = commit.FirstProperty("id", "short_id").StringValue() ?? Guid.NewGuid().ToString("N");
                yield return new ActivityEvent(
                    $"gitlab:commit:{id}",
                    taskId,
                    employee.Id,
                    occurredAt.Value,
                    "GitLab",
                    "Commit",
                    $"Коммит в {reference.Branch}",
                    commit.FirstProperty("title", "message").StringValue(),
                    commit.FirstProperty("web_url", "webUrl", "url").StringValue() ?? reference.Url);
            }
        }

        var mergedAt = ParseTimestamp(mergeRequest.FirstProperty("merged_at", "mergedAt"));
        var notes = EnumerateDiscussionNotes(details)
            .Concat(supplementalNotes ?? [])
            .GroupBy(note => note.FirstProperty("id").StringValue() ?? note.GetRawText(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(500);
        foreach (var note in notes)
        {
            var occurredAt = ParseTimestamp(note.FirstProperty("created_at", "createdAt"));
            if (occurredAt is null)
            {
                continue;
            }
            var id = note.FirstProperty("id").StringValue() ?? Guid.NewGuid().ToString("N");
            var isEmployeeAction = IsEmployeeGitLabAuthor(note, employee);
            if (!isEmployeeAction)
            {
                continue;
            }
            var isMerge = isEmployeeAction && IsMergeNote(note, mergedAt);
            var isApproval = isEmployeeAction && IsApprovalNote(note);
            var isResolution = isEmployeeAction && IsThreadResolutionNote(note);
            var isComment = isEmployeeAction && !isApproval && !isMerge && !isResolution && !IsSystemNote(note);
            if (!isMerge && !isApproval && !isResolution && !isComment)
            {
                continue;
            }
            yield return new ActivityEvent(
                $"gitlab:discussion:{id}",
                taskId,
                employee.Id,
                occurredAt.Value,
                "GitLab",
                isMerge ? "Merge" : isApproval ? "Approval" : isResolution ? "ThreadResolution" : "ReviewComment",
                isMerge
                    ? $"Сотрудник слил MR !{reference.MergeRequestIid}"
                    : isApproval
                        ? $"Сотрудник одобрил MR !{reference.MergeRequestIid}"
                        : isResolution
                            ? $"Сотрудник разрешил треды в MR !{reference.MergeRequestIid}"
                            : $"Сотрудник оставил комментарий в MR !{reference.MergeRequestIid}",
                isApproval ? null : note.FirstProperty("body", "text").StringValue(),
                note.FirstProperty("web_url", "webUrl", "url").StringValue() ?? reference.Url);
        }
    }

    private static IEnumerable<JsonElement> EnumerateDiscussionNotes(JsonElement details)
    {
        var mergeRequest = FindMergeRequestDetails(details);
        if ((details.FindArray("discussions") ?? mergeRequest.FindArray("discussions")) is not { } discussions)
        {
            yield break;
        }
        foreach (var discussion in discussions.EnumerateArray())
        {
            if (discussion.FindArray("notes") is not { } notes)
            {
                continue;
            }
            foreach (var note in notes.EnumerateArray())
            {
                yield return note;
            }
        }
    }

    private static bool IsEmployeeGitLabAuthor(JsonElement item, Employee employee)
    {
        if (string.IsNullOrWhiteSpace(employee.GitLabUsername) ||
            item.FirstProperty("author", "user") is not { ValueKind: JsonValueKind.Object } author)
        {
            return false;
        }
        return IsEmployeeGitLabUser(author, employee);
    }

    private static bool IsCommitByEmployee(JsonElement commit, Employee employee)
    {
        var author = commit.FirstProperty("author") is { ValueKind: JsonValueKind.Object } nested
            ? nested
            : commit;
        var username = author.FirstProperty("username", "user_name", "login", "author_username").StringValue();
        if (GitLabIdentityEquals(username, employee.GitLabUsername))
        {
            return true;
        }

        var email = author.FirstProperty("email", "author_email").StringValue();
        if (!string.IsNullOrWhiteSpace(email) &&
            !string.IsNullOrWhiteSpace(employee.Email) &&
            email.Trim().Equals(employee.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var emailLocalPart = email?.Split('@', 2)[0];
        if (GitLabIdentityEquals(emailLocalPart, employee.GitLabUsername))
        {
            return true;
        }

        var name = author.FirstProperty("name", "author_name").StringValue();
        return NamesReferToSamePerson(name, employee.DisplayName);
    }

    private static bool NamesReferToSamePerson(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var actualTokens = NormalizeNameTokens(actual);
        var expectedTokens = NormalizeNameTokens(expected);
        return NameTokenSetsMatch(actualTokens, expectedTokens) ||
               NameTokenSetsMatch(
                   NormalizeNameTokens(TransliterateCyrillic(actual)),
                   NormalizeNameTokens(TransliterateCyrillic(expected)));
    }

    private static string[] NormalizeNameTokens(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var normalized = new string(decomposed
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ')
            .ToArray());
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool NameTokenSetsMatch(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return false;
        }

        var remaining = right.ToList();
        var hasStableToken = false;
        foreach (var token in left.OrderByDescending(item => item.Length))
        {
            var exactIndex = remaining.FindIndex(item => item.Equals(token, StringComparison.Ordinal));
            if (exactIndex >= 0)
            {
                hasStableToken |= token.Length >= 3;
                remaining.RemoveAt(exactIndex);
                continue;
            }

            var initialIndex = remaining.FindIndex(item =>
                token.Length > 0 && item.Length > 0 && token[0] == item[0] &&
                (token.Length == 1 || item.Length == 1));
            if (initialIndex < 0)
            {
                return false;
            }
            remaining.RemoveAt(initialIndex);
        }
        return remaining.Count == 0 && hasStableToken;
    }

    private static string TransliterateCyrillic(string value)
    {
        var result = new StringBuilder(value.Length * 2);
        foreach (var character in value.ToLowerInvariant())
        {
            result.Append(character switch
            {
                'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'е' or 'ё' => "e",
                'ж' => "zh", 'з' => "z", 'и' or 'й' => "i", 'к' => "k", 'л' => "l", 'м' => "m",
                'н' => "n", 'о' => "o", 'п' => "p", 'р' => "r", 'с' => "s", 'т' => "t",
                'у' => "u", 'ф' => "f", 'х' => "kh", 'ц' => "ts", 'ч' => "ch", 'ш' => "sh",
                'щ' => "sch", 'ы' => "y", 'э' => "e", 'ю' => "yu", 'я' => "ya",
                'ъ' or 'ь' => string.Empty,
                _ => character.ToString()
            });
        }
        return result.ToString();
    }

    private static string GetCommitAuthorLabel(JsonElement commit)
    {
        var author = commit.FirstProperty("author") is { ValueKind: JsonValueKind.Object } nested
            ? nested
            : commit;
        return author.FirstProperty("name", "author_name").StringValue()?.Trim()
            ?? author.FirstProperty("username", "user_name", "login", "author_username").StringValue()?.Trim()
            ?? author.FirstProperty("email", "author_email").StringValue()?.Trim()
            ?? "Неизвестный автор";
    }

    private static bool IsEmployeeListedAsReviewer(JsonElement details, Employee employee)
    {
        var mergeRequest = FindMergeRequestDetails(details);
        return IsEmployeeInUserArray(details, employee, "reviewers", "assigned_reviewers", "assignedReviewers") ||
               IsEmployeeInUserArray(mergeRequest, employee, "reviewers", "assigned_reviewers", "assignedReviewers") ||
               IsEmployeeInUserProperty(details, employee, "reviewer", "assigned_reviewer", "assignedReviewer") ||
               IsEmployeeInUserProperty(mergeRequest, employee, "reviewer", "assigned_reviewer", "assignedReviewer") ||
               EnumerateDiscussionNotes(details).Any(note => IsReviewerAssignmentNote(note, employee));
    }

    private static bool IsEmployeeInUserArray(JsonElement details, Employee employee, params string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(employee.GitLabUsername) ||
            details.FindArray(propertyNames) is not { } users)
        {
            return false;
        }

        return users.EnumerateArray().Any(item =>
        {
            var user = item.FirstProperty("user") is { ValueKind: JsonValueKind.Object } nested ? nested : item;
            return IsEmployeeGitLabUser(user, employee);
        });
    }

    private static bool IsEmployeeInUserProperty(JsonElement details, Employee employee, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (details.FirstProperty(propertyName) is not { ValueKind: JsonValueKind.Object } user)
            {
                continue;
            }
            if (user.FirstProperty("user") is { ValueKind: JsonValueKind.Object } nested)
            {
                user = nested;
            }
            if (IsEmployeeGitLabUser(user, employee))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEmployeeGitLabUser(JsonElement user, Employee employee)
    {
        var username = user.FirstProperty("username", "user_name", "login").StringValue();
        if (GitLabIdentityEquals(username, employee.GitLabUsername))
        {
            return true;
        }

        var name = user.FirstProperty("name", "display_name", "displayName").StringValue();
        return !string.IsNullOrWhiteSpace(name) &&
               name.Trim().Equals(employee.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReviewerAssignmentNote(JsonElement note, Employee employee)
    {
        if (!IsSystemNote(note))
        {
            return false;
        }

        var body = note.FirstProperty("body", "text").StringValue();
        if (string.IsNullOrWhiteSpace(body) ||
            (!body.Contains("requested review from", StringComparison.OrdinalIgnoreCase) &&
             !body.Contains("assigned reviewer", StringComparison.OrdinalIgnoreCase) &&
             !(body.Contains("ревью", StringComparison.OrdinalIgnoreCase) &&
               body.Contains("назнач", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(employee.GitLabUsername) &&
                body.Contains(employee.GitLabUsername, StringComparison.OrdinalIgnoreCase)) ||
               body.Contains(employee.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool GitLabIdentityEquals(string? actual, string? expected) =>
        !string.IsNullOrWhiteSpace(actual) &&
        !string.IsNullOrWhiteSpace(expected) &&
        actual.Trim().TrimStart('@').Equals(expected.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase);

    private static bool IsEmployeeListedAsApprover(JsonElement details, Employee employee)
    {
        var mergeRequest = FindMergeRequestDetails(details);
        return IsEmployeeInUserArray(details, employee, "approved_by", "approvedBy", "approvers") ||
               IsEmployeeInUserArray(mergeRequest, employee, "approved_by", "approvedBy", "approvers");
    }

    private static bool IsApprovalNote(JsonElement note)
    {
        var body = note.FirstProperty("body", "text").StringValue();
        return body?.Contains("approved this merge request", StringComparison.OrdinalIgnoreCase) == true ||
               body?.Equals("approved", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsMergeNote(JsonElement note, DateTimeOffset? mergedAt)
    {
        if (!IsSystemNote(note))
        {
            return false;
        }

        var body = note.FirstProperty("body", "text").StringValue();
        if (body?.Contains("merged this merge request", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }
        if (mergedAt is null ||
            body?.Contains("mentioned in commit", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        var noteCreatedAt = ParseTimestamp(note.FirstProperty("created_at", "createdAt"));
        return noteCreatedAt is not null &&
               Math.Abs((noteCreatedAt.Value - mergedAt.Value).TotalMinutes) <= 5;
    }

    private static bool IsThreadResolutionNote(JsonElement note)
    {
        if (!IsSystemNote(note))
        {
            return false;
        }
        var body = note.FirstProperty("body", "text").StringValue();
        return body?.Contains("resolved all threads", StringComparison.OrdinalIgnoreCase) == true ||
               body?.Contains("resolved thread", StringComparison.OrdinalIgnoreCase) == true ||
               body?.Contains("разрешил все обсуждения", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsSystemNote(JsonElement note) =>
        note.FirstProperty("system", "is_system", "isSystem") is { ValueKind: JsonValueKind.True };

    private static string? BuildWorkItemUrl(string taskUrl, string workItemId)
    {
        if (string.IsNullOrWhiteSpace(taskUrl) || string.IsNullOrWhiteSpace(workItemId) ||
            workItemId.StartsWith("presence:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var issueUrl = taskUrl.Split('#', 2)[0];
        return $"{issueUrl}#focus=WorkItem-{Uri.EscapeDataString(workItemId)}.0-0";
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement? value)
    {
        var raw = value.StringValue();
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp
            : null;
    }

    private static string? GetTaskId(JsonElement item)
    {
        var direct = item.FirstProperty("idReadable", "taskId", "issueId", "id").StringValue();
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }
        return item.FirstProperty("issue", "task") is { ValueKind: JsonValueKind.Object } issue
            ? issue.FirstProperty("idReadable", "issueId", "id").StringValue()
            : null;
    }

    private static DateOnly? ParseDate(JsonElement? value)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var epoch))
        {
            return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(epoch).DateTime);
        }
        var raw = value.StringValue();
        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? DateOnly.FromDateTime(timestamp.DateTime)
            : null;
    }

    private static int? ParseDurationMinutes(JsonElement item)
    {
        var direct = item.FirstProperty("durationMinutes", "minutes", "spentMinutes").IntValue();
        if (direct is not null)
        {
            return direct;
        }
        if (item.FirstProperty("duration") is not { } duration)
        {
            return null;
        }
        if (duration.ValueKind == JsonValueKind.Object)
        {
            return duration.FirstProperty("minutes", "value").IntValue();
        }
        if (duration.ValueKind == JsonValueKind.Number)
        {
            return ((JsonElement?)duration).IntValue();
        }
        var raw = ((JsonElement?)duration).StringValue();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var days = DurationPartRegex("d").Match(raw);
        var hours = DurationPartRegex("h").Match(raw);
        var minutes = DurationPartRegex("m").Match(raw);
        return (days.Success ? int.Parse(days.Groups[1].Value) * 8 * 60 : 0) +
               (hours.Success ? int.Parse(hours.Groups[1].Value) * 60 : 0) +
               (minutes.Success ? int.Parse(minutes.Groups[1].Value) : 0);
    }

    private static bool IsEmployeeDeveloper(string? developer, Employee employee) =>
        !string.IsNullOrWhiteSpace(developer) &&
        (developer.Equals(employee.Id, StringComparison.OrdinalIgnoreCase) ||
         developer.Equals(employee.YouTrackLogin, StringComparison.OrdinalIgnoreCase) ||
         developer.Equals(employee.DisplayName, StringComparison.OrdinalIgnoreCase));

    private static string? ResolveTool(IEnumerable<string> available, IEnumerable<string> candidates)
    {
        var names = available.ToArray();
        foreach (var candidate in candidates)
        {
            var exact = names.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }
        return null;
    }

    private static McpIntegrationException MissingSearchIssuesTool(IEnumerable<string> names) => new(
        "YouTrack MCP подключён, но не предоставляет read-only метод поиска задач " +
        "youtrack_search_issues(query, customFieldsToReturn, offset, limit). " +
        $"Доступные методы: {string.Join(", ", names.Order())}");

    [GeneratedRegex(@"(?<url>https?://[^/]+/(?<project>[^\s]+?)/-/merge_requests/(?<iid>\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex MergeRequestUrlRegex();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutionEstimateNumberRegex();

    private static Regex DurationPartRegex(string suffix) =>
        new($@"(\d+)\s*{suffix}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
