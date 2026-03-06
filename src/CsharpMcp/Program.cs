using CsharpMcp;
using CsharpMcp.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;

var config = ServerConfig.Parse(args);
var workspace = await RoslynWorkspace.LoadAsync(config.RootPath);

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddSingleton(workspace)
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
