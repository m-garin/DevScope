namespace DevScope.Api.Domain;

public sealed record ApplicationSettingsSnapshot(
    string DefaultLlmModel,
    string CacheRefreshInterval,
    DateTimeOffset? LastCacheRefreshAt,
    int CachedEmployeeCount);

public static class CacheRefreshIntervals
{
    public const string Hourly = "Hourly";
    public const string Daily = "Daily";

    public static bool IsSupported(string value) =>
        value.Equals(Hourly, StringComparison.OrdinalIgnoreCase) ||
        value.Equals(Daily, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string value) =>
        value.Equals(Hourly, StringComparison.OrdinalIgnoreCase) ? Hourly : Daily;
}
