using System.Text.Json;
using DevScope.Api.Data;
using DevScope.Api.Data.Entities;
using DevScope.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DevScope.Api.Services.Implementations;

public sealed class ActivityRefreshService(
    McpActivityProvider source,
    IDbContextFactory<DevScopeDbContext> dbContextFactory) : IActivityRefreshService
{
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DateTimeOffset?> RefreshEmployeeAsync(
        string employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            var summary = await source.GetPeriodSummaryAsync(
                employeeId,
                from,
                to,
                cancellationToken);
            if (summary is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var key = CacheKey(employeeId, from, to);
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
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var keys = await database.CachedReports.AsNoTracking()
            .Where(item => item.CacheKey.StartsWith("activity:"))
            .Select(item => item.CacheKey)
            .ToArrayAsync(cancellationToken);
        var ranges = keys.Select(TryParseCacheKey).Where(item => item is not null).Distinct().ToArray();
        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshEmployeeAsync(range!.Value.EmployeeId, range.Value.From, range.Value.To, cancellationToken);
        }
    }

    internal static string CacheKey(string employeeId, DateOnly from, DateOnly to) =>
        $"activity:{employeeId}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";

    private static (string EmployeeId, DateOnly From, DateOnly To)? TryParseCacheKey(string key)
    {
        var parts = key.Split(':');
        if (parts.Length < 4 ||
            !DateOnly.TryParse(parts[^2], out var from) ||
            !DateOnly.TryParse(parts[^1], out var to))
        {
            return null;
        }
        return (string.Join(':', parts[1..^2]), from, to);
    }
}
