using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace DevScope.MockMcp.Tools;

[McpServerToolType]
public sealed class YouTrackMockTools(MockDataStore store)
{
    [McpServerTool(Name = "youtrack_search_work_items")]
    [Description("Read-only search for work items by project, author and date range.")]
    public JsonObject SearchWorkItems(
        string projectKey,
        string authorLogin,
        string dateFrom,
        string dateTo,
        int offset = 0,
        int limit = 100)
    {
        var author = store.FindEmployee(authorLogin);
        if (author is null)
        {
            return new JsonObject { ["workItemsPage"] = new JsonArray(), ["hasNextPage"] = false };
        }

        var authorId = author["id"]!.GetValue<string>();
        var from = DateOnly.Parse(dateFrom, CultureInfo.InvariantCulture);
        var to = DateOnly.Parse(dateTo, CultureInfo.InvariantCulture);
        var matching = store.Worklogs.OfType<JsonObject>()
            .Where(worklog => worklog["authorId"]?.GetValue<string>() == authorId)
            .Where(worklog =>
            {
                var date = DateOnly.Parse(worklog["date"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                return date >= from && date <= to;
            })
            .ToArray();

        var page = new JsonArray(matching.Skip(offset).Take(limit).Select(worklog =>
        {
            var result = worklog.DeepClone().AsObject();
            var task = store.FindTask(worklog["taskId"]!.GetValue<string>());
            result["issue"] = task is null ? null : new JsonObject
            {
                ["id"] = task["id"]?.DeepClone(),
                ["summary"] = task["summary"]?.DeepClone(),
                ["status"] = task["status"]?.DeepClone(),
                ["url"] = task["url"]?.DeepClone()
            };
            result["author"] = new JsonObject
            {
                ["login"] = author["youTrackLogin"]?.DeepClone(),
                ["name"] = author["displayName"]?.DeepClone()
            };
            return (JsonNode)result;
        }).ToArray());

        return new JsonObject
        {
            ["workItemsPage"] = page,
            ["hasNextPage"] = offset + page.Count < matching.Length,
            ["nextPageOffset"] = offset + page.Count,
            ["total"] = matching.Length
        };
    }

    [McpServerTool(Name = "youtrack_search_issues")]
    [Description("Read-only issue search in the FINTDR mock project.")]
    public JsonObject SearchIssues(string query, int limit = 20, int offset = 0, string[]? customFieldsToReturn = null)
    {
        var tasks = store.Tasks.OfType<JsonObject>().Skip(offset).Take(limit).Select(task => (JsonNode)task.DeepClone()).ToArray();
        return new JsonObject
        {
            ["issuesPage"] = new JsonArray(tasks),
            ["hasNextPage"] = offset + tasks.Length < store.Tasks.Count,
            ["nextPageOffset"] = offset + tasks.Length
        };
    }

    [McpServerTool(Name = "youtrack_get_issue")]
    [Description("Read-only issue card.")]
    public JsonObject? GetIssue(string issueId) => store.FindTask(issueId)?.DeepClone().AsObject();

    [McpServerTool(Name = "youtrack_get_issue_comments")]
    [Description("Read-only issue comments, including an MR link when present.")]
    public JsonArray GetIssueComments(string issueId, int offset = 0, int limit = 10)
    {
        var task = store.FindTask(issueId);
        var gitLab = task?["gitLab"] as JsonObject;
        if (gitLab is null)
        {
            return [];
        }

        var project = gitLab["projectPath"]?.GetValue<string>();
        var iid = gitLab["mergeRequestIid"]?.GetValue<int>();
        return new JsonArray(new JsonObject
        {
            ["author"] = "mock-user",
            ["text"] = $"Merge Request For Progress: https://gitlab.example/{project}/-/merge_requests/{iid}",
            ["createdAt"] = "2026-10-12T10:00:00+03:00"
        });
    }
}

