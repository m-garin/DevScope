using Microsoft.AspNetCore.Mvc;

namespace DevScope.MockMcp.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", service = "DevScope.MockMcp" });
}
