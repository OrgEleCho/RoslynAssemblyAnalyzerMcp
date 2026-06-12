using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoslynAssemblyAnalyzerMcp;
using Serilog;
using Serilog.Events;
using System.Text.Encodings.Web;

var serverOptions = ServerOptions.Parse(args);

if (serverOptions.Transport == ServerTransport.Stdio)
{
    var builder = Host.CreateApplicationBuilder(args);
    ConfigureServices(builder.Services);

    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();
    await app.Services.GetRequiredService<RoslynService>().InitializeAsync();
    await app.RunAsync();
}
else
{
    var builder = WebApplication.CreateBuilder(args);
    ConfigureServices(builder.Services);

    builder.Services.AddSerilog(configuration =>
    {
        configuration.WriteTo.Console();
        configuration.MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning);
    });

    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });

    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.MapMcp();

    await app.Services.GetRequiredService<RoslynService>().InitializeAsync();
    await app.RunAsync(serverOptions.HttpUrl);
}

static void ConfigureServices(IServiceCollection services)
{
    services.AddSingleton<RoslynService>();
    services.AddSingleton<RoslynMcp>();
    services.AddSingleton<AssemblyDecompiler>();
    services.AddSingleton<AssemblyResourceService>();
}

internal enum ServerTransport
{
    Http,
    Stdio
}

internal sealed record ServerOptions(ServerTransport Transport, string HttpUrl)
{
    public static ServerOptions Parse(string[] args)
    {
        var transport = args.Any(arg => arg.Equals("--stdio", StringComparison.OrdinalIgnoreCase))
            ? ServerTransport.Stdio
            : ServerTransport.Http;

        var httpUrl = GetOptionValue(args, "--url") ?? "http://localhost:43541";
        return new ServerOptions(transport, httpUrl);
    }

    private static string? GetOptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(optionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return i + 1 < args.Length ? args[i + 1] : null;
        }

        var prefix = $"{optionName}=";
        return args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }
}
