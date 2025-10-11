using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RoslynAssemblyAnalyzerMcp;
using Serilog;
using System.Text.Encodings.Web;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog();
builder.Services.AddSingleton<RoslynService>();
builder.Services.AddSingleton<RoslynMcp>();

builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.ConfigureSessionOptions = async (httpContext, mcpOptions, cancellationToken) =>
        {
            var roslynMcp = httpContext.RequestServices.GetRequiredService<RoslynMcp>();
            mcpOptions.ToolCollection = roslynMcp.GetMcpTools();
        };
    })
    .WithToolsFromAssembly();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

var app = builder.Build();

app.MapMcp();

await app.Services.GetRequiredService<RoslynService>().InitializeAsync();

app.Run("http://localhost:43541");

