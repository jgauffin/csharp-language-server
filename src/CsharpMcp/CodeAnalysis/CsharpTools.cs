using System.ComponentModel;
using System.Runtime.CompilerServices;
using ArchiMetrics.Analysis;
using CsharpMcp.CodeAnalysis.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CsharpMcp.CodeAnalysis;

[McpServerToolType]
public class CsharpTools(RoslynWorkspace workspace, CodeAnalysisAgent agent, ILogger<CsharpTools> logger)
{
    [McpServerTool, Description("Jump from a symbol usage to its declaration.")]
    public Task<string> get_definition(string filePath, int line, int column) =>
        Safe(
            () => NavigationTools.GetDefinitionAsync(workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.Format);

    [McpServerTool, Description("Find all usages of a symbol with read/write classification. Use maxResults to limit output.")]
    public Task<string> get_references(string filePath, int line, int column, int maxResults = 200) =>
        Safe(
            () => NavigationTools.GetReferencesAsync(workspace.Solution, new Position(filePath, line, column), maxResults),
            TextFormatter.Format);

    [McpServerTool, Description("Find concrete implementations of an interface member or abstract method.")]
    public Task<string> get_implementations(string filePath, int line, int column) =>
        Safe(
            () => NavigationTools.GetImplementationsAsync(workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.FormatLocations);

    [McpServerTool, Description("Trace callers and callees of a method.")]
    public Task<string> get_call_hierarchy(string filePath, int line, int column) =>
        Safe(
            () => NavigationTools.GetCallHierarchyAsync(workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.Format);

    [McpServerTool, Description(
        "Trace usages of any symbol (method, property setter, field, etc.) as an indented tree, " +
        "following callers N levels deep. Useful for understanding the blast radius of a change. " +
        "maxDepth controls recursion depth (default 3). maxPerNode limits callers shown per node " +
        "by relevance score: same namespace/project is preferred, generated and test code is deprioritized. " +
        "Truncated callers are reported as '+N omitted'.")]
    public Task<string> trace_usages(string filePath, int line, int column, int maxDepth = 3, int maxPerNode = 5) =>
        Safe(
            () => NavigationTools.TraceUsagesAsync(workspace.Solution, new Position(filePath, line, column), maxDepth, maxPerNode),
            TextFormatter.Format);

    [McpServerTool, Description("Navigate base types, interfaces, and derived types.")]
    public Task<string> get_type_hierarchy(string filePath, int line, int column) =>
        Safe(
            () => NavigationTools.GetTypeHierarchyAsync(workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.Format);

    [McpServerTool, Description("Get type info and XML documentation at a position.")]
    public Task<string> get_hover(string filePath, int line, int column) =>
        Safe(
            () => TypeIntelligenceTools.GetHoverAsync(workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.Format);

    [McpServerTool, Description("Get function parameter signatures at a call site.")]
    public Task<string> get_signature(string filePath, int line, int column) =>
        Safe(
            () => TypeIntelligenceTools.GetSignatureAsync(workspace.Solution, new Position(filePath, line, column)),
            TextFormatter.Format);

    [McpServerTool, Description("Flat list of all symbols declared in a file.")]
    public Task<string> get_symbols(string filePath) =>
        Safe(
            () => CodeStructureTools.GetSymbolsAsync(workspace.Solution, filePath),
            TextFormatter.Format);

    [McpServerTool, Description("Hierarchical outline of types and members in a file.")]
    public Task<string> get_outline(string filePath) =>
        Safe(
            async () => CodeStructureTools.RenderOutline(await CodeStructureTools.GetOutlineAsync(workspace.Solution, filePath)),
            s => s);

    [McpServerTool, Description("List all using directives in a file.")]
    public Task<string> get_imports(string filePath) =>
        Safe(
            () => CodeStructureTools.GetImportsAsync(workspace.Solution, filePath),
            TextFormatter.Format);

    [McpServerTool, Description("Search symbols by name pattern (glob: *, ? or substring). Optionally filter by kind (class, interface, enum, struct, delegate, method, property, field, event, namespace) and project name (glob or substring). Use maxResults to limit output.")]
    public Task<string> find(
        string namePattern, string? kind = null, string? projectName = null, int maxResults = 200) =>
        Safe(
            () => SemanticSearchTools.FindAsync(workspace.Solution, namePattern, kind, projectName, maxResults),
            TextFormatter.Format);

    [McpServerTool, Description("Fast symbol search across the workspace. query supports glob (*, ?) or substring match. projectName filters by project (glob or substring).")]
    public Task<string> get_workspace_symbols(
        string query, string? projectName = null, int maxResults = 200) =>
        Safe(
            () => SemanticSearchTools.GetWorkspaceSymbolsAsync(workspace.Solution, query, projectName, maxResults),
            TextFormatter.Format);

    [McpServerTool, Description("Get XML documentation for symbols matching a name pattern (glob: *, ? or substring). Optionally filter by kind (class, method, property, etc.) and project name. Returns summary, params, returns, remarks, examples, and exceptions.")]
    public Task<string> get_xmldoc(
        string namePattern, string? kind = null, string? projectName = null, int maxResults = 100) =>
        Safe(
            () => TypeIntelligenceTools.GetXmlDocAsync(workspace.Solution, namePattern, kind, projectName, maxResults),
            TextFormatter.Format);

    [McpServerTool, Description("Get errors and warnings for a specific file.")]
    public Task<string> get_diagnostics(string filePath) =>
        Safe(
            () => DiagnosticsTools.GetDiagnosticsAsync(workspace.Solution, filePath),
            TextFormatter.Format);

    [McpServerTool, Description("Get errors and warnings for all projects, or filter by project name (glob: *, ? or substring). Default filters to Warning+Error only. Use minSeverity='info' or 'hidden' to include more. Use skip/take to page.")]
    public Task<string> get_all_diagnostics(string? projectName = null, string? minSeverity = null, int skip = 0, int take = 100) =>
        Safe(
            () => DiagnosticsTools.GetAllDiagnosticsAsync(workspace.Solution, projectName, minSeverity, skip, take),
            TextFormatter.Format);

    [McpServerTool, Description("Preview the impact of a rename without writing to disk. line/column must point to the identifier to rename (1-based). Use get_symbols or find to get the exact line:column of the symbol.")]
    public Task<string> rename_preview(
        string filePath, int line, int column, string newName) =>
        Safe(
            () => RefactoringTools.RenamePreviewAsync(
                workspace.Solution, new Position(filePath, line, column), newName),
            TextFormatter.Format);

    [McpServerTool, Description("Rename a symbol across all projects and write changes to disk. Fails on compilation errors. line/column must point to the identifier to rename (1-based). Use get_symbols or find to get the exact line:column of the symbol.")]
    public Task<string> rename_symbol(
        string filePath, int line, int column, string newName) =>
        Safe(
            () => RefactoringTools.RenameSymbolAsync(
                workspace, new Position(filePath, line, column), newName),
            TextFormatter.Format);

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

    [McpServerTool, Description("List source files in the solution. Use search to filter by file path or project name — supports glob patterns (*, ?) or plain substring match. Returns relative paths.")]
    public Task<string> get_solution_files(
        string? search = null, int maxResults = 200) =>
        Task.FromResult(TextFormatter.Format(ProjectTools.GetSolutionFiles(workspace.Solution, workspace.RootPath, search, maxResults)));

    [McpServerTool, Description("Get code actions (quick fixes) for diagnostics in a file. Includes fixes like add using, implement interface, generate constructor, etc. Optionally filter to a specific position. Use maxResults to limit output.")]
    public Task<string> get_code_actions(
        string filePath, int? line = null, int? column = null, int maxResults = 50) =>
        Safe(
            () => CodeActionTools.GetCodeActionsAsync(workspace.Solution, filePath, line, column, maxResults),
            TextFormatter.Format);

    [McpServerTool, Description("Get the worst types by maintainability index across all projects or a specific one. Returns a flat list of types with metrics (LOC, CC, coupling, etc.). Use skip/take to page.")]
    public Task<string> calculate_metrics(string? projectName = null, int skip = 0, int take = 50) =>
        Safe(
            () => agent.GetWorstTypes(workspace.Solution, projectName: projectName, skip: skip, take: take),
            TextFormatter.Format);

    [McpServerTool, Description("Detect duplicated code (exact, renamed, and semantic clones) across all projects or a specific one. Results sorted by most instances. Use skip/take to page through results.")]
    public Task<string> detect_duplication(
        string? projectName = null, int minimumTokens = 50, double similarityThreshold = 0.85, int skip = 0, int take = 50) =>
        Safe(
            () => agent.DetectDuplication(workspace.Solution, projectName: projectName,
                minimumTokens: minimumTokens, similarityThreshold: similarityThreshold, skip: skip, take: take),
            TextFormatter.Format);

    [McpServerTool, Description("Find methods that are hard to understand and need documentation or refactoring. Uses embedding similarity, cyclomatic complexity, nesting depth, and magic literal counts. Results sorted by worst opacity. Use skip/take to page through results. Requires --model-dir.")]
    public Task<string> find_needs_docs_or_refactor(
        string? projectName = null, int minimumTokens = 20, int skip = 0, int take = 50) =>
        Safe(
            () => agent.FindNeedsDocsOrRefactor(workspace.Solution, projectName: projectName, minimumTokens: minimumTokens, skip: skip, take: take),
            TextFormatter.Format);

    [McpServerTool, Description("Get the worst namespaces by maintainability index. Returns flat namespace summaries without type/member trees. Use get_namespace_types to drill into a specific namespace. Use skip/take to page.")]
    public Task<string> get_worst_namespaces(string? projectName = null, int skip = 0, int take = 20) =>
        Safe(
            () => agent.GetWorstNamespaces(workspace.Solution, projectName: projectName, skip: skip, take: take),
            TextFormatter.Format);

    [McpServerTool, Description("Get types within a specific namespace, sorted by worst maintainability index. Use after get_worst_namespaces to drill into a flagged namespace. Use skip/take to page.")]
    public Task<string> get_namespace_types(string namespaceName, string? projectName = null, int skip = 0, int take = 20) =>
        Safe(
            () => agent.GetNamespaceTypes(workspace.Solution, namespaceName, projectName: projectName, skip: skip, take: take),
            TextFormatter.Format);

    [McpServerTool, Description("Generate a high-level summary of the workspace including project metrics, dependencies, and health indicators.")]
    public Task<string> generate_workspace_summary() =>
        Safe(
            () => agent.GenerateWorkspaceSummary(workspace.Solution),
            s => s);

    [McpServerTool, Description(
        "Find symbols most heavily accessed through layers of indirection " +
        "(A -> B -> C). Returns worst offenders ranked by score with full call chains. " +
        "Useful for identifying hidden coupling and deeply wrapped dependencies. " +
        "Use projectName (glob or substring) to scope to a single project.")]
    public Task<string> find_indirection_hotspots(
        [Description("Filter to a specific project (glob or substring). Omit for entire solution.")] string? projectName = null,
        [Description("Max call chain depth to trace (default 5).")] int maxDepth = 5,
        [Description("Minimum direct callers required to be a candidate (default 3). Lower = more results but slower.")] int minDirectCallers = 3,
        [Description("Max example chains to show per offender.")] int maxChainsPerOffender = 5,
        [Description("Include test files in the analysis.")] bool includeTests = false,
        int skip = 0,
        int take = 30) =>
        Safe(
            () => IndirectionTools.FindIndirectionHotspotsAsync(
                workspace.Solution, projectName, maxDepth, minDirectCallers,
                maxChainsPerOffender, includeTests, skip, take),
            TextFormatter.Format);

    private static readonly Dictionary<string, QualitySnapshot> _snapshots = new(StringComparer.Ordinal);
    private static int _snapshotCounter;

    [McpServerTool, Description("Capture a quality baseline snapshot (metrics + diagnostics). Use before making changes, then call quality_report with the returned label to see the impact.")]
    public Task<string> quality_snapshot() =>
        Safe(async () =>
        {
            var label = $"snap-{Interlocked.Increment(ref _snapshotCounter)}";
            var snapshot = await QualityTools.CaptureSnapshotAsync(workspace.Solution, agent);
            _snapshots[label] = snapshot;
            return (label, snapshot);
        }, t => TextFormatter.FormatSnapshot(t.label, t.snapshot));

    [McpServerTool, Description("Compare current code quality against a stored snapshot. Pass the label returned by quality_snapshot. Call quality_snapshot before making changes, then call this after.")]
    public Task<string> quality_report([Description("The label returned by quality_snapshot")] string label) =>
        Safe(async () =>
        {
            if (!_snapshots.TryGetValue(label, out var baseline))
                throw new ArgumentException($"Unknown snapshot label '{label}'. Call quality_snapshot first.");
            var current = await QualityTools.CaptureSnapshotAsync(workspace.Solution, agent);
            return QualityTools.CompareSnapshots(baseline, current);
        }, TextFormatter.Format);

    private async Task<string> Safe<T>(Func<Task<T>> action, Func<T, string> format, [CallerMemberName] string tool = "")
    {
        logger.LogInformation("Tool {Tool} invoked", tool);
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
