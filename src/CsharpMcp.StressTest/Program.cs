using System.Diagnostics;
using ArchiMetrics.Analysis;
using CsharpMcp.CodeAnalysis;
using CsharpMcp.CodeAnalysis.Tools;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

MSBuildLocator.RegisterDefaults();

var solutionDir = args.Length > 0
    ? args[0]
    : @"D:\src\coderr\Kiwisonic\DesktopClient\src";

Console.WriteLine($"=== CsharpMcp Stress Test ===");
Console.WriteLine($"Target: {solutionDir}");
Console.WriteLine();

var totalSw = Stopwatch.StartNew();
int passed = 0;
int failed = 0;
var failures = new List<(string name, Exception ex)>();

async Task<T?> RunStep<T>(string name, Func<Task<T>> action)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var result = await action();
        sw.Stop();
        var resultDesc = result switch
        {
            null => "null",
            string s => $"{s.Length} chars",
            System.Collections.ICollection c => $"{c.Count} items",
            _ => result.ToString()?[..Math.Min(result.ToString()!.Length, 80)] ?? ""
        };
        Console.WriteLine($"  OK  {sw.ElapsedMilliseconds,6}ms  {name}  => {resultDesc}");
        passed++;
        return result;
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"  FAIL {sw.ElapsedMilliseconds,5}ms  {name}  => {ex.GetType().Name}: {ex.Message}");
        failed++;
        failures.Add((name, ex));
        return default;
    }
}

void Expect(bool condition, string message)
{
    if (!condition) throw new Exception($"Assertion failed: {message}");
}

List<ProjectTools.ProjectFileEntry> PickSpread(List<ProjectTools.ProjectFileEntry> files, int count)
{
    if (files.Count <= count) return files;
    var result = new List<ProjectTools.ProjectFileEntry> { files[0], files[^1] };
    var step = files.Count / (count - 1);
    for (int i = step; result.Count < count && i < files.Count - 1; i += step)
        result.Add(files[i]);
    return result.Distinct().ToList();
}

// ── Load workspace ──────────────────────────────────────────────────────
var workspace = await RunStep("Load workspace", () => RoslynWorkspace.LoadAsync(solutionDir));
if (workspace is null)
{
    Console.WriteLine("FATAL: Could not load workspace.");
    return 1;
}
var solution = workspace.Solution;
var agent = new CodeAnalysisAgent(workspace.InnerWorkspace, solutionDir);
var tools = new CsharpTools(workspace, agent, NullLogger<CsharpTools>.Instance);

// ── Discover files and pick targets ─────────────────────────────────────
var allFiles = ProjectTools.GetSolutionFiles(solution, solutionDir);
Console.WriteLine($"  Found {allFiles.Count} files across {solution.Projects.Count()} projects");

var csFiles = allFiles.Where(f => f.FilePath.EndsWith(".cs")).ToList();
if (csFiles.Count == 0)
{
    Console.WriteLine("ERROR: No .cs files found in workspace.");
    return 1;
}

var targetFiles = PickSpread(csFiles, 5);
Console.WriteLine($"  Target files for position-based tests:");
foreach (var f in targetFiles)
    Console.WriteLine($"    {f.ProjectName}/{Path.GetFileName(f.FilePath)}");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════════════════
// ProjectTools.GetSolutionFiles
// ═══════════════════════════════════════════════════════════════════════
await RunStep("GetSolutionFiles (all)", () =>
{
    var files = ProjectTools.GetSolutionFiles(solution, solutionDir);
    Expect(files.Count > 0, $"Expected files, got {files.Count}");
    return Task.FromResult(files);
});

var firstProject = solution.Projects.First().Name;
await RunStep($"GetSolutionFiles (search={firstProject})", () =>
{
    var files = ProjectTools.GetSolutionFiles(solution, solutionDir, search: firstProject);
    Expect(files.Count > 0, $"Expected files matching {firstProject}");
    return Task.FromResult(files);
});

await RunStep("GetSolutionFiles (search=*.cs)", () =>
{
    var files = ProjectTools.GetSolutionFiles(solution, solutionDir, search: "*.cs");
    Expect(files.Count > 0, "Expected .cs files");
    return Task.FromResult(files);
});

// ═══════════════════════════════════════════════════════════════════════
// SemanticSearchTools
// ═══════════════════════════════════════════════════════════════════════
await RunStep("Find (kind=Class)", async () =>
{
    var results = await SemanticSearchTools.FindAsync(solution, "", kind: "Class", maxResults: 50);
    Expect(results.Count > 0, "Expected classes");
    return results;
});

await RunStep("Find (kind=Method)", async () =>
{
    var results = await SemanticSearchTools.FindAsync(solution, "", kind: "Method", maxResults: 50);
    Expect(results.Count > 0, "Expected methods");
    return results;
});

