using System.ComponentModel;
using System.Runtime.CompilerServices;
using CsharpMcp.CodeAnalysis.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CsharpMcp.CodeAnalysis;

[McpServerToolType]
public class CsharpTools(RoslynWorkspace workspace, ILogger<CsharpTools> logger)
{
    [McpServerTool, Description("Jump from a symbol usage to its declaration. Use instead of grepping for 'class X' or 'void X('.")]
    public Task<string> get_definition(string filePath, int line, int column) =>
        Safe(
            () => NavigationTools.GetDefinitionAsync(workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.Format);

    [McpServerTool, Description(
        "Find all usages of a symbol across the solution. Use instead of grepping for a name: resolves overloads and " +
        "excludes matches in comments, strings and unrelated symbols with the same name. Each hit is tagged with what it " +
        "does with the symbol and the member it sits in. The filters narrow the result on the server, so prefer them over " +
        "fetching everything and reading each site.")]
    public Task<string> get_references(
        string filePath, int line, int column, int maxResults = 200,
        [Description("Keep only hits of this kind: read, write, invocation, instantiation, typeref, inheritance, typeof, nameof, attribute")] string? usage = null,
        [Description("Keep only hits inside a member whose Type.Member name matches (glob or substring), e.g. 'OrderService.Save'")] string? inEnclosingMember = null,
        [Description("Keep only hits whose enclosing member also calls or constructs something matching (glob or substring), e.g. 'SaveChanges'")] string? enclosingAlsoCalls = null,
        [Description("Keep only hits in files matching (glob or substring)")] string? filePattern = null,
        [Description("Keep only hits in projects matching (glob or substring)")] string? inProject = null,
        [Description("Drop hits in test files")] bool excludeTests = false,
        [Description("Drop hits in generated files (.g.cs, .designer.cs, obj/)")] bool excludeGenerated = false) =>
        Safe(
            () => NavigationTools.GetReferencesAsync(
                workspace.Solution, new Position(filePath, line, column), maxResults,
                new NavigationTools.ReferenceFilter(
                    usage, inEnclosingMember, enclosingAlsoCalls, filePattern, inProject, excludeTests, excludeGenerated)),
            TextFormatter.Format);

    [McpServerTool, Description("Find concrete implementations of an interface member or abstract method.")]
    public Task<string> get_implementations(string filePath, int line, int column) =>
        Safe(
            () => NavigationTools.GetImplementationsAsync(workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.FormatLocations);

    [McpServerTool, Description(
        "Trace callers and callees of a method. When maxDepth > 0, traces usage as an indented tree " +
        "following callers N levels deep with relevance scoring (same namespace/project preferred, " +
        "generated and test code deprioritized). maxPerNode limits callers shown per node.")]
    public Task<string> get_call_hierarchy(
        string filePath, int line, int column,
        [Description("Recursion depth for usage tracing (0 = callers/callees only, >0 = deep trace)")] int maxDepth = 0,
        [Description("Max callers per node when tracing (ranked by relevance)")] int maxPerNode = 5) =>
        maxDepth > 0
            ? Safe(
                () => NavigationTools.TraceUsagesAsync(workspace.Solution, new Position(filePath, line, column), maxDepth, maxPerNode),
                TextFormatter.Format)
            : Safe(
                () => NavigationTools.GetCallHierarchyAsync(workspace.Solution, new Position(filePath, line, column)),
                TextFormatter.Format);

    [McpServerTool, Description("Navigate base types, interfaces, and derived types.")]
    public Task<string> get_type_hierarchy(string filePath, int line, int column) =>
        Safe(
            () => NavigationTools.GetTypeHierarchyAsync(workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.Format);

    [McpServerTool, Description("Get type info, XML documentation, and parameter signatures at a position. Returns type details and, when at a call site, also includes method signature with parameter types and docs.")]
    public Task<string> get_hover(string filePath, int line, int column) =>
        Safe(async () =>
        {
            var pos = new Position(filePath, line, column);
            var hoverTask = TypeIntelligenceTools.GetHoverAsync(workspace.Solution, pos);
            var sigTask = TypeIntelligenceTools.GetSignatureAsync(workspace.Solution, pos);
            await Task.WhenAll(hoverTask, sigTask);
            return (hover: hoverTask.Result, signature: sigTask.Result);
        }, result =>
        {
            var parts = new List<string>();
            if (result.hover != null) parts.Add(TextFormatter.Format(result.hover));
            if (result.signature != null) parts.Add(TextFormatter.Format(result.signature));
            return parts.Count > 0 ? string.Join("\n\n", parts) : "No information at position.";
        });

    [McpServerTool, Description("Hierarchical outline of types and members in a file with line numbers. Call this before reading a C# file, then read only the members you need. Use flat=true for a flat symbol list instead.")]
    public Task<string> get_outline(string filePath, [Description("Return flat symbol list instead of hierarchy")] bool flat = false) =>
        flat
            ? Safe(
                () => CodeStructureTools.GetSymbolsAsync(workspace.Solution, filePath),
                TextFormatter.Format)
            : Safe(
                async () => CodeStructureTools.RenderOutline(await CodeStructureTools.GetOutlineAsync(workspace.Solution, filePath)),
                s => s);

    [McpServerTool, Description("List all using directives in a file.")]
    public Task<string> get_imports(string filePath) =>
        Safe(
            () => CodeStructureTools.GetImportsAsync(workspace.Solution, filePath),
            TextFormatter.Format);

    [McpServerTool, Description(
        "Search symbols by name pattern (glob: *, ? or substring). Use instead of grep or file globbing to locate a type or " +
        "member: returns declaration file and line. Optionally filter by kind (class, interface, enum, struct, delegate, " +
        "method, property, field, event, namespace) and project name (glob or substring). The filters narrow the result on " +
        "the server, so prefer them over fetching everything and reading each declaration. Use maxResults to limit output.")]
    public Task<string> find(
        [Description("Name pattern to match (glob with * and ?, or a substring)")] string query,
        string? kind = null, string? projectName = null, int maxResults = 200,
        [Description("Keep only symbols declared in files matching (glob or substring)")] string? filePattern = null,
        [Description("Keep only symbols in namespaces matching (glob or substring)")] string? namespacePattern = null,
        [Description("Keep only symbols with this accessibility: public, internal, private or protected")] string? accessibility = null,
        [Description("Keep only symbols carrying an attribute matching (glob or substring), e.g. 'ApiController'")] string? hasAttribute = null,
        [Description("Drop symbols declared in test files")] bool excludeTests = false,
        [Description("Drop symbols declared in generated files (.g.cs, .designer.cs, obj/)")] bool excludeGenerated = false) =>
        Safe(
            () => SemanticSearchTools.FindAsync(
                workspace.Solution, query, kind, projectName, maxResults,
                new SemanticSearchTools.FindFilter(
                    filePattern, namespacePattern, accessibility, hasAttribute, excludeTests, excludeGenerated)),
            TextFormatter.Format);

    [McpServerTool, Description("Get compile errors and warnings without running dotnet build. Provide filePath for a single file, or omit for workspace-wide diagnostics. Use projectName to filter by project (glob or substring). Default filters to Warning+Error; use minSeverity='info' or 'hidden' for more.")]
    public Task<string> get_diagnostics(
        [Description("File path for single-file diagnostics. Omit for workspace-wide.")] string? filePath = null,
        [Description("Filter by project name (glob or substring). Only used for workspace-wide.")] string? projectName = null,
        [Description("Minimum severity: 'error', 'warning', 'info', or 'hidden'. Only used for workspace-wide.")] string? minSeverity = null,
        int skip = 0,
        int take = 100) =>
        filePath != null
            ? Safe(
                () => DiagnosticsTools.GetDiagnosticsAsync(workspace.Solution, filePath),
                TextFormatter.Format)
            : Safe(
                () => DiagnosticsTools.GetAllDiagnosticsAsync(workspace.Solution, projectName, minSeverity, skip, take, workspace.GetCompilationAsync),
                TextFormatter.Format);

    [McpServerTool, Description("Rename a symbol across all projects. Set preview=true (default) to list every edit site (file:line:col) without writing. Set preview=false to execute the rename and write to disk, returning a summary of edits per file plus any file renames. line/column must point to the identifier (1-based). Use find or get_outline to locate symbols.")]
    public Task<string> rename(
        string filePath, int line, int column, string newName,
        [Description("Preview only (true) or execute rename (false)")] bool preview = true,
        [Description("Max edit sites listed in a preview before truncating")] int maxChangesShown = 100) =>
        preview
            ? Safe(
                () => RefactoringTools.RenamePreviewAsync(
                    workspace.Solution, new Position(filePath, line, column), newName),
                p => TextFormatter.Format(p, maxChangesShown))
            : Safe(
                () => RefactoringTools.RenameSymbolAsync(
                    workspace, new Position(filePath, line, column), newName),
                TextFormatter.FormatRenameResult);

    [McpServerTool, Description("Format a document using the Roslyn formatter.")]
    public Task<string> format_document(string filePath) =>
        Safe(
            () => RefactoringTools.FormatDocumentAsync(workspace.Solution, filePath),
            s => s);

    [McpServerTool, Description("Combined hover, diagnostics, and symbol list for a position in one call.")]
    public Task<string> analyze_position(
        string filePath, int line, int column) =>
        Safe(
            () => EfficiencyTools.AnalyzePositionAsync(
                workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.Format);

    [McpServerTool, Description("Analyze multiple positions in one call.")]
    public Task<string> batch_analyze(List<Position> positions) =>
        Safe(
            () => EfficiencyTools.BatchAnalyzeAsync(workspace.Solution, positions),
            TextFormatter.Format);

    [McpServerTool, Description("Get code completions at a position. Returns available members, methods, types, keywords, etc.")]
    public Task<string> get_completions(
        string filePath, int line, int column, int maxResults = 50) =>
        Safe(
            () => CompletionTools.GetCompletionsAsync(
                workspace.Solution, new Position(filePath, line, column), maxResults),
            TextFormatter.Format);

    [McpServerTool, Description("Get code actions (quick fixes) for diagnostics in a file. Includes fixes like add using, implement interface, generate constructor, etc. Optionally filter to a specific position. Use maxResults to limit output.")]
    public Task<string> get_code_actions(
        string filePath, int? line = null, int? column = null, int maxResults = 50) =>
        Safe(
            () => CodeActionTools.GetCodeActionsAsync(workspace.Solution, filePath, line, column, maxResults),
            TextFormatter.Format);

    private async Task<string> Safe<T>(Func<Task<T>> action, Func<T, string> format, [CallerMemberName] string tool = "")
    {
        logger.LogInformation("Tool {Tool} invoked", tool);
        if (!workspace.IsReady)
        {
            logger.LogInformation("Tool {Tool} rejected: {Status}", tool, workspace.LoadingStatus);
            return $"{workspace.LoadingStatus}. Please retry in a moment.";
        }
        try
        {
            var result = await action();
            logger.LogInformation("Tool {Tool} completed successfully", tool);
            return format(result!);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool {Tool} failed", tool);
            return $"Error: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
