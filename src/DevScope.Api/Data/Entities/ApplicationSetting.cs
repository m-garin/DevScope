namespace DevScope.Api.Data.Entities;

public sealed class ApplicationSetting
{
    public int Id { get; set; }
    public required string DefaultLlmModel { get; set; }
    public required string CacheRefreshInterval { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
