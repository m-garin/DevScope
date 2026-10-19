using System.Text.Json;
using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;

namespace DevScope.Api.Services.Implementations;

public sealed class MockActivityProvider(IWebHostEnvironment environment) : IActivityProvider
{
    private readonly Lazy<Task<MockData>> _data = new(() => LoadAsync(environment.ContentRootPath));

    public async Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken)
    {
        var data = await _data.Value.WaitAsync(cancellationToken);
        return data.Employees.ToArray();
    }

    public async Task<PeriodSummary?> GetPeriodSummaryAsync(
        string employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var data = await _data.Value.WaitAsync(cancellationToken);
        var employee = data.Employees.SingleOrDefault(item => item.Id == employeeId);
        if (employee is null)
        {
            return null;
        }

        var worklogs = data.Worklogs
            .Where(item => item.AuthorId == employeeId && item.Date >= from && item.Date <= to)
            .OrderByDescending(item => item.Date)
            .ToArray();

        var taskIds = worklogs.Select(item => item.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tasks = data.Tasks
            .Where(item => taskIds.Contains(item.Id))
            .Select(task =>
            {
                var enrichedTask = EnrichMockTask(task, employee);
                return new TaskActivity(
                    enrichedTask,
                    worklogs.Where(item => item.TaskId == task.Id).Sum(item => item.DurationMinutes),
                    worklogs.Where(item => item.TaskId == task.Id).ToArray(),
                    task.DeveloperId == employeeId);
            })
            .OrderByDescending(item => item.Worklogs.Max(worklog => worklog.Date))
            .ToArray();

        var events = data.Events
            .Where(item => item.EmployeeId == employeeId && DateOnly.FromDateTime(item.OccurredAt.Date) >= from && DateOnly.FromDateTime(item.OccurredAt.Date) <= to)
            .OrderByDescending(item => item.OccurredAt)
            .ToArray();

        var reviewCount = worklogs.Count(item => item.Type.Contains("ревью", StringComparison.OrdinalIgnoreCase));
        var ownTaskCount = tasks.Count(item => item.IsOwnTask);
        var narrative = reviewCount > 0
            ? $"Работал над {ownTaskCount} своими задачами и участвовал в Code Review чужих задач."
            : $"Работал над {ownTaskCount} задачами разработки.";
        var currentReviewAssignments = data.Tasks
            .Select(task => EnrichMockTask(task, employee))
            .Where(task => task.GitLab?.EmployeeIsReviewer == true &&
                task.GitLab.MergeRequestState is "open" or "opened")
            .Select(task => new TaskActivity(task, 0, [], task.DeveloperId == employeeId))
            .ToArray();

        return new PeriodSummary(
            employee,
            from,
            to,
            worklogs.Sum(item => item.DurationMinutes),
            tasks.Length,
            tasks.Count(item => item.Task.GitLab is not null),
            narrative,
            tasks,
            events,
            [],
            CurrentReviewAssignments: currentReviewAssignments);
    }

    public async Task<TaskActivity?> GetTaskActivityAsync(
        string employeeId,
        string taskId,
        CancellationToken cancellationToken)
    {
        var data = await _data.Value.WaitAsync(cancellationToken);
        var task = data.Tasks.SingleOrDefault(item =>
            string.Equals(item.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (task is null || data.Employees.All(item => item.Id != employeeId))
        {
            return null;
        }

        var worklogs = data.Worklogs
            .Where(item => item.AuthorId == employeeId &&
                string.Equals(item.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Date)
            .ToArray();
        if (worklogs.Length == 0)
        {
            return null;
        }

        return new TaskActivity(
            EnrichMockTask(task, data.Employees.Single(item => item.Id == employeeId)),
            worklogs.Sum(item => item.DurationMinutes),
            worklogs,
            task.DeveloperId == employeeId);
    }

    private static WorkTask EnrichMockTask(WorkTask task, Employee employee)
    {
        if (task.GitLab is not { } gitLab)
        {
            return task;
        }

        var isEmployeeAuthor = string.Equals(task.DeveloperId, employee.Id, StringComparison.OrdinalIgnoreCase);
        var isReviewer = !isEmployeeAuthor &&
            (task.Type?.Contains("review", StringComparison.OrdinalIgnoreCase) == true ||
             task.Summary.Contains("review", StringComparison.OrdinalIgnoreCase));
        return task with
        {
            GitLab = gitLab with
            {
                EmployeeCommitCount = isEmployeeAuthor ? gitLab.CommitCount : 0,
                EmployeeIsReviewer = isReviewer,
                CommitAuthorsKnown = true,
                EmployeeActionFactsKnown = true,
                OtherCommitAuthors = !isEmployeeAuthor && gitLab.CommitCount > 0
                    ? [$"Mock Developer ({gitLab.CommitCount})"]
                    : []
            }
        };
    }

    private static async Task<MockData> LoadAsync(string contentRoot)
    {
        var candidates = new[]
        {
            Path.Combine(contentRoot, "Data", "mock-data.json"),
            Path.Combine(contentRoot, "data", "mock-data.json"),
            Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "data", "mock-data.json"))
        };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Mock data file was not found.", string.Join(";", candidates));

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MockData>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Mock data file is empty.");
    }

    private sealed record MockData(
        IReadOnlyList<Employee> Employees,
        IReadOnlyList<WorkTask> Tasks,
        IReadOnlyList<Worklog> Worklogs,
        IReadOnlyList<ActivityEvent> Events);
}

