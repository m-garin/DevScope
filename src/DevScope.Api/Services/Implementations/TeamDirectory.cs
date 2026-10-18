using System.Text.Json;
using DevScope.Api.Configuration;
using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace DevScope.Api.Services.Implementations;

public sealed class TeamDirectory(IOptions<DevScopeOptions> options) : ITeamDirectory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.TeamsConfigPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Конфиг сотрудников не найден: {path}", path);
        }

        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<TeamsConfiguration>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Конфиг сотрудников пуст.");

        return configuration.Teams
            .SelectMany(team => team.Members.Select(member => new Employee(
                member.Id,
                member.DisplayName,
                team.Name,
                member.Roles.FirstOrDefault() ?? "Сотрудник",
                member.YouTrackLogin,
                member.GitLabUsername,
                member.Email)))
            .OrderBy(employee => employee.DisplayName)
            .ToArray();
    }

    public async Task<Employee?> FindEmployeeAsync(string employeeId, CancellationToken cancellationToken) =>
        (await GetEmployeesAsync(cancellationToken)).FirstOrDefault(employee =>
            employee.Id.Equals(employeeId, StringComparison.OrdinalIgnoreCase));
}
