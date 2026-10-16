using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevScope.MockMcp.Tools;

public sealed class MockDataStore(IConfiguration configuration)
{
    private readonly Lazy<JsonObject> _root = new(() => Load(configuration));

    public JsonArray Employees => _root.Value["employees"]?.AsArray() ?? [];
    public JsonArray Tasks => _root.Value["tasks"]?.AsArray() ?? [];
    public JsonArray Worklogs => _root.Value["worklogs"]?.AsArray() ?? [];
    public JsonArray Events => _root.Value["events"]?.AsArray() ?? [];

    public JsonObject? FindEmployee(string idOrLogin) => Employees
        .OfType<JsonObject>()
        .FirstOrDefault(employee =>
            string.Equals(employee["id"]?.GetValue<string>(), idOrLogin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(employee["youTrackLogin"]?.GetValue<string>(), idOrLogin, StringComparison.OrdinalIgnoreCase));

    public JsonObject? FindTask(string id) => Tasks
        .OfType<JsonObject>()
        .FirstOrDefault(task => string.Equals(task["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));

    private static JsonObject Load(IConfiguration configuration)
    {
        var path = configuration["MockData:Path"] ?? Path.Combine(AppContext.BaseDirectory, "data", "mock-data.json");
        using var stream = File.OpenRead(path);
        return JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException("Mock data is empty.");
    }
}

