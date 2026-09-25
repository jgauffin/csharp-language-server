using Microsoft.CodeAnalysis;

namespace CsharpMcp.CodeAnalysis.Tools;

/// <summary>
/// Predicates shared by the filtering options on find and get_references.
/// </summary>
public static class SymbolFilters
{
    public static bool IsTestPath(string filePath) =>
        filePath.Contains("Test", StringComparison.OrdinalIgnoreCase)
        || filePath.Contains("Spec", StringComparison.OrdinalIgnoreCase);

    public static bool IsGeneratedPath(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');

        if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            return true;

        var fileName = Path.GetFileName(normalized);
        return fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matches "public", "internal", "private" or "protected". Protected also matches the
    /// combined forms (protected internal, private protected).
    /// </summary>
    public static bool MatchesAccessibility(ISymbol symbol, string accessibility) =>
        accessibility.ToLowerInvariant() switch
        {
            "public" => symbol.DeclaredAccessibility == Accessibility.Public,
            "internal" => symbol.DeclaredAccessibility == Accessibility.Internal,
            "private" => symbol.DeclaredAccessibility == Accessibility.Private,
            "protected" => symbol.DeclaredAccessibility
                is Accessibility.Protected
                or Accessibility.ProtectedOrInternal
                or Accessibility.ProtectedAndInternal,
            _ => throw new ArgumentException(
                $"Unknown accessibility '{accessibility}'. Use public, internal, private or protected.")
        };

    /// <summary>
    /// Matches an attribute by short name ("ApiController"), full name ("ApiControllerAttribute")
    /// or fully qualified name, as glob or substring.
    /// </summary>
    public static bool HasAttribute(ISymbol symbol, string pattern) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass is { } cls
            && (ProjectTools.MatchesPattern(cls.Name, pattern)
                || ProjectTools.MatchesPattern(cls.ToDisplayString(), pattern)));
}
