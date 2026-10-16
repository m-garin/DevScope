namespace DevScope.Api.Services.Interfaces;

public interface IActivityRefreshService
{
    Task<DateTimeOffset?> RefreshEmployeeAsync(string employeeId, CancellationToken cancellationToken);
    Task RefreshAllAsync(CancellationToken cancellationToken);
}
