using ArchiMetrics.Analysis;
using CsharpMcp;
using CsharpMcp.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var config = ServerConfig.Parse(args);
var serverVersion = typeof(ServerConfig).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

// Refuse to start in a non-.NET directory before any heavy init (MSBuildLocator,
// MSBuildWorkspace, ONNX model). Scanning a non-.NET tree was costing ~8GB RAM.
if (!HasDotNetProject(config.RootPath))
{
    Console.Error.WriteLine($"csharp-language-mcp: no .csproj or .sln found under '{config.RootPath}'. Refusing to start in a non-.NET directory.");
    return 1;
}

// Build a temporary logger factory for workspace loading (before host is built).
// All console logging goes to stderr: stdout is the MCP stdio transport, so anything
// written there is parsed as protocol, rejected by the client and lost.
using var earlyLoggerFactory = LoggerFactory.Create(b => b
    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
    .SetMinimumLevel(LogLevel.Information));
// Load in the background so the MCP server can start responding immediately.
// Tools check workspace.IsReady and return a "still loading" response until load completes.
var workspace = RoslynWorkspace.Create(config.RootPath, earlyLoggerFactory);

var agent = new CodeAnalysisAgent(workspace.InnerWorkspace, config.RootPath);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
var services = builder.Services
    .AddSingleton(config)
    .AddSingleton(workspace)
    .AddSingleton(agent)
    .AddSingleton<CsharpTools>()
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = config.Name, Version = serverVersion };
        // Agents default to grep/glob/whole-file reads. The instructions land in the agent's system
        // prompt, so they state which tool replaces which habit rather than describing the server.
        var instructions =
            $"C# code intelligence (Roslyn) for the .NET solution at {config.RootPath}. " +
            "For C# code, use these tools instead of grep, glob or reading whole files: " +
            "locate a type or member by name: find. " +
            "See what a file contains before reading it: get_outline. " +
            "Where a symbol is declared: get_definition. " +
            "Everything that uses a symbol: get_references; callers/callees: get_call_hierarchy; " +
            "implementations of an interface or abstract member: get_implementations; base/derived types: get_type_hierarchy. " +
            "Resolved type, signature and docs: get_hover. " +
            "Compile errors after editing: get_diagnostics, not dotnet build. " +
            "Renaming: rename (preview first). " +
            "Grep is only the right tool for string literals and comments.";
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
    // Shares RoslynWorkspace's walk, which prunes excluded directories and refuses to follow
    // reparse points. This guard runs before anything can report status, so a walk that never
    // returns here would hang the server with no way to say why.
    foreach (var pattern in new[] { "*.csproj", "*.sln", "*.slnx", "*.slnf" })
    {
        if (RoslynWorkspace.EnumerateFiles(rootPath, pattern).Any())
            return true;
    }
    return false;
}
