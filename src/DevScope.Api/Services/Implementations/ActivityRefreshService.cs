using System.Text.Json;
using DevScope.Api.Data;
using DevScope.Api.Data.Entities;
using DevScope.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DevScope.Api.Services.Implementations;

public sealed class ActivityRefreshService(
    McpActivityProvider source,
    ITeamDirectory teamDirectory,
    IDbContextFactory<DevScopeDbContext> dbContextFactory) : IActivityRefreshService
{
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DateTimeOffset?> RefreshEmployeeAsync(
        string employeeId,
        CancellationToken cancellationToken)
    {
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var summary = await source.GetPeriodSummaryAsync(
                employeeId,
                today.AddDays(-365),
                today,
                cancellationToken);
            if (summary is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var key = CacheKey(employeeId);
            var cached = await database.CachedReports.SingleOrDefaultAsync(
                item => item.CacheKey == key,
                cancellationToken);
            if (cached is null)
            {
                database.CachedReports.Add(new CachedReport
                {
                    CacheKey = key,
                    Json = JsonSerializer.Serialize(summary, JsonOptions),
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                cached.Json = JsonSerializer.Serialize(summary, JsonOptions);
                cached.UpdatedAt = now;
            }

            await database.SaveChangesAsync(cancellationToken);
            return now;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var employees = await teamDirectory.GetEmployeesAsync(cancellationToken);
        foreach (var employee in employees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshEmployeeAsync(employee.Id, cancellationToken);
        }
    }

    internal static string CacheKey(string employeeId) => $"activity:{employeeId}";
}
