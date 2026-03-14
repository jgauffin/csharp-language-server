using System.IO.Enumeration;
using Microsoft.CodeAnalysis;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class ProjectTools
{
    public record ProjectFileEntry(string FilePath, string ProjectName);

    public static List<ProjectFileEntry> GetSolutionFiles(
        Solution solution, string rootPath, string? search = null, int maxResults = 200)
    {
        var root = rootPath.TrimEnd('\\', '/');
        var results = new List<ProjectFileEntry>();

        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath is null) continue;

                var relativePath = doc.FilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? doc.FilePath[(root.Length + 1)..].Replace('\\', '/')
                    : doc.FilePath;

                if (search is not null
                    && !MatchesPattern(relativePath, search)
                    && !MatchesPattern(project.Name, search))
                    continue;

                results.Add(new ProjectFileEntry(relativePath, project.Name));
            }
        }

        return results.OrderBy(f => f.ProjectName).ThenBy(f => f.FilePath).Take(maxResults).ToList();
    }

    /// <summary>
    /// Matches a value against a pattern. Supports glob (*, ?) or plain substring match.
    /// </summary>
    internal static bool MatchesPattern(string value, string pattern)
    {
        if (pattern.Contains('*') || pattern.Contains('?'))
            return FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true);

        return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
