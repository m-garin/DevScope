using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace DevScope.MockMcp.Tools;

[McpServerToolType]
public sealed class GitLabMockTools(MockDataStore store)
{
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
            ["web_url"] = $"https://gitlab.example/{project_id}/-/merge_requests/{mr_iid}"
        };

        if (include_commits)
        {
            result["commits"] = new JsonArray(new JsonObject
            {
                ["id"] = "abc123",
                ["author_name"] = "Mock Developer",
                ["author_email"] = "developer@example.test",
                ["authored_date"] = "2026-10-12T11:42:00+03:00",
                ["message"] = $"{task?["id"]?.GetValue<string>()} mock commit"
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
}

