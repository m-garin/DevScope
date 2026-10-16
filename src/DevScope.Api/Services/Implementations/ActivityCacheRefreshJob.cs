using DevScope.Api.Configuration;
using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace DevScope.Api.Services.Implementations;

public sealed class ActivityCacheRefreshJob(
    IServiceScopeFactory scopeFactory,
    IOptions<DevScopeOptions> options,
    ILogger<ActivityCacheRefreshJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DataSource.Equals("Mcp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var settingsScope = scopeFactory.CreateAsyncScope();
                var settingsService = settingsScope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
                var settings = await settingsService.GetAsync(stoppingToken);
                var delay = DelayUntilNextRun(settings.CacheRefreshInterval);
                if (delay > TimeSpan.FromMinutes(1))
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }
                await Task.Delay(delay, stoppingToken);

                await using var refreshScope = scopeFactory.CreateAsyncScope();
                var refreshService = refreshScope.ServiceProvider.GetRequiredService<IActivityRefreshService>();
                await refreshService.RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduled MCP cache refresh failed.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private TimeSpan DelayUntilNextRun(string interval)
    {
        var now = DateTimeOffset.Now;
        if (interval.Equals(CacheRefreshIntervals.Hourly, StringComparison.OrdinalIgnoreCase))
        {
            var nextHour = new DateTimeOffset(
                now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(1);
            return nextHour - now;
        }

        var nextDay = now.Date.AddHours(options.Value.DailyRefreshHour);
        if (nextDay <= now)
        {
            nextDay = nextDay.AddDays(1);
        }
        return nextDay - now;
    }
}
