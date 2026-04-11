using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class RefactoringTools
{
    public record RenameChange(string FilePath, int Line, int Column, string OldText, string NewText);

    public record RenamePreview(string NewName, List<RenameChange> Changes, List<string> AffectedFiles);

    /// <summary>
    /// Previews a rename without writing to disk. Fails if there are compilation errors.
    /// </summary>
    public static async Task<RenamePreview> RenamePreviewAsync(
        Solution solution,
        Position pos,
        string newName)
    {
        var (symbol, renamedSolution) = await ComputeRenameAsync(solution, pos, newName);
        var changes = await CollectChangesAsync(solution, renamedSolution, newName);
        return new RenamePreview(newName, changes, changes.Select(c => c.FilePath).Distinct().ToList());
    }

    /// <summary>
    /// Executes a rename across the entire solution and writes all changed files to disk.
    /// Fails hard if there are any compilation errors before renaming.
    /// </summary>
    public static async Task<RenamePreview> RenameSymbolAsync(
        RoslynWorkspace workspace,
        Position pos,
        string newName)
    {
        var solution = workspace.Solution;

        // Only check the project containing the target symbol for errors,
        // not every project in the solution.
        var targetDoc = PositionHelper.ResolveDocument(solution, pos.FilePath);
        await AssertNoCompilationErrorsAsync(targetDoc.Project, workspace.GetCompilationAsync);

        var (_, renamedSolution) = await ComputeRenameAsync(solution, pos, newName);
        var changes = await CollectChangesAsync(solution, renamedSolution, newName);

        // Apply changes and write to disk
        if (!workspace.TryApplyChanges(renamedSolution))
            throw new InvalidOperationException("Failed to apply rename changes to workspace.");

        // Write each changed document to disk
        foreach (var docId in renamedSolution.GetChanges(solution).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
        {
            var doc = renamedSolution.GetDocument(docId);
            if (doc?.FilePath is null) continue;

            var text = await doc.GetTextAsync();
            await File.WriteAllTextAsync(doc.FilePath, text.ToString());
        }

        return new RenamePreview(newName, changes, changes.Select(c => c.FilePath).Distinct().ToList());
    }

    public static async Task<string> FormatDocumentAsync(Solution solution, string filePath)
    {
        var doc = PositionHelper.ResolveDocument(solution, filePath);
        var formatted = await Formatter.FormatAsync(doc);
        var text = await formatted.GetTextAsync();
        return text.ToString();
    }

    private static async Task<(ISymbol symbol, Solution renamedSolution)> ComputeRenameAsync(
        Solution solution, Position pos, string newName)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, offset)
            ?? throw new ArgumentException($"No symbol found at {pos.FilePath}:{pos.Line}:{pos.Column}");

        if (!symbol.Locations.Any(l => l.IsInSource))
            throw new ArgumentException(
                $"Symbol '{symbol.ToDisplayString()}' is not a user-defined source symbol and cannot be renamed. " +
                $"Ensure the position points to the identifier name, not a keyword or type reference.");

        var renamedSolution = await Renamer.RenameSymbolAsync(
            solution,
            symbol,
            new SymbolRenameOptions(),
            newName
        );

        return (symbol, renamedSolution);
    }

    private static async Task<List<RenameChange>> CollectChangesAsync(
        Solution original, Solution renamed, string newName)
    {
        var changes = new List<RenameChange>();

        foreach (var docId in renamed.GetChanges(original).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
        {
            var originalDoc = original.GetDocument(docId);
            var renamedDoc = renamed.GetDocument(docId);
            if (originalDoc is null || renamedDoc is null) continue;

            var originalText = await originalDoc.GetTextAsync();
            var renamedText = await renamedDoc.GetTextAsync();

            var textChanges = renamedText.GetChangeRanges(originalText);
            var filePath = renamedDoc.FilePath ?? "";

            foreach (var change in textChanges)
            {
                var linePos = originalText.Lines.GetLinePosition(change.Span.Start);
                var oldText = originalText.GetSubText(change.Span).ToString();
                var newText = renamedText.GetSubText(
                    new Microsoft.CodeAnalysis.Text.TextSpan(change.Span.Start, change.NewLength)
                ).ToString();

                changes.Add(new RenameChange(
                    filePath,
                    linePos.Line + 1,
                    linePos.Character + 1,
                    oldText,
                    newText
                ));
            }
        }

        return changes;
    }

    private static async Task AssertNoCompilationErrorsAsync(Project project, Func<Project, Task<Compilation?>> getCompilation)
    {
        var compilation = await getCompilation(project);
        if (compilation is null) return;

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            var messages = string.Join("\n", errors.Select(e =>
                $"[{e.Id}] {e.GetMessage()} @ {e.Location.GetLineSpan().Path}:{e.Location.GetLineSpan().StartLinePosition.Line + 1}"));

            throw new InvalidOperationException(
                $"Rename aborted: {project.Name} has compilation errors:\n{messages}");
        }
    }
}
