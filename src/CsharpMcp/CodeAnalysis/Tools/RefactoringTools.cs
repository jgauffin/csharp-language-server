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

        // Persist to disk ourselves, then sync the in-memory snapshot. We do NOT let
        // MSBuildWorkspace.TryApplyChanges write files: when a type rename also renames
        // its document, ApplyChanges adds a new document whose path resolves against the
        // project root (losing the original subfolder), producing a duplicate copy in
        // the project root. Driving disk writes from the document FilePaths avoids that.
        await PersistRenameToDiskAsync(solution, renamedSolution);

        // Update the in-memory solution to match what we just wrote, without going
        // through MSBuildWorkspace's disk-mutating ApplyChanges.
        workspace.SyncRenamedSolution(renamedSolution);

        return new RenamePreview(newName, changes, changes.Select(c => c.FilePath).Distinct().ToList());
    }

    /// <summary>
    /// Writes the renamed solution to disk by reconciling documents against the original:
    /// changed documents are overwritten, renamed-file documents move (delete old, write new),
    /// and any added/removed documents are mirrored on disk. Each file is written exactly once.
    /// </summary>
    private static async Task PersistRenameToDiskAsync(Solution original, Solution renamed)
    {
        foreach (var projectChange in renamed.GetChanges(original).GetProjectChanges())
        {
            // A file rename surfaces as a removed document (old path) + added document (new path),
            // both pointing at real FilePaths. Delete the old file so we don't leave a stale copy.
            foreach (var docId in projectChange.GetRemovedDocuments())
            {
                var oldDoc = original.GetDocument(docId);
                if (oldDoc?.FilePath is { } oldPath && File.Exists(oldPath))
                    File.Delete(oldPath);
            }

            foreach (var docId in projectChange.GetAddedDocuments())
            {
                var newDoc = renamed.GetDocument(docId);
                if (newDoc?.FilePath is null) continue;

                var dir = Path.GetDirectoryName(newDoc.FilePath);
                if (dir is not null) Directory.CreateDirectory(dir);

                var text = await newDoc.GetTextAsync();
                await File.WriteAllTextAsync(newDoc.FilePath, text.ToString());
            }

            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var newDoc = renamed.GetDocument(docId);
                if (newDoc?.FilePath is null) continue;

                var text = await newDoc.GetTextAsync();
                await File.WriteAllTextAsync(newDoc.FilePath, text.ToString());
            }
        }
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

        // RenameFile renames the type's document to match the new name, but only when the
        // symbol is a type whose declaration file already shares its name (e.g. Calculator.cs
        // for type Calculator). Files housing multiple types are left untouched by Roslyn.
        var renamedSolution = await Renamer.RenameSymbolAsync(
            solution,
            symbol,
            new SymbolRenameOptions { RenameFile = true },
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
