using DevScope.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Controllers;

[ApiController]
[Route("api/activity/refresh")]
public sealed class ActivityRefreshController(
    IActivityRefreshService refreshService,
    IApplicationSettingsService settingsService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Refresh(
        [FromQuery] string employeeId,
        CancellationToken cancellationToken)
    {
        var refreshedAt = await refreshService.RefreshEmployeeAsync(employeeId, cancellationToken);
        return refreshedAt is null
            ? NotFound(new { error = "Сотрудник не найден." })
            : Ok(new { refreshedAt });
    }

    [HttpPost("all")]
    public async Task<IActionResult> RefreshAll(CancellationToken cancellationToken)
    {
        await refreshService.RefreshAllAsync(cancellationToken);
        return Ok(await settingsService.GetAsync(cancellationToken));
    }
}
