using DevScope.Api.Domain;
using DevScope.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Controllers;

[ApiController]
[Route("api/llm")]
public sealed class LlmController(IApplicationSettingsService settingsService) : ControllerBase
{
    [HttpGet("models")]
    public async Task<IActionResult> GetModels(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        return Ok(new
        {
            defaultModel = settings.DefaultLlmModel,
            models = LlmModels.All
        });
    }
}
