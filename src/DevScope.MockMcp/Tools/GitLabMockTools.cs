using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace DevScope.MockMcp.Tools;

[McpServerToolType]
public sealed class GitLabMockTools(MockDataStore store)
{
    [McpServerTool(Name = "gitlab_gitlab_mr_list")]
    [Description("Read-only list of merge requests, including filtering by reviewer username.")]
    public JsonObject MergeRequestList(
        string? project_id = null,
        string state = "opened",
        string? search = null,
        string? reviewer_username = null,
        string scope = "all",
        int page = 1,
        int per_page = 100)
    {
        var matching = store.Tasks.OfType<JsonObject>()
            .Select(task => (Task: task, GitLab: task["gitLab"] as JsonObject))
            .Where(item => item.GitLab is not null)
            .Where(item => string.IsNullOrWhiteSpace(project_id) ||
                item.GitLab!["projectPath"]?.GetValue<string>() == project_id)
            .Where(item => state.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.GitLab!["mergeRequestState"]?.GetValue<string>(), state, StringComparison.OrdinalIgnoreCase) ||
                state.Equals("opened", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.GitLab!["mergeRequestState"]?.GetValue<string>(), "open", StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(search) ||
                item.Task["id"]?.GetValue<string>().Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                item.Task["summary"]?.GetValue<string>().Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            .Where(_ => string.IsNullOrWhiteSpace(reviewer_username) ||
                reviewer_username.Equals("ipetrov", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var offset = Math.Max(0, page - 1) * per_page;
        var items = matching.Skip(offset).Take(per_page).Select(item =>
        {
            var gitLab = item.GitLab!;
            var projectPath = gitLab["projectPath"]?.GetValue<string>() ?? string.Empty;
            var iid = gitLab["mergeRequestIid"]?.GetValue<int>() ?? 0;
            return (JsonNode)new JsonObject
            {
                ["iid"] = iid,
                ["title"] = $"{item.Task["id"]?.GetValue<string>()} {item.Task["summary"]?.GetValue<string>()}",
                ["state"] = gitLab["mergeRequestState"]?.DeepClone() ?? "opened",
                ["source_branch"] = gitLab["branch"]?.DeepClone(),
                ["project_path"] = projectPath,
                ["web_url"] = $"https://gitlab.example/{projectPath}/-/merge_requests/{iid}",
                ["reviewers"] = new JsonArray(new JsonObject
                {
                    ["username"] = "ipetrov",
                    ["name"] = "Иван Петров"
                })
            };
        }).ToArray();

        return new JsonObject
        {
            ["merge_requests"] = new JsonArray(items),
            ["hasNextPage"] = offset + items.Length < matching.Length
        };
    }

    [McpServerTool(Name = "gitlab_gitlab_mr_details")]
    [Description("Read-only Merge Request details with optional commits and discussions.")]
    public JsonObject MergeRequestDetails(
        string project_id,
        int mr_iid,
        bool include_changes = false,
        bool include_commits = false,
        bool include_discussions = false)
    {
        var task = store.Tasks.OfType<JsonObject>().FirstOrDefault(item =>
        {
            var gitLab = item["gitLab"] as JsonObject;
            return gitLab?["projectPath"]?.GetValue<string>() == project_id &&
                   gitLab["mergeRequestIid"]?.GetValue<int>() == mr_iid;
        });
        var gitLabReference = task?["gitLab"] as JsonObject;

        var result = new JsonObject
        {
            ["id"] = 100000 + mr_iid,
            ["iid"] = mr_iid,
            ["title"] = task?["summary"]?.DeepClone() ?? "Mock MR",
            ["state"] = gitLabReference?["mergeRequestState"]?.DeepClone() ?? "opened",
            ["source_branch"] = gitLabReference?["branch"]?.DeepClone(),
            ["target_branch"] = "main",
            ["author"] = new JsonObject
            {
                ["username"] = "feature-author",
                ["name"] = "Feature Author"
            },
            ["reviewers"] = new JsonArray(new JsonObject
            {
                ["username"] = "ipetrov",
                ["name"] = "Иван Петров"
            }),
            ["web_url"] = $"https://gitlab.example/{project_id}/-/merge_requests/{mr_iid}"
        };

        if (include_commits)
        {
            result["commits"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "abc123",
                    ["author_name"] = "Иван Петров",
                    ["author_email"] = "ipetrov@example.test",
                    ["authored_date"] = "2026-10-12T11:42:00+03:00",
                    ["message"] = $"{task?["id"]?.GetValue<string>()} employee commit"
                },
                new JsonObject
                {
                    ["id"] = "def456",
                    ["author_name"] = "Feature Author",
                    ["author_email"] = "feature.author@example.test",
                    ["authored_date"] = "2026-10-12T10:15:00+03:00",
                    ["message"] = $"{task?["id"]?.GetValue<string>()} feature commit"
                },
                new JsonObject
                {
                    ["id"] = "ghi789",
                    ["author_name"] = "Feature Author",
                    ["author_email"] = "feature.author@example.test",
                    ["authored_date"] = "2026-10-12T09:30:00+03:00",
                    ["message"] = $"{task?["id"]?.GetValue<string>()} follow-up commit"
                });
        }

        if (include_discussions)
        {
            result["discussions"] = new JsonArray(new JsonObject
            {
                ["id"] = "discussion-1",
                ["notes"] = new JsonArray(new JsonObject
                {
                    ["author"] = new JsonObject { ["username"] = "reviewer" },
                    ["body"] = "Mock review comment",
                    ["created_at"] = "2026-10-11T14:05:00+03:00",
                    ["resolvable"] = true,
                    ["resolved"] = false
                })
            });
        }

        if (include_changes)
        {
            result["changes"] = new JsonArray(new JsonObject
            {
                ["old_path"] = "src/Service.cs",
                ["new_path"] = "src/Service.cs",
                ["diff"] = "@@ mock diff @@"
            });
        }

        return result;
    }

    [McpServerTool(Name = "gitlab_gitlab_mr_comments")]
    [Description("Read-only paginated Merge Request comments and system notes.")]
    public JsonObject MergeRequestComments(
        string project_id,
        int mr_iid,
        int page = 1,
        int per_page = 20)
    {
        var notes = new JsonNode[]
        {
            new JsonObject
            {
                ["id"] = $"{mr_iid}-review",
                ["author"] = new JsonObject { ["username"] = "ipetrov", ["name"] = "Иван Петров" },
                ["body"] = "Mock review comment",
                ["created_at"] = "2026-10-11T14:05:00+03:00",
                ["system"] = false,
                ["resolvable"] = true
            }
        };
        var offset = Math.Max(0, page - 1) * per_page;
        var items = notes.Skip(offset).Take(per_page).Select(item => item.DeepClone()).ToArray();
        return new JsonObject
        {
            ["comments"] = new JsonArray(items),
            ["hasNextPage"] = offset + items.Length < notes.Length
        };
    }
}

