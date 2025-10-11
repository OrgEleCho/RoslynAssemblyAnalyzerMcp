using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RoslynAssemblyAnalyzerMcp;
using Serilog;
using Serilog.Events;
using System.Text.Encodings.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(configuration =>
{
    configuration.WriteTo.Console();
    configuration.MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning);
});
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

app.UseSerilogRequestLogging();

app.MapMcp();

await app.Services.GetRequiredService<RoslynService>().InitializeAsync();

app.Run("http://localhost:43541");

