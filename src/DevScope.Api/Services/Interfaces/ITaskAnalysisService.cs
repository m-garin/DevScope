using DevScope.Api.Domain;

namespace DevScope.Api.Services.Interfaces;

public interface ITaskAnalysisService
{
    Task<TaskAnalysis> AnalyzeAsync(TaskActivity activity, string model, CancellationToken cancellationToken);
}
