using DevScope.Api.Configuration;
using DevScope.Api.Data;
using DevScope.Api.Services.Implementations;
using DevScope.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DevScopeOptions>(builder.Configuration.GetSection(DevScopeOptions.SectionName));
builder.Services.AddSingleton<IMcpConfigurationReader, McpConfigurationReader>();
builder.Services.AddSingleton<IActivityProvider, MockActivityProvider>();
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
