using DevScope.Api.Domain;

namespace DevScope.Api.Services.Interfaces;

public interface IActivityProvider
{
    Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken);
    Task<PeriodSummary?> GetPeriodSummaryAsync(string employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
    Task<TaskActivity?> GetTaskActivityAsync(string employeeId, string taskId, CancellationToken cancellationToken);
}
