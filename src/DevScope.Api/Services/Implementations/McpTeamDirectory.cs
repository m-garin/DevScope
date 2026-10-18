using DevScope.Api.Configuration;
using DevScope.Api.Domain;
using DevScope.Api.Integrations.Mcp;
using DevScope.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace DevScope.Api.Services.Implementations;

public sealed class McpTeamDirectory(
    IMcpGatewayClient mcp,
    IOptions<DevScopeOptions> options) : ITeamDirectory
{
    private const string Server = "youtrack";
    private const int PageSize = 20;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IReadOnlyList<Employee>? _employees;
    private DateTimeOffset _loadedAt;

    public async Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken)
    {
        if (_employees is not null && DateTimeOffset.UtcNow - _loadedAt < CacheLifetime)
        {
            return _employees;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_employees is not null && DateTimeOffset.UtcNow - _loadedAt < CacheLifetime)
            {
                return _employees;
            }

            var tools = await mcp.ListToolsAsync(Server, cancellationToken);
            var names = tools.Select(item => item.Name).ToArray();
            var membersTool = ResolveTool(names, "youtrack_get_user_group_members", "get_user_group_members")
                ?? throw MissingTool("youtrack_get_user_group_members", names);
            var group = options.Value.DeveloperGroup.Trim();
            if (string.IsNullOrWhiteSpace(group))
            {
                throw new McpIntegrationException(
                    "Не задана группа разработчиков DevScope:DeveloperGroup.");
            }

            var employees = new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);
            for (var offset = 0; offset < 10_000; offset += PageSize)
            {
                var payload = await mcp.CallToolAsync(
                    Server,
                    membersTool,
                    new Dictionary<string, object?>
                    {
                        ["group"] = group,
                        ["offset"] = offset,
                        ["limit"] = PageSize
                    },
                    cancellationToken);
                var page = payload.FindArray("members", "users", "items", "data")
                    ?? throw new McpIntegrationException(
                        $"MCP tool '{membersTool}' вернул ответ без массива участников.");
                var count = 0;
                foreach (var member in page.EnumerateArray())
                {
                    count++;
                    var login = member.FirstProperty("login", "username").StringValue();
                    if (string.IsNullOrWhiteSpace(login) || IsServiceAccount(login) || IsInactive(member))
                    {
                        continue;
                    }
                    var name = member.FirstProperty("name", "displayName", "fullName").StringValue() ?? login;
                    var email = member.FirstProperty("email", "emailAddress", "mail").StringValue() ?? string.Empty;
                    employees[login] = new Employee(login, name, group, "Участник проекта", login, login, email);
                }
                if (count < PageSize)
                {
                    break;
                }
            }

            _employees = employees.Values.OrderBy(item => item.DisplayName).ToArray();
            _loadedAt = DateTimeOffset.UtcNow;
            return _employees;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<Employee?> FindEmployeeAsync(string employeeId, CancellationToken cancellationToken) =>
        (await GetEmployeesAsync(cancellationToken)).FirstOrDefault(item =>
            item.Id.Equals(employeeId, StringComparison.OrdinalIgnoreCase));

    private static bool IsServiceAccount(string login) =>
        login.StartsWith("hq-svc-", StringComparison.OrdinalIgnoreCase);

    private static bool IsInactive(System.Text.Json.JsonElement member) =>
        member.FirstProperty("banned", "isBanned", "disabled", "isDisabled") is
            { ValueKind: System.Text.Json.JsonValueKind.True };

    private static string? ResolveTool(IEnumerable<string> names, params string[] candidates) =>
        candidates.Select(candidate => names.FirstOrDefault(name =>
            name.Equals(candidate, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(name => name is not null);

    private static McpIntegrationException MissingTool(string required, IEnumerable<string> names) => new(
        $"YouTrack MCP не предоставляет read-only tool '{required}'. " +
        $"Доступные методы: {string.Join(", ", names.Order())}");
}
