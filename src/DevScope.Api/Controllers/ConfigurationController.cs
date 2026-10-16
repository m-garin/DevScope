using DevScope.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Controllers;

[ApiController]
[Route("api/configuration")]
public sealed class ConfigurationController(IMcpConfigurationReader configurationReader) : ControllerBase
{
    [HttpGet("mcp")]
    public async Task<IActionResult> GetMcp(CancellationToken cancellationToken) =>
        Ok(await configurationReader.GetStatusesAsync(cancellationToken));
}