await RunStep("Find (kind=Interface)", async () =>
{
    var results = await SemanticSearchTools.FindAsync(solution, "", kind: "Interface", maxResults: 50);
    return results;
});

await RunStep($"Find (project={firstProject})", async () =>
{
    var results = await SemanticSearchTools.FindAsync(solution, "", projectName: firstProject, maxResults: 50);
    Expect(results.Count > 0, "Expected results");
    return results;
});

await RunStep("GetWorkspaceSymbols (query=a)", async () =>
{
    var results = await SemanticSearchTools.GetWorkspaceSymbolsAsync(solution, "a", maxResults: 50);
    Expect(results.Count > 0, "Expected workspace symbols");
    return results;
});

await RunStep("GetWorkspaceSymbols (query=Get)", async () =>
{
    var results = await SemanticSearchTools.GetWorkspaceSymbolsAsync(solution, "Get", maxResults: 50);
    return results;
});

// ═══════════════════════════════════════════════════════════════════════
// Per-file tools: CodeStructure, Diagnostics, CodeActions, Format
// ═══════════════════════════════════════════════════════════════════════
foreach (var file in targetFiles)
{
    var shortName = $"{file.ProjectName}/{Path.GetFileName(file.FilePath)}";

    await RunStep($"GetSymbols ({shortName})", async () =>
        await CodeStructureTools.GetSymbolsAsync(solution, file.FilePath));

    await RunStep($"GetOutline ({shortName})", async () =>
        await CodeStructureTools.GetOutlineAsync(solution, file.FilePath));

    await RunStep($"GetImports ({shortName})", async () =>
        await CodeStructureTools.GetImportsAsync(solution, file.FilePath));

    await RunStep($"GetDiagnostics ({shortName})", async () =>
        await DiagnosticsTools.GetDiagnosticsAsync(solution, file.FilePath));

    await RunStep($"GetCodeActions ({shortName})", async () =>
        await CodeActionTools.GetCodeActionsAsync(solution, file.FilePath, maxResults: 10));

    await RunStep($"FormatDocument ({shortName})", async () =>
    {
        var formatted = await RefactoringTools.FormatDocumentAsync(solution, file.FilePath);
        Expect(!string.IsNullOrWhiteSpace(formatted), "Format returned empty");
        return formatted;
    });
}

// ═══════════════════════════════════════════════════════════════════════
// DiagnosticsTools (solution-wide)
// ═══════════════════════════════════════════════════════════════════════
await RunStep("GetAllDiagnostics (whole solution, take=50)", async () =>
    await DiagnosticsTools.GetAllDiagnosticsAsync(solution, take: 50));

await RunStep($"GetAllDiagnostics (project={firstProject})", async () =>
    await DiagnosticsTools.GetAllDiagnosticsAsync(solution, projectName: firstProject, take: 50));

await RunStep("GetAllDiagnostics (minSeverity=Error)", async () =>
    await DiagnosticsTools.GetAllDiagnosticsAsync(solution, minSeverity: "Error", take: 50));

await RunStep("GetAllDiagnostics (skip=10, take=20)", async () =>
    await DiagnosticsTools.GetAllDiagnosticsAsync(solution, skip: 10, take: 20));

// ═══════════════════════════════════════════════════════════════════════
// Position-based tools: Navigation, TypeIntelligence, Completion, Efficiency
// ═══════════════════════════════════════════════════════════════════════

// Discover real symbol positions from the target files
var symbolPositions = new List<(Position pos, string description)>();

foreach (var file in targetFiles)
{
    try
    {
        var symbols = await CodeStructureTools.GetSymbolsAsync(solution, file.FilePath);
        var lines = await File.ReadAllLinesAsync(file.FilePath);
        foreach (var sym in symbols.Take(3))
        {
            // Find the actual column of the symbol name in the line
            int col = 1;
            if (sym.Line >= 1 && sym.Line <= lines.Length)
            {
                var lineText = lines[sym.Line - 1];
                var idx = lineText.IndexOf(sym.Name, StringComparison.Ordinal);
                if (idx >= 0) col = idx + 1;
            }
            symbolPositions.Add((
                new Position(file.FilePath, sym.Line, col),
                $"{sym.Kind} {sym.Name} in {Path.GetFileName(file.FilePath)}"
            ));
        }
    }
    catch
    {
        // Skip files that fail symbol extraction
    }
}

Console.WriteLine($"\n  Found {symbolPositions.Count} symbol positions for navigation tests\n");

