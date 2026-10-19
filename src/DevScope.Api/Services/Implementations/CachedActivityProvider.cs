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
        var snapshot = await ReadSnapshotAsync(employeeId, from, to, cancellationToken);
        if (snapshot is null)
        {
            await refreshService.RefreshEmployeeAsync(employeeId, from, to, cancellationToken);
            snapshot = await ReadSnapshotAsync(employeeId, from, to, cancellationToken);
        }
        if (snapshot is null)
        {
            return null;
        }

        var periodEvents = snapshot.Value.Summary.Events
            .Where(item => IsInPeriod(item, from, to) && IsEmployeeTimelineEvent(item))
            .OrderByDescending(item => item.OccurredAt)
            .ToArray();
        var cachedTasks = snapshot.Value.Summary.Tasks
            .Select(activity =>
            {
                var worklogs = activity.Worklogs
                    .Where(worklog => worklog.Date >= from && worklog.Date <= to)
                    .OrderByDescending(worklog => worklog.Date)
                    .ToArray();
                return new TaskActivity(
                    activity.Task,
                    worklogs.Sum(worklog => worklog.DurationMinutes),
                    worklogs,
                    activity.IsOwnTask);
            })
            .ToArray();
        var currentReviewAssignments = snapshot.Value.Summary.CurrentReviewAssignments ?? cachedTasks
            .Where(activity => activity.Task.GitLab?.EmployeeIsReviewer == true)
            .ToArray();
        var tasks = cachedTasks
            .Where(activity => activity.Worklogs.Count > 0 || periodEvents.Any(item =>
                item.Source.Equals("GitLab", StringComparison.OrdinalIgnoreCase) &&
                item.TaskId.Equals(activity.Task.Id, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(activity => activity.Worklogs.Select(worklog => worklog.Date).DefaultIfEmpty(DateOnly.MinValue).Max())
            .ThenBy(activity => activity.Task.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var reviewTasks = tasks.Count(activity => activity.Worklogs.Any(worklog =>
            worklog.Type.Contains("ревью", StringComparison.OrdinalIgnoreCase) ||
            worklog.Type.Contains("review", StringComparison.OrdinalIgnoreCase)));
        var worklogTaskCount = tasks.Count(activity => activity.Worklogs.Count > 0);
        var gitLabOnlyTaskCount = tasks.Length - worklogTaskCount;
        var narrative = snapshot.Value.Summary.HasExactWorklogDurations
            ? $"Списано время по {worklogTaskCount} задачам" +
              (reviewTasks > 0 ? $", включая участие в ревью по {reviewTasks}" : string.Empty) +
              "." +
              (gitLabOnlyTaskCount > 0
                  ? $" Ещё по {gitLabOnlyTaskCount} задачам подтверждена активность сотрудника в GitLab."
                  : string.Empty)
            : $"Списания подтверждены по {worklogTaskCount} задачам в " +
              $"{tasks.SelectMany(item => item.Worklogs).Select(item => item.Date).Distinct().Count()} дн. выбранного периода." +
              (gitLabOnlyTaskCount > 0
                  ? $" Ещё по {gitLabOnlyTaskCount} задачам подтверждена активность сотрудника в GitLab."
                  : string.Empty);

        return new PeriodSummary(
            snapshot.Value.Summary.Employee,
            from,
            to,
            tasks.Sum(task => task.LoggedMinutes),
            tasks.Length,
            tasks.Count(task => task.Task.GitLab is not null),
            narrative,
            tasks,
            periodEvents,
            snapshot.Value.Summary.Limitations,
            snapshot.Value.UpdatedAt,
            snapshot.Value.Summary.HasExactWorklogDurations,
            currentReviewAssignments);
    }

    private static bool IsInPeriod(ActivityEvent item, DateOnly from, DateOnly to)
    {
        var date = DateOnly.FromDateTime(item.OccurredAt.Date);
        return date >= from && date <= to;
    }

    private static bool IsEmployeeTimelineEvent(ActivityEvent item)
    {
        if (!item.Source.Equals("GitLab", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Compatibility filter for snapshots created before GitLab events were limited
        // to confirmed actions of the selected employee.
        return !item.Kind.Equals("Discussion", StringComparison.OrdinalIgnoreCase) &&
               !item.Title.StartsWith("Коммит другого автора", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<TaskActivity?> GetTaskActivityAsync(
        string employeeId,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var prefix = $"activity:{employeeId}:";
        var reports = await database.CachedReports.AsNoTracking()
            .Where(item => item.CacheKey.StartsWith(prefix))
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => item.Json)
            .ToArrayAsync(cancellationToken);
        foreach (var json in reports)
        {
            var summary = JsonSerializer.Deserialize<PeriodSummary>(json, JsonOptions);
            var activity = summary?.Tasks.FirstOrDefault(item =>
                item.Task.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase));
            if (activity is not null)
            {
                return activity;
            }
        }
        return null;
    }

    private async Task<(PeriodSummary Summary, DateTimeOffset UpdatedAt)?> ReadSnapshotAsync(
        string employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var database = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var key = ActivityRefreshService.CacheKey(employeeId, from, to);
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
