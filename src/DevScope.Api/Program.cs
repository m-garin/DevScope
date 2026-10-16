using DevScope.Api.Configuration;
using DevScope.Api.Data;
using DevScope.Api.Integrations.Mcp;
using DevScope.Api.Services.Implementations;
using DevScope.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DevScopeOptions>(builder.Configuration.GetSection(DevScopeOptions.SectionName));
builder.Services.AddSingleton<IMcpConfigurationReader, McpConfigurationReader>();
builder.Services.AddSingleton<IMcpGatewayClient, McpGatewayClient>();
builder.Services.AddSingleton<ITeamDirectory, TeamDirectory>();
var dataSource = builder.Configuration[$"{DevScopeOptions.SectionName}:DataSource"] ?? "Mock";
if (dataSource.Equals("Mock", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IActivityProvider, MockActivityProvider>();
    builder.Services.AddSingleton<IActivityRefreshService, MockActivityRefreshService>();
}
else if (dataSource.Equals("Mcp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<McpActivityProvider>();
    builder.Services.AddScoped<IActivityRefreshService, ActivityRefreshService>();
    builder.Services.AddScoped<IActivityProvider, CachedActivityProvider>();
}
else
{
    throw new InvalidOperationException(
        $"Unsupported DevScope:DataSource '{dataSource}'. Supported values: Mock, Mcp.");
}

builder.Services.AddSingleton<ITaskAnalysisService, MockTaskAnalysisService>();
builder.Services.AddScoped<IApplicationSettingsService, ApplicationSettingsService>();
builder.Services.AddPooledDbContextFactory<DevScopeDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DevScope")
        ?? "Host=postgres;Port=5432;Database=devscope;Username=devscope;Password=devscope"));
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<McpExceptionHandler>();
builder.Services.AddHostedService<ActivityCacheRefreshJob>();

var app = builder.Build();

Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data"));
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DevScopeDbContext>>();
    await using var database = await factory.CreateDbContextAsync();
    await database.Database.EnsureCreatedAsync();
    await database.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "ApplicationSettings" (
            "Id" integer NOT NULL,
            "DefaultLlmModel" text NOT NULL,
            "CacheRefreshInterval" text NOT NULL,
            "UpdatedAt" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_ApplicationSettings" PRIMARY KEY ("Id")
        );
        """);
}

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
