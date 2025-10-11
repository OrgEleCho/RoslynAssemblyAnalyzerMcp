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

