using DevScope.MockMcp.Tools;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<MockDataStore>();
builder.Services.AddControllers();
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<YouTrackMockTools>()
    .WithTools<GitLabMockTools>();

var app = builder.Build();

app.MapControllers();
app.MapMcp("/mcp");

app.Run();

