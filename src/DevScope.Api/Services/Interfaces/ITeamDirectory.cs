using DevScope.Api.Domain;

namespace DevScope.Api.Services.Interfaces;

public interface ITeamDirectory
{
    Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken);
    Task<Employee?> FindEmployeeAsync(string employeeId, CancellationToken cancellationToken);
}
