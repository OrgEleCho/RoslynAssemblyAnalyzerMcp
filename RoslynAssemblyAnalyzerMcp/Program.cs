using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RoslynAssemblyAnalyzerMcp;
using System.Text.Encodings.Web;

await RoslynMcp.Initialize();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

var app = builder.Build();

app.MapMcp();

app.Run("http://localhost:43541");

