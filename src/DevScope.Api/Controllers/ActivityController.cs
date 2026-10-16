using DevScope.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DevScope.Api.Controllers;

[ApiController]
[Route("api/activity")]
public sealed class ActivityController(IActivityProvider activityProvider) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string employeeId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken cancellationToken)
    {
        if (to < from || to.DayNumber - from.DayNumber > 366)
        {
            return BadRequest(new { error = "Некорректный диапазон дат." });
        }

        var summary = await activityProvider.GetPeriodSummaryAsync(employeeId, from, to, cancellationToken);
        return summary is null ? NotFound() : Ok(summary);
    }
}
