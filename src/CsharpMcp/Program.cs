using ArchiMetrics.Analysis;
using CsharpMcp;
using CsharpMcp.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var config = ServerConfig.Parse(args);

// Refuse to start in a non-.NET directory before any heavy init (MSBuildLocator,
// MSBuildWorkspace, ONNX model). Scanning a non-.NET tree was costing ~8GB RAM.
if (!HasDotNetProject(config.RootPath))
{
    Console.Error.WriteLine($"csharp-language-mcp: no .csproj or .sln found under '{config.RootPath}'. Refusing to start in a non-.NET directory.");
    return 1;
}

// Build a temporary logger factory for workspace loading (before host is built)
using var earlyLoggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
// Load in the background so the MCP server can start responding immediately.
// Tools check workspace.IsReady and return a "still loading" response until load completes.
var workspace = RoslynWorkspace.Create(config.RootPath, earlyLoggerFactory);

var modelDir = config.ModelDir
    ?? @"D:\src\External\ArchiMetrics\models\unixcoder\";
var agent = Directory.Exists(modelDir)
    ? CodeAnalysisAgent.WithOnnxModel(workspace.InnerWorkspace, config.RootPath, modelDir)
    : new CodeAnalysisAgent(workspace.InnerWorkspace, config.RootPath);

var builder = Host.CreateApplicationBuilder(args);
var services = builder.Services
    .AddSingleton(config)
    .AddSingleton(workspace)
    .AddSingleton(agent)
    .AddSingleton<CsharpTools>()
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = config.Name, Version = "1.0.0" };
        var instructions = "C# and NuGet code intelligence server powered by Roslyn. Provides navigation, type info, diagnostics, refactoring, and NuGet package exploration for .NET projects.";
        if (config.Description is not null)
            instructions += " " + config.Description;
        options.ServerInstructions = instructions;
    })
    .WithStdioServerTransport()
    .WithTools<CsharpTools>();

if (config.EnableQuality)
    services.WithTools<QualityHotspotsTools>();

if (config.EnableNuget)
    services.WithTools<CsharpMcp.Nuget.NugetTools>();

await builder.Build().RunAsync();
return 0;

static bool HasDotNetProject(string rootPath)
{
    var enumOpts = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        MatchType = MatchType.Simple,
    };

    foreach (var pattern in new[] { "*.csproj", "*.sln", "*.slnx", "*.slnf" })
    {
        foreach (var path in Directory.EnumerateFiles(rootPath, pattern, enumOpts))
        {
            var norm = path.Replace('\\', '/');
            if (norm.Contains("/bin/") || norm.Contains("/obj/") || norm.Contains("/node_modules/"))
                continue;
            return true;
        }
    }
    return false;
}
