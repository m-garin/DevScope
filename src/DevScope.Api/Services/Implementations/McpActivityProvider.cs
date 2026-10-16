using System.Collections.Concurrent;
using System.Globalization;
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
    IOptions<DevScopeOptions> options) : IActivityProvider
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
    private readonly ConcurrentDictionary<string, TaskActivity> _taskCache = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken) =>
        teamDirectory.GetEmployeesAsync(cancellationToken);

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
        var mergeRequestListTool = await ResolveMergeRequestListToolAsync(cancellationToken);
        var workItemTool = ResolveTool(toolNames, WorkItemToolCandidates);
        if (workItemTool is not null)
        {
            return await GetExactPeriodSummaryAsync(
                workItemTool, issueTool, commentTool, mergeRequestListTool, mergeRequestTool,
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
        foreach (var pair in issueById)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var taskId = pair.Key;
            var issuePayload = pair.Value;
            var task = ParseIssue(pair.Value, employee);

            if (issueTool is not null)
            {
                var issue = await mcp.CallToolAsync(
                    YouTrackServer,
                    issueTool,
                    new Dictionary<string, object?> { ["issueId"] = taskId },
                    cancellationToken);
                issuePayload = issue;
                task = ParseIssue(issue, employee) ?? task;
            }

            if (task is not null && task.GitLab is null && commentTool is not null)
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
                task = task with { GitLab = EnrichGitLabReference(task.GitLab, details) };
                gitLabEvents.AddRange(ParseGitLabEvents(details, task.Id, employee.Id, task.GitLab));
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

        var ordered = taskActivities
            .OrderByDescending(activity => activity.Worklogs.Max(worklog => worklog.Date))
            .ToArray();
        var activeDays = ordered.SelectMany(item => item.Worklogs).Select(item => item.Date).Distinct().Count();
        var narrative = $"Списания подтверждены по {ordered.Length} задачам в {activeDays} дн. выбранного периода.";
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
            HasExactWorklogDurations: false);
    }

    private async Task<PeriodSummary> GetExactPeriodSummaryAsync(
        string workItemTool,
        string? issueTool,
        string? commentTool,
        string? mergeRequestListTool,
        string? mergeRequestTool,
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
            ? new[] { "MCP вернул work items, но их формат не удалось распознать." }
            : Array.Empty<string>();
        var activities = new List<TaskActivity>();
        var events = new List<ActivityEvent>();

        foreach (var group in worklogs.GroupBy(item => item.TaskId, StringComparer.OrdinalIgnoreCase))
        {
            var taskId = group.Key;
            var raw = rawWorkItems.FirstOrDefault(item =>
                string.Equals(GetTaskId(item), taskId, StringComparison.OrdinalIgnoreCase));
            var issuePayload = raw.ValueKind == JsonValueKind.Undefined ? default : raw;
            var task = raw.ValueKind == JsonValueKind.Undefined ? null : ParseEmbeddedTask(raw, employee);
            if (issueTool is not null)
            {
                var issue = await mcp.CallToolAsync(
                    YouTrackServer, issueTool,
                    new Dictionary<string, object?> { ["issueId"] = taskId },
                    cancellationToken);
                issuePayload = issue;
                task = ParseIssue(issue, employee) ?? task;
            }
            if (task is not null && task.GitLab is null && commentTool is not null)
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
                task = task with { GitLab = EnrichGitLabReference(task.GitLab, details) };
                events.AddRange(ParseGitLabEvents(details, task.Id, employee.Id, task.GitLab));
            }

            task ??= new WorkTask(taskId, taskId, "Неизвестно", null, null, string.Empty, null);
            var taskWorklogs = group.OrderByDescending(item => item.Date).ToArray();
            events.AddRange(taskWorklogs.Select(item => new ActivityEvent(
                item.Id,
                taskId,
                employee.Id,
                new DateTimeOffset(item.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                "YouTrack",
                "Worklog",
                $"Списано {item.DurationMinutes} мин в {taskId}",
                item.Description)));
            var activity = new TaskActivity(
                task,
                taskWorklogs.Sum(item => item.DurationMinutes),
                taskWorklogs,
                IsEmployeeDeveloper(task.DeveloperId, employee));
            activities.Add(activity);
            _taskCache[$"{employee.Id}:{task.Id}"] = activity;
        }

        var ordered = activities
            .OrderByDescending(item => item.Worklogs.Max(worklog => worklog.Date))
            .ToArray();
        return new PeriodSummary(
            employee,
            from,
            to,
            worklogs.Sum(item => item.DurationMinutes),
            ordered.Length,
            ordered.Count(item => item.Task.GitLab is not null),
            $"Списано время по {ordered.Length} задачам.",
            ordered,
            events.OrderByDescending(item => item.OccurredAt).ToArray(),
            limitations);
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
            item.FirstProperty("description", "text", "comment").StringValue());
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
        var estimate = issue.FirstProperty("estimateMinutes").IntValue();
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
            issue.FirstProperty("type").StringValue() ?? Field("Type", "Тип1", "$Тип"));
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
                    "issue");
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
                "task-key");
        }
        return null;
    }

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

    private static GitLabReference? ParseGitLabReferenceFromText(string text, string source)
    {
        var match = MergeRequestUrlRegex().Match(text);
        return match.Success && int.TryParse(match.Groups["iid"].Value, out var iid)
            ? new GitLabReference(match.Groups["project"].Value, string.Empty, iid, "unknown", 0, source)
            : null;
    }

    private static GitLabReference EnrichGitLabReference(GitLabReference reference, JsonElement details)
    {
        var commits = details.FindArray("commits");
        var discussions = details.FindArray("discussions");
        return reference with
        {
            Branch = details.FirstProperty("source_branch", "branch").StringValue() ?? reference.Branch,
            MergeRequestState = details.FirstProperty("state", "merge_status").StringValue() ?? reference.MergeRequestState,
            CommitCount = commits?.GetArrayLength() ?? details.FirstProperty("commitCount", "commits_count").IntValue() ?? reference.CommitCount,
            DiscussionCount = discussions?.GetArrayLength() ?? reference.DiscussionCount
        };
    }

    private static IEnumerable<ActivityEvent> ParseGitLabEvents(
        JsonElement details,
        string taskId,
        string employeeId,
        GitLabReference reference)
    {
        if (details.FindArray("commits") is { } commits)
        {
            foreach (var commit in commits.EnumerateArray().Take(100))
            {
                var occurredAt = ParseTimestamp(commit.FirstProperty("committed_date", "authored_date", "created_at"));
                if (occurredAt is null)
                {
                    continue;
                }
                var id = commit.FirstProperty("id", "short_id").StringValue() ?? Guid.NewGuid().ToString("N");
                yield return new ActivityEvent(
                    $"gitlab:commit:{id}",
                    taskId,
                    employeeId,
                    occurredAt.Value,
                    "GitLab",
                    "Commit",
                    $"Коммит в {reference.Branch}",
                    commit.FirstProperty("title", "message").StringValue());
            }
        }

        if (details.FindArray("discussions") is not { } discussions)
        {
            yield break;
        }
        foreach (var discussion in discussions.EnumerateArray().Take(100))
        {
            if (discussion.FindArray("notes") is not { } notes)
            {
                continue;
            }
            foreach (var note in notes.EnumerateArray().Take(100))
            {
                var occurredAt = ParseTimestamp(note.FirstProperty("created_at", "createdAt"));
                if (occurredAt is null)
                {
                    continue;
                }
                var id = note.FirstProperty("id").StringValue() ?? Guid.NewGuid().ToString("N");
                yield return new ActivityEvent(
                    $"gitlab:discussion:{id}",
                    taskId,
                    employeeId,
                    occurredAt.Value,
                    "GitLab",
                    "Discussion",
                    $"Обсуждение MR !{reference.MergeRequestIid}",
                    note.FirstProperty("body", "text").StringValue());
            }
        }
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

    [GeneratedRegex(@"https?://[^/]+/(?<project>[^\s]+?)/-/merge_requests/(?<iid>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MergeRequestUrlRegex();

    private static Regex DurationPartRegex(string suffix) =>
        new($@"(\d+)\s*{suffix}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
