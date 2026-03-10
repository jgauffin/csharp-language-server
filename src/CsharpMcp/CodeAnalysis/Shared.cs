using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CsharpMcp.CodeAnalysis;

public record Position(string FilePath, int Line, int Column);

public record Location(string FilePath, int Line, int Column, string? Preview = null);

public record SymbolInfo(string Name, string Kind, string ContainingType, string? Documentation);

public static class PositionHelper
{
    /// <summary>
    /// Normalizes a file path for Roslyn lookup (full path, canonical form).
    /// </summary>
    public static string NormalizePath(string filePath) => Path.GetFullPath(filePath);

    /// <summary>
    /// Resolves a document by file path, with path normalization.
    /// Throws ArgumentException with helpful message if not found.
    /// </summary>
    public static Document ResolveDocument(Solution solution, string filePath)
    {
        var normalized = NormalizePath(filePath);
        var docIds = solution.GetDocumentIdsWithFilePath(normalized);

        // Fall back to scanning all documents with case-insensitive comparison
        // to handle path normalization mismatches.
        if (docIds.IsEmpty)
            docIds = FindDocumentIdsCaseInsensitive(solution, normalized);

        return docIds
            .Select(id => solution.GetDocument(id))
            .FirstOrDefault(d => d is not null)
            ?? throw new ArgumentException(
                $"File not found in workspace: {filePath}. " +
                $"Loaded projects: {string.Join(", ", solution.Projects.Select(p => p.Name))}");
    }

    /// <summary>
    /// Resolves a document ID by file path, with path normalization.
    /// </summary>
    public static DocumentId ResolveDocumentId(Solution solution, string filePath)
    {
        var normalized = NormalizePath(filePath);
        var docId = solution.GetDocumentIdsWithFilePath(normalized).FirstOrDefault()
            ?? FindDocumentIdsCaseInsensitive(solution, normalized).FirstOrDefault();
        return docId
            ?? throw new ArgumentException(
                $"File not found in workspace: {filePath}. " +
                $"Loaded projects: {string.Join(", ", solution.Projects.Select(p => p.Name))}");
    }

    private static ImmutableArray<DocumentId> FindDocumentIdsCaseInsensitive(Solution solution, string filePath)
    {
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (string.Equals(doc.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    return [doc.Id];
            }
        }
        return [];
    }

    /// <summary>
    /// Converts 1-indexed line/column to a Roslyn TextSpan offset within the document's SourceText.
    /// </summary>
    public static int ToOffset(SourceText text, int line, int column)
    {
        // Convert from 1-indexed to 0-indexed
        var linePosition = new LinePosition(line - 1, column - 1);
        return text.Lines.GetPosition(linePosition);
    }

    public static Location ToLocation(FileLinePositionSpan span, string? preview = null) =>
        new(
            span.Path,
            span.StartLinePosition.Line + 1,       // back to 1-indexed
            span.StartLinePosition.Character + 1,
            preview
        );

    public static async Task<(Document doc, int offset)> ResolveAsync(
        Solution solution, Position pos)
    {
        var doc = ResolveDocument(solution, pos.FilePath);

        var text = await doc.GetTextAsync();
        var offset = ToOffset(text, pos.Line, pos.Column);
        return (doc, offset);
    }
}
