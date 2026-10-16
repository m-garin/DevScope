using DevScope.Api.Data;
using DevScope.Api.Data.Entities;
using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DevScope.Api.Services.Implementations;

public sealed class ApplicationSettingsService(
    IDbContextFactory<DevScopeDbContext> dbContextFactory) : IApplicationSettingsService
{
    private const int GlobalSettingsId = 1;

    public async Task<ApplicationSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await database.ApplicationSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == GlobalSettingsId, cancellationToken);
        return await CreateSnapshotAsync(database, settings, cancellationToken);
    }

    public async Task<ApplicationSettingsSnapshot> UpdateAsync(
        string defaultLlmModel,
        string cacheRefreshInterval,
        CancellationToken cancellationToken)
    {
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await database.ApplicationSettings
            .SingleOrDefaultAsync(item => item.Id == GlobalSettingsId, cancellationToken);
        if (settings is null)
        {
            settings = new ApplicationSetting
            {
                Id = GlobalSettingsId,
                DefaultLlmModel = defaultLlmModel,
                CacheRefreshInterval = CacheRefreshIntervals.Normalize(cacheRefreshInterval),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            database.ApplicationSettings.Add(settings);
        }
        else
        {
            settings.DefaultLlmModel = defaultLlmModel;
            settings.CacheRefreshInterval = CacheRefreshIntervals.Normalize(cacheRefreshInterval);
            settings.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await database.SaveChangesAsync(cancellationToken);
        return await CreateSnapshotAsync(database, settings, cancellationToken);
    }

    private static async Task<ApplicationSettingsSnapshot> CreateSnapshotAsync(
        DevScopeDbContext database,
        ApplicationSetting? settings,
        CancellationToken cancellationToken)
    {
        var cache = database.CachedReports.AsNoTracking()
            .Where(item => item.CacheKey.StartsWith("activity:"));
        var lastRefresh = await cache.MaxAsync(item => (DateTimeOffset?)item.UpdatedAt, cancellationToken);
        var keys = await cache.Select(item => item.CacheKey).ToArrayAsync(cancellationToken);
        var count = keys.Select(key =>
        {
            var parts = key.Split(':');
            return parts.Length >= 4
                ? string.Join(':', parts[1..^2])
                : parts.Length == 2 ? parts[1] : key;
        }).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new ApplicationSettingsSnapshot(
            settings?.DefaultLlmModel ?? LlmModels.Default,
            settings?.CacheRefreshInterval ?? CacheRefreshIntervals.Daily,
            lastRefresh,
            count);
    }
}
