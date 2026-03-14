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
    private static readonly Dictionary<string, SymbolKind> KindAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["class"] = SymbolKind.NamedType,
        ["interface"] = SymbolKind.NamedType,
        ["enum"] = SymbolKind.NamedType,
        ["struct"] = SymbolKind.NamedType,
        ["delegate"] = SymbolKind.NamedType,
        ["type"] = SymbolKind.NamedType,
    };

    internal static bool MatchesKind(ISymbol sym, string kind)
    {
        // Direct match on SymbolKind name (e.g. "Method", "NamedType", "Property")
        if (sym.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase))
            return true;

        // Alias match (e.g. "class" -> NamedType) with TypeKind refinement
        if (KindAliases.TryGetValue(kind, out var mappedKind))
        {
            if (sym.Kind != mappedKind) return false;
            if (sym is INamedTypeSymbol namedType)
            {
                return kind.Equals(namedType.TypeKind.ToString(), StringComparison.OrdinalIgnoreCase)
                    || kind.Equals("type", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        return false;
    }

    public static async Task<List<FindResult>> FindAsync(
        Solution solution,
        string namePattern,
        string? kind = null,
        string? projectName = null,
        int maxResults = 200)
    {
        var projects = projectName is not null
            ? solution.Projects.Where(p => ProjectTools.MatchesPattern(p.Name, projectName))
            : solution.Projects;

        var results = new List<FindResult>();

        foreach (var project in projects)
        {
            var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                project,
                name => ProjectTools.MatchesPattern(name, namePattern)
            );

            foreach (var sym in symbols)
            {
                if (kind is not null && !MatchesKind(sym, kind))
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

                if (results.Count >= maxResults) return results;
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
        string? projectName = null,
        int maxResults = 200)
    {
        return await FindAsync(solution, query, kind: null, projectName: projectName, maxResults: maxResults);
    }
}
