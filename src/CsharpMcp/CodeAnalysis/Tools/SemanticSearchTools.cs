using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class SemanticSearchTools
{
    public record FindResult(string Name, string Kind, string FilePath, int Line, string ContainingType);

    /// <summary>
    /// Searches for symbols by name pattern across all projects (or a specific one).
    /// namePattern supports substring match; kind filters by SymbolKind name (e.g. "Method", "NamedType").
    /// </summary>
    public static async Task<List<FindResult>> FindAsync(
        Solution solution,
        string namePattern,
        string? kind = null,
        string? projectName = null)
    {
        var projects = projectName is not null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        var results = new List<FindResult>();

        foreach (var project in projects)
        {
            var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                project,
                name => name.Contains(namePattern, StringComparison.OrdinalIgnoreCase)
            );

            foreach (var sym in symbols)
            {
                if (kind is not null && !sym.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase))
                    continue;

                var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
                if (loc is null) continue;

                var span = loc.GetLineSpan();
                results.Add(new FindResult(
                    sym.Name,
                    sym.Kind.ToString(),
                    span.Path,
                    span.StartLinePosition.Line + 1,
                    sym.ContainingType?.ToDisplayString() ?? sym.ContainingNamespace?.ToString() ?? ""
                ));
            }
        }

        return results;
    }

    /// <summary>
    /// Fast fuzzy symbol search across the workspace.
    /// </summary>
    public static async Task<List<FindResult>> GetWorkspaceSymbolsAsync(
        Solution solution,
        string query,
        string? projectName = null)
    {
        // Reuse Find with the same pattern — SymbolFinder already does prefix/substring matching
        return await FindAsync(solution, query, kind: null, projectName: projectName);
    }
}
