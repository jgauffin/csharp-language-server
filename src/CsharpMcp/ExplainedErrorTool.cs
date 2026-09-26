using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CsharpMcp;

/// <summary>
/// Wraps a tool so a call the server cannot carry out comes back as text the calling agent can act on.
/// Without this the SDK answers "An error occurred invoking 'x'." and the agent has nothing to correct.
/// </summary>
public sealed class ExplainedErrorTool(McpServerTool inner) : DelegatingMcpServerTool(inner)
{
    /// <summary>
    /// Builds the tool set for a tool type, resolving the instance from the request's service provider.
    /// </summary>
    public static IEnumerable<McpServerTool> For<TToolType>() where TToolType : notnull =>
        typeof(TToolType)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(method => (McpServerTool)new ExplainedErrorTool(
                McpServerTool.Create(
                    method,
                    request => request.Services!.GetRequiredService<TToolType>(),
                    new McpServerToolCreateOptions())));

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var mismatch = ArgumentMismatch.Describe(ProtocolTool.InputSchema, request.Params?.Arguments?.Keys);
        if (mismatch is not null)
            return Failure($"{ProtocolTool.Name}: {mismatch}");

        try
        {
            return await base.InvokeAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure($"{ProtocolTool.Name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static CallToolResult Failure(string text) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = text }] };
}
