using DevScope.Api.Domain;

namespace DevScope.Api.Services.Interfaces;

public interface IApplicationSettingsService
{
    Task<ApplicationSettingsSnapshot> GetAsync(CancellationToken cancellationToken);
    Task<ApplicationSettingsSnapshot> UpdateAsync(
        string defaultLlmModel,
        string cacheRefreshInterval,
        CancellationToken cancellationToken);
}
