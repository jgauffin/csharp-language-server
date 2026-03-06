using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CsharpMcp.CodeAnalysis;

public record Position(string FilePath, int Line, int Column);

public record Location(string FilePath, int Line, int Column, string? Preview = null);

public record SymbolInfo(string Name, string Kind, string ContainingType, string? Documentation);

public static class PositionHelper
{
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
        var doc = solution.GetDocumentIdsWithFilePath(pos.FilePath)
            .Select(id => solution.GetDocument(id))
            .FirstOrDefault(d => d is not null)
            ?? throw new ArgumentException($"File not found in solution: {pos.FilePath}");

        var text = await doc.GetTextAsync();
        var offset = ToOffset(text, pos.Line, pos.Column);
        return (doc, offset);
    }
}
