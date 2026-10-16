using DevScope.Api.Services.Interfaces;

namespace DevScope.Api.Services.Implementations;

public sealed class MockActivityRefreshService : IActivityRefreshService
{
    public Task<DateTimeOffset?> RefreshEmployeeAsync(
        string employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken) =>
        Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);

    public Task RefreshAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
