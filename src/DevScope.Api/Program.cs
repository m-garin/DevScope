using DevScope.Api.Configuration;
using DevScope.Api.Data;
using DevScope.Api.Services.Implementations;
using DevScope.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DevScopeOptions>(builder.Configuration.GetSection(DevScopeOptions.SectionName));
builder.Services.AddSingleton<IMcpConfigurationReader, McpConfigurationReader>();
var dataSource = builder.Configuration[$"{DevScopeOptions.SectionName}:DataSource"] ?? "Mock";
if (dataSource.Equals("Mock", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IActivityProvider, MockActivityProvider>();
}
else if (dataSource.Equals("Mcp", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "DevScope:DataSource=Mcp is not available yet: McpActivityProvider must be implemented before real data can be used.");
}
else
{
    throw new InvalidOperationException(
        $"Unsupported DevScope:DataSource '{dataSource}'. Supported value: Mock.");
}

builder.Services.AddSingleton<ITaskAnalysisService, MockTaskAnalysisService>();
builder.Services.AddDbContext<DevScopeDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DevScope") ?? "Data Source=data/devscope.db"));
builder.Services.AddControllers();
builder.Services.AddProblemDetails();

var app = builder.Build();

Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data"));
await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<DevScopeDbContext>();
    await database.Database.EnsureCreatedAsync();
}

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
