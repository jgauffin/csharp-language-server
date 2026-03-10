using ArchiMetrics.Analysis;
using ArchiMetrics.Analysis.Metrics;
using CsharpMcp;
using CsharpMcp.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var config = ServerConfig.Parse(args);

// Build a temporary logger factory for workspace loading (before host is built)
using var earlyLoggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
var workspace = await RoslynWorkspace.LoadAsync(config.RootPath, earlyLoggerFactory);

var modelDir = config.ModelDir
    ?? @"D:\src\External\ArchiMetrics\models\unixcoder\";
var agent = Directory.Exists(modelDir)
    ? CodeAnalysisAgent.WithOnnxModel(workspace.InnerWorkspace, config.RootPath, modelDir)
    : new CodeAnalysisAgent(workspace.InnerWorkspace, config.RootPath);

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddSingleton(config)
    .AddSingleton(workspace)
    .AddSingleton(agent)
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = config.Name, Version = "1.0.0" };
        var instructions = "C# and NuGet code intelligence server powered by Roslyn. Provides navigation, type info, diagnostics, refactoring, and NuGet package exploration for .NET projects.";
        if (config.Description is not null)
            instructions += " " + config.Description;
        options.ServerInstructions = instructions;
    })
    .WithStdioServerTransport()
    .WithTools<CsharpTools>()
    .WithTools<CsharpMcp.Nuget.NugetTools>();

await builder.Build().RunAsync();
