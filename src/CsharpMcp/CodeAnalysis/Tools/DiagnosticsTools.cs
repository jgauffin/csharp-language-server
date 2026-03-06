using Microsoft.CodeAnalysis;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class DiagnosticsTools
{
    public record DiagnosticEntry(
        string Id,
        string Severity,
        string Message,
        string FilePath,
        int Line,
        int Column
    );

    public static async Task<List<DiagnosticEntry>> GetDiagnosticsAsync(
        Solution solution,
        string filePath)
    {
        var docId = solution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault()
            ?? throw new ArgumentException($"File not found: {filePath}");

        var doc = solution.GetDocument(docId)!;
        var model = await doc.GetSemanticModelAsync();
        if (model is null) return [];

        return model.GetDiagnostics()
            .Where(d => d.Location.IsInSource)
            .Select(ToDiagnosticEntry)
            .ToList();
    }

    public static async Task<List<DiagnosticEntry>> GetAllDiagnosticsAsync(
        Solution solution,
        string? projectName = null)
    {
        var projects = projectName is not null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        var results = new List<DiagnosticEntry>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            var diags = compilation.GetDiagnostics()
                .Where(d => d.Location.IsInSource);

            results.AddRange(diags.Select(ToDiagnosticEntry));
        }

        return results;
    }

    private static DiagnosticEntry ToDiagnosticEntry(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        return new DiagnosticEntry(
            d.Id,
            d.Severity.ToString(),
            d.GetMessage(),
            span.Path,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1
        );
    }
}