foreach (var (pos, desc) in symbolPositions.Take(10))
{
    // NavigationTools
    await RunStep($"GetDefinition ({desc})", async () =>
        await NavigationTools.GetDefinitionAsync(solution, pos));

    await RunStep($"GetReferences ({desc})", async () =>
        await NavigationTools.GetReferencesAsync(solution, pos, maxResults: 20));

    await RunStep($"GetImplementations ({desc})", async () =>
        await NavigationTools.GetImplementationsAsync(solution, pos));

    await RunStep($"GetCallHierarchy ({desc})", async () =>
        await NavigationTools.GetCallHierarchyAsync(solution, pos));

    await RunStep($"GetTypeHierarchy ({desc})", async () =>
        await NavigationTools.GetTypeHierarchyAsync(solution, pos));

    // TypeIntelligenceTools
    await RunStep($"GetHover ({desc})", async () =>
        await TypeIntelligenceTools.GetHoverAsync(solution, pos));

    await RunStep($"GetSignature ({desc})", async () =>
        await TypeIntelligenceTools.GetSignatureAsync(solution, pos));

    // CompletionTools
    await RunStep($"GetCompletions ({desc})", async () =>
        await CompletionTools.GetCompletionsAsync(solution, pos, maxResults: 10));

    // EfficiencyTools
    await RunStep($"AnalyzePosition ({desc})", async () =>
        await EfficiencyTools.AnalyzePositionAsync(solution, pos));
}

// ═══════════════════════════════════════════════════════════════════════
// BatchAnalyze
// ═══════════════════════════════════════════════════════════════════════
if (symbolPositions.Count >= 2)
{
    var batchPositions = symbolPositions.Take(5).Select(x => x.pos).ToList();
    await RunStep($"BatchAnalyze ({batchPositions.Count} positions)", async () =>
        await EfficiencyTools.BatchAnalyzeAsync(solution, batchPositions));
}

// ═══════════════════════════════════════════════════════════════════════
// Known failure reproductions
// ═══════════════════════════════════════════════════════════════════════
var availableInstrument = allFiles.FirstOrDefault(f => f.FilePath.EndsWith("AvailableInstrument.cs"));
if (availableInstrument is not null)
{
    var pos = new Position(availableInstrument.FilePath, 32, 43);
    await RunStep("REPRO: GetReferences on AvailableInstrument.cs:32:43 (FamilyFromProgram)", async () =>
        await NavigationTools.GetReferencesAsync(solution, pos, maxResults: 20));

    // Also test via MCP wrapper (the actual failure path)
    await RunStep("REPRO-MCP: get_references on AvailableInstrument.cs:32:43", async () =>
        await tools.get_references(availableInstrument.FilePath, 32, 43));
}

var trackFile = allFiles.FirstOrDefault(f => f.FilePath.EndsWith("Track.cs"));
if (trackFile is not null)
{
    await RunStep("REPRO: GetOutline on Track.cs", async () =>
        await CodeStructureTools.GetOutlineAsync(solution, trackFile.FilePath));

    // Also test via MCP wrapper
    await RunStep("REPRO-MCP: get_outline on Track.cs", async () =>
        await tools.get_outline(trackFile.FilePath));
}

// Run all files through get_outline via MCP wrapper to find the crasher
Console.WriteLine("\n  Running get_outline on ALL files via MCP wrapper...\n");
foreach (var file in allFiles)
{
    var name = $"{file.ProjectName}/{Path.GetFileName(file.FilePath)}";
    await RunStep($"MCP get_outline ({name})", async () =>
        await tools.get_outline(file.FilePath));
}

// Run get_references on all symbols we found, via MCP wrapper
Console.WriteLine("\n  Running get_references on symbol positions via MCP wrapper...\n");
foreach (var (pos, desc) in symbolPositions)
{
    await RunStep($"MCP get_references ({desc})", async () =>
        await tools.get_references(pos.FilePath, pos.Line, pos.Column));
}

// ═══════════════════════════════════════════════════════════════════════
// RenamePreview (read-only — won't write to disk)
// ═══════════════════════════════════════════════════════════════════════
// Pick a method or type symbol (not namespace) for rename preview
var renameCandidate = symbolPositions
    .FirstOrDefault(x => x.description.StartsWith("Method ") || x.description.StartsWith("NamedType "));
if (renameCandidate.pos is not null)
{
    await RunStep($"RenamePreview ({renameCandidate.description})", async () =>
        await RefactoringTools.RenamePreviewAsync(solution, renameCandidate.pos, "StressTestRename"));
}

// ═══════════════════════════════════════════════════════════════════════
// Summary
// ═══════════════════════════════════════════════════════════════════════
totalSw.Stop();
Console.WriteLine();
Console.WriteLine(new string('═', 60));
Console.WriteLine($"  Total steps: {passed + failed}");
Console.WriteLine($"  Passed:      {passed}");
Console.WriteLine($"  Failed:      {failed}");
Console.WriteLine($"  Total time:  {totalSw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine(new string('═', 60));

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  FAILURES:");
    foreach (var (name, ex) in failures)
    {
        Console.WriteLine($"    X {name}");
        Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException is not null)
            Console.WriteLine($"      Inner: {ex.InnerException.Message}");
    }
}

workspace.Dispose();
return failed > 0 ? 1 : 0;
