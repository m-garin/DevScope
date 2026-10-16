namespace DevScope.Api.Services.Interfaces;

public interface IActivityRefreshService
{
    Task<DateTimeOffset?> RefreshEmployeeAsync(
        string employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken);
    Task RefreshAllAsync(CancellationToken cancellationToken);
}
