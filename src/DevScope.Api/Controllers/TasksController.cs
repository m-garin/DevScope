using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Controllers;

[ApiController]
[Route("api/tasks")]
public sealed class TasksController(
    IActivityProvider activityProvider,
    ITaskAnalysisService taskAnalysisService) : ControllerBase
{
    [HttpPost("{taskId}/analysis")]
    public async Task<IActionResult> Analyze(
        [FromRoute] string taskId,
        [FromQuery] string employeeId,
        [FromQuery] string? model,
        CancellationToken cancellationToken)
    {
        var selectedModel = string.IsNullOrWhiteSpace(model) ? LlmModels.Default : model;
        if (!LlmModels.IsSupported(selectedModel))
        {
            return BadRequest(new { error = "Неизвестная модель LLM." });
        }

        var activity = await activityProvider.GetTaskActivityAsync(employeeId, taskId, cancellationToken);
        if (activity is null)
        {
            return NotFound(new { error = "Задача со списаниями сотрудника не найдена." });
        }

        return Ok(await taskAnalysisService.AnalyzeAsync(activity, selectedModel, cancellationToken));
    }
}
