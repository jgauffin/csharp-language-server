using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class RefactoringTools
{
    /// <summary>A single identifier swap at one source location.</summary>
    public record RenameChange(string FilePath, int Line, int Column, string OldText, string NewText);

    /// <summary>A source file that moved because the type it declares was renamed.</summary>
    public record RenamedFile(string OldFilePath, string NewFilePath);

    /// <summary>Number of identifier swaps applied to a single file.</summary>
    public record FileEditCount(string FilePath, int EditCount);

    public record RenamePreview(
        string NewName,
        List<RenameChange> Changes,
        List<string> AffectedFiles,
        List<RenamedFile> RenamedFiles,
        List<FileEditCount> EditsPerFile);

    /// <summary>
    /// Previews a rename without writing to disk. Fails if there are compilation errors.
    /// </summary>
    public static async Task<RenamePreview> RenamePreviewAsync(
        Solution solution,
        Position pos,
        string newName)
    {
        var (symbol, renamedSolution) = await ComputeRenameAsync(solution, pos, newName);
        var changes = await CollectChangesAsync(solution, renamedSolution, symbol.Name, newName);
        return new RenamePreview(
            newName,
            changes,
            changes.Select(c => c.FilePath).Distinct().ToList(),
            CollectRenamedFiles(solution, renamedSolution),
            CountEditsPerFile(changes));
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

        var (symbol, renamedSolution) = await ComputeRenameAsync(solution, pos, newName);
        var changes = await CollectChangesAsync(solution, renamedSolution, symbol.Name, newName);
        var renamedFiles = CollectRenamedFiles(solution, renamedSolution);

        // Persist to disk ourselves, then sync the in-memory snapshot. We do NOT let
        // MSBuildWorkspace.TryApplyChanges write files: when a type rename also renames
        // its document, ApplyChanges adds a new document whose path resolves against the
        // project root (losing the original subfolder), producing a duplicate copy in
        // the project root. Driving disk writes from the document FilePaths avoids that.
        await PersistRenameToDiskAsync(solution, renamedSolution);

        // Update the in-memory solution to match what we just wrote, without going
        // through MSBuildWorkspace's disk-mutating ApplyChanges.
        workspace.SyncRenamedSolution(renamedSolution);

        return new RenamePreview(
            newName,
            changes,
            changes.Select(c => c.FilePath).Distinct().ToList(),
            renamedFiles,
            CountEditsPerFile(changes));
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

    /// <summary>
    /// Collects the individual identifier swaps a rename produces, so callers report
    /// edit sites rather than file contents.
    ///
    /// Roslyn's change ranges are only required to be conservative: for a re-serialized
    /// document they routinely collapse into a single span covering the whole file, which
    /// would make every "change" carry the entire before/after source and report a
    /// meaningless 1:1 position. We therefore ask for fine-grained text changes and keep
    /// only spans that are genuinely a rename of <paramref name="oldName"/> to
    /// <paramref name="newName"/>. Anything wider is a collapsed range, not a real edit,
    /// and is counted per file instead of being emitted as text.
    /// </summary>
    private static async Task<List<RenameChange>> CollectChangesAsync(
        Solution original, Solution renamed, string oldName, string newName)
    {
        var changes = new List<RenameChange>();

        foreach (var docId in renamed.GetChanges(original).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
        {
            var originalDoc = original.GetDocument(docId);
            var renamedDoc = renamed.GetDocument(docId);
            if (originalDoc is null || renamedDoc is null) continue;

            var originalText = await originalDoc.GetTextAsync();
            var filePath = renamedDoc.FilePath ?? "";

            foreach (var change in await renamedDoc.GetTextChangesAsync(originalDoc))
            {
                var replaced = originalText.GetSubText(change.Span).ToString();
                var replacement = change.NewText ?? "";

                // Keep only true identifier swaps; a wider span means the range collapsed
                // into a whole-file diff and must not be reported as source text.
                if (!IsIdentifierSwap(replaced, replacement, oldName, newName)) continue;

                // Report the full identifier and its start, not the minimal diff: Roslyn
                // trims the shared affix (Calculator -> MathHelper reports "Calculato"),
                // which would be misleading to read and impossible to navigate to.
                var identifierStart = change.Span.Start - CommonPrefixLength(oldName, newName);
                var linePos = originalText.Lines.GetLinePosition(identifierStart);

                changes.Add(new RenameChange(
                    filePath,
                    linePos.Line + 1,
                    linePos.Character + 1,
                    oldName,
                    newName
                ));
            }
        }

        return changes;
    }

    /// <summary>
    /// Decides whether a text change is the rename itself rather than a collapsed range.
    /// Roslyn emits a minimal diff, trimming any prefix and suffix the two names share
    /// (Calculator -> MathHelper arrives as "Calculato" -> "MathHelpe"), so we verify the
    /// change is exactly what remains of the two names after that trimming.
    /// </summary>
    private static bool IsIdentifierSwap(string replaced, string replacement, string oldName, string newName)
    {
        var prefix = CommonPrefixLength(oldName, newName);
        var suffix = CommonSuffixLength(oldName, newName, prefix);

        return replaced == oldName[prefix..(oldName.Length - suffix)]
            && replacement == newName[prefix..(newName.Length - suffix)];
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>
    /// Length of the shared trailing text, without overlapping the shared prefix.
    /// </summary>
    private static int CommonSuffixLength(string a, string b, int prefixLength)
    {
        var max = Math.Min(a.Length, b.Length) - prefixLength;
        var i = 0;
        while (i < max && a[a.Length - 1 - i] == b[b.Length - 1 - i]) i++;
        return i;
    }

    /// <summary>
    /// Identifies files that moved because a renamed type's document was renamed to match.
    /// Roslyn surfaces this as a removed document plus an added document holding the same
    /// type, so we pair them by the renamed solution's document identity.
    /// </summary>
    private static List<RenamedFile> CollectRenamedFiles(Solution original, Solution renamed)
    {
        var renamedFiles = new List<RenamedFile>();

        foreach (var projectChange in renamed.GetChanges(original).GetProjectChanges())
        {
            var removed = projectChange.GetRemovedDocuments()
                .Select(id => original.GetDocument(id)?.FilePath)
                .Where(p => p is not null)
                .ToList();

            var added = projectChange.GetAddedDocuments()
                .Select(id => renamed.GetDocument(id)?.FilePath)
                .Where(p => p is not null)
                .ToList();

            // A rename is a one-for-one move within the same folder; pair them in order.
            for (var i = 0; i < removed.Count && i < added.Count; i++)
                renamedFiles.Add(new RenamedFile(removed[i]!, added[i]!));
        }

        return renamedFiles;
    }

    private static List<FileEditCount> CountEditsPerFile(List<RenameChange> changes) =>
        changes
            .GroupBy(c => c.FilePath)
            .Select(g => new FileEditCount(g.Key, g.Count()))
            .OrderByDescending(f => f.EditCount)
            .ThenBy(f => f.FilePath)
            .ToList();

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
