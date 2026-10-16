using DevScope.Api.Domain;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Controllers;

[ApiController]
[Route("api/llm")]
public sealed class LlmController : ControllerBase
{
    [HttpGet("models")]
    public IActionResult GetModels() => Ok(new
    {
        defaultModel = LlmModels.Default,
        models = LlmModels.All
    });
}
