using System.ComponentModel;
using CsharpMcp.CodeAnalysis.Tools;
using ModelContextProtocol.Server;

namespace CsharpMcp.CodeAnalysis;

[McpServerToolType]
public class CsharpTools(RoslynWorkspace workspace)
{
    [McpServerTool, Description("Jump from a symbol usage to its declaration.")]
    public Task<Location?> get_definition(string filePath, int line, int column) =>
        NavigationTools.GetDefinitionAsync(workspace.Solution, new Position(filePath, line, column));

    [McpServerTool, Description("Find all usages of a symbol with read/write classification.")]
    public Task<List<NavigationTools.ReferenceResult>> get_references(string filePath, int line, int column) =>
        NavigationTools.GetReferencesAsync(workspace.Solution, new Position(filePath, line, column));

    [McpServerTool, Description("Find concrete implementations of an interface member or abstract method.")]
    public Task<List<Location>> get_implementations(string filePath, int line, int column) =>
        NavigationTools.GetImplementationsAsync(workspace.Solution, new Position(filePath, line, column));

    [McpServerTool, Description("Trace callers and callees of a method.")]
    public Task<List<NavigationTools.CallInfo>> get_call_hierarchy(string filePath, int line, int column) =>
        NavigationTools.GetCallHierarchyAsync(workspace.Solution, new Position(filePath, line, column));

    [McpServerTool, Description("Navigate base types, interfaces, and derived types.")]
    public Task<NavigationTools.TypeHierarchyResult?> get_type_hierarchy(string filePath, int line, int column) =>
        NavigationTools.GetTypeHierarchyAsync(workspace.Solution, new Position(filePath, line, column));

    [McpServerTool, Description("Get type info and XML documentation at a position.")]
    public Task<TypeIntelligenceTools.HoverResult?> get_hover(string filePath, int line, int column) =>
        TypeIntelligenceTools.GetHoverAsync(workspace.Solution, new Position(filePath, line, column));

    [McpServerTool, Description("Get function parameter signatures at a call site.")]
    public Task<TypeIntelligenceTools.ParameterHelp?> get_signature(string filePath, int line, int column) =>
        TypeIntelligenceTools.GetSignatureAsync(workspace.Solution, new Position(filePath, line, column));

    [McpServerTool, Description("Flat list of all symbols declared in a file.")]
    public Task<List<CodeStructureTools.SymbolEntry>> get_symbols(string filePath) =>
        CodeStructureTools.GetSymbolsAsync(workspace.Solution, filePath);

    [McpServerTool, Description("Hierarchical outline of types and members in a file.")]
    public async Task<string> get_outline(string filePath) =>
        CodeStructureTools.RenderOutline(await CodeStructureTools.GetOutlineAsync(workspace.Solution, filePath));

    [McpServerTool, Description("List all using directives in a file.")]
    public Task<List<CodeStructureTools.ImportEntry>> get_imports(string filePath) =>
        CodeStructureTools.GetImportsAsync(workspace.Solution, filePath);

    [McpServerTool, Description("Search symbols by name pattern. Optionally filter by kind and project.")]
    public Task<List<SemanticSearchTools.FindResult>> find(
        string namePattern, string? kind = null, string? projectName = null) =>
        SemanticSearchTools.FindAsync(workspace.Solution, namePattern, kind, projectName);

    [McpServerTool, Description("Fast fuzzy symbol search across the workspace or a specific project.")]
    public Task<List<SemanticSearchTools.FindResult>> get_workspace_symbols(
        string query, string? projectName = null) =>
        SemanticSearchTools.GetWorkspaceSymbolsAsync(workspace.Solution, query, projectName);

    [McpServerTool, Description("Get errors and warnings for a specific file.")]
    public Task<List<DiagnosticsTools.DiagnosticEntry>> get_diagnostics(string filePath) =>
        DiagnosticsTools.GetDiagnosticsAsync(workspace.Solution, filePath);

    [McpServerTool, Description("Get errors and warnings for all projects, or a specific one.")]
    public Task<List<DiagnosticsTools.DiagnosticEntry>> get_all_diagnostics(string? projectName = null) =>
        DiagnosticsTools.GetAllDiagnosticsAsync(workspace.Solution, projectName);

    [McpServerTool, Description("Preview the impact of a rename without writing to disk.")]
    public Task<RefactoringTools.RenamePreview> rename_preview(
        string filePath, int line, int column, string newName) =>
        RefactoringTools.RenamePreviewAsync(
            workspace.Solution, new Position(filePath, line, column), newName);

    [McpServerTool, Description("Rename a symbol across all projects and write changes to disk. Fails on compilation errors.")]
    public Task<RefactoringTools.RenamePreview> rename_symbol(
        string filePath, int line, int column, string newName) =>
        RefactoringTools.RenameSymbolAsync(
            workspace, new Position(filePath, line, column), newName);

    [McpServerTool, Description("Format a document using the Roslyn formatter.")]
    public Task<string> format_document(string filePath) =>
        RefactoringTools.FormatDocumentAsync(workspace.Solution, filePath);

    [McpServerTool, Description("Combined hover, diagnostics, and symbol list for a position in one call.")]
    public Task<EfficiencyTools.PositionAnalysis> analyze_position(
        string filePath, int line, int column) =>
        EfficiencyTools.AnalyzePositionAsync(
            workspace.Solution, new Position(filePath, line, column));

    [McpServerTool, Description("Analyze multiple positions in one call.")]
    public Task<List<EfficiencyTools.PositionAnalysis>> batch_analyze(List<Position> positions) =>
        EfficiencyTools.BatchAnalyzeAsync(workspace.Solution, positions);

    [McpServerTool, Description("Get code completions at a position. Returns available members, methods, types, keywords, etc.")]
    public Task<List<CompletionTools.CompletionItem>> get_completions(
        string filePath, int line, int column, int maxResults = 50) =>
        CompletionTools.GetCompletionsAsync(
            workspace.Solution, new Position(filePath, line, column), maxResults);

    [McpServerTool, Description("List all source files in the workspace, optionally filtered by project name and/or file name pattern (substring match).")]
    public List<ProjectTools.ProjectFileEntry> get_project_files(
        string? projectName = null, string? filePattern = null) =>
        ProjectTools.GetProjectFiles(workspace.Solution, projectName, filePattern);

    [McpServerTool, Description("Get code actions (quick fixes) for diagnostics in a file. Includes fixes like add using, implement interface, generate constructor, etc. Optionally filter to a specific position.")]
    public Task<List<CodeActionTools.CodeActionEntry>> get_code_actions(
        string filePath, int? line = null, int? column = null) =>
        CodeActionTools.GetCodeActionsAsync(workspace.Solution, filePath, line, column);
}
