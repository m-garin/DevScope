using System.Text.Json;
using DevScope.Api.Data;
using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DevScope.Api.Services.Implementations;

public sealed class CachedActivityProvider(
    ITeamDirectory teamDirectory,
    IActivityRefreshService refreshService,
    IDbContextFactory<DevScopeDbContext> dbContextFactory) : IActivityProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken) =>
        teamDirectory.GetEmployeesAsync(cancellationToken);

    public async Task<PeriodSummary?> GetPeriodSummaryAsync(
        string employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadSnapshotAsync(employeeId, cancellationToken);
        if (snapshot is null)
        {
            await refreshService.RefreshEmployeeAsync(employeeId, cancellationToken);
            snapshot = await ReadSnapshotAsync(employeeId, cancellationToken);
        }
        if (snapshot is null)
        {
            return null;
        }

        var tasks = snapshot.Value.Summary.Tasks
            .Select(activity =>
            {
                var worklogs = activity.Worklogs
                    .Where(worklog => worklog.Date >= from && worklog.Date <= to)
                    .OrderByDescending(worklog => worklog.Date)
                    .ToArray();
                return worklogs.Length == 0
                    ? null
                    : new TaskActivity(
                        activity.Task,
                        worklogs.Sum(worklog => worklog.DurationMinutes),
                        worklogs,
                        activity.IsOwnTask);
            })
            .Where(activity => activity is not null)
            .Cast<TaskActivity>()
            .OrderByDescending(activity => activity.Worklogs.Max(worklog => worklog.Date))
            .ToArray();
        var reviewTasks = tasks.Count(activity => activity.Worklogs.Any(worklog =>
            worklog.Type.Contains("ревью", StringComparison.OrdinalIgnoreCase) ||
            worklog.Type.Contains("review", StringComparison.OrdinalIgnoreCase)));

        return new PeriodSummary(
            snapshot.Value.Summary.Employee,
            from,
            to,
            tasks.Sum(task => task.LoggedMinutes),
            tasks.Length,
            tasks.Count(task => task.Task.GitLab is not null),
            reviewTasks > 0
                ? $"Списано время по {tasks.Length} задачам, включая участие в ревью по {reviewTasks}."
                : $"Списано время по {tasks.Length} задачам.",
            tasks,
            snapshot.Value.Summary.Events.Where(item =>
            {
                var date = DateOnly.FromDateTime(item.OccurredAt.Date);
                return date >= from && date <= to;
            }).ToArray(),
            snapshot.Value.Summary.Limitations,
            snapshot.Value.UpdatedAt);
    }

    public async Task<TaskActivity?> GetTaskActivityAsync(
        string employeeId,
        string taskId,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadSnapshotAsync(employeeId, cancellationToken);
        return snapshot?.Summary.Tasks.FirstOrDefault(task =>
            task.Task.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(PeriodSummary Summary, DateTimeOffset UpdatedAt)?> ReadSnapshotAsync(
        string employeeId,
        CancellationToken cancellationToken)
    {
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var key = ActivityRefreshService.CacheKey(employeeId);
        var cached = await database.CachedReports.AsNoTracking().SingleOrDefaultAsync(
            item => item.CacheKey == key,
            cancellationToken);
        if (cached is null)
        {
            return null;
        }

        var summary = JsonSerializer.Deserialize<PeriodSummary>(cached.Json, JsonOptions);
        return summary is null ? null : (summary, cached.UpdatedAt);
    }
}
