using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(IApplicationSettingsService settingsService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await settingsService.GetAsync(cancellationToken));

    [HttpPut]
    public async Task<IActionResult> Update(
        [FromBody] UpdateSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!LlmModels.IsSupported(request.DefaultLlmModel))
        {
            return BadRequest(new { error = "Неизвестная модель LLM." });
        }
        if (!CacheRefreshIntervals.IsSupported(request.CacheRefreshInterval))
        {
            return BadRequest(new { error = "Допустимая частота обновления: Hourly или Daily." });
        }

        return Ok(await settingsService.UpdateAsync(
            request.DefaultLlmModel,
            request.CacheRefreshInterval,
            cancellationToken));
    }
}

public sealed record UpdateSettingsRequest(string DefaultLlmModel, string CacheRefreshInterval);
