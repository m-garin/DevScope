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
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rangeFrom = from ?? today.AddDays(-6);
        var rangeTo = to ?? today;
        if (rangeTo < rangeFrom || rangeTo.DayNumber - rangeFrom.DayNumber > 30)
        {
            return BadRequest(new { error = "Некорректный диапазон дат." });
        }
        var refreshedAt = await refreshService.RefreshEmployeeAsync(
            employeeId, rangeFrom, rangeTo, cancellationToken);
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

    [HttpPost("clear")]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        var clearedReports = await refreshService.ClearCacheAsync(cancellationToken);
        return Ok(new { clearedReports });
    }
}
