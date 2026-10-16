namespace DevScope.Api.Data.Entities;

public sealed class CachedReport
{
    public long Id { get; set; }
    public required string CacheKey { get; set; }
    public required string Json { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
