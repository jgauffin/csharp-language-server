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
        var doc = PositionHelper.ResolveDocument(solution, filePath);
        var model = await doc.GetSemanticModelAsync();
        if (model is null) return [];

        return model.GetDiagnostics()
            .Where(d => d.Location.IsInSource)
            .Select(ToDiagnosticEntry)
            .ToList();
    }

    public record DiagnosticPage(List<DiagnosticEntry> Items, int TotalCount);

    public static async Task<DiagnosticPage> GetAllDiagnosticsAsync(
        Solution solution,
        string? projectName = null,
        string? minSeverity = null,
        int skip = 0,
        int take = 100)
    {
        var projects = projectName is not null
            ? solution.Projects.Where(p => ProjectTools.MatchesPattern(p.Name, projectName))
            : solution.Projects;

        var sevFilter = ParseMinSeverity(minSeverity);

        var results = new List<DiagnosticEntry>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            var diags = compilation.GetDiagnostics()
                .Where(d => d.Location.IsInSource && d.Severity >= sevFilter);

            results.AddRange(diags.Select(ToDiagnosticEntry));
        }

        // Sort: errors first, then warnings, then by file/line
        results.Sort((a, b) =>
        {
            var sevCmp = SeverityOrder(b.Severity).CompareTo(SeverityOrder(a.Severity));
            if (sevCmp != 0) return sevCmp;
            var fileCmp = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
            if (fileCmp != 0) return fileCmp;
            return a.Line.CompareTo(b.Line);
        });

        var totalCount = results.Count;
        var paged = results.Skip(skip).Take(take).ToList();
        return new DiagnosticPage(paged, totalCount);
    }

    private static DiagnosticSeverity ParseMinSeverity(string? s) => s?.ToLowerInvariant() switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        "info" => DiagnosticSeverity.Info,
        "hidden" => DiagnosticSeverity.Hidden,
        _ => DiagnosticSeverity.Warning // default: skip info/hidden
    };

    private static int SeverityOrder(string s) => s switch
    {
        "Error" => 3,
        "Warning" => 2,
        "Info" => 1,
        _ => 0
    };

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
