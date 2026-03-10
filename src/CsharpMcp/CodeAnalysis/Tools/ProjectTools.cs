using Microsoft.CodeAnalysis;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class ProjectTools
{
    public record ProjectFileEntry(string FilePath, string ProjectName);

    public static List<ProjectFileEntry> GetProjectFiles(
        Solution solution, string? projectName = null, string? filePattern = null, int maxResults = 500)
    {
        var projects = projectName is not null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        var results = new List<ProjectFileEntry>();

        foreach (var project in projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath is null) continue;

                if (filePattern is not null &&
                    !doc.FilePath.Contains(filePattern, StringComparison.OrdinalIgnoreCase) &&
                    !doc.Name.Contains(filePattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(new ProjectFileEntry(doc.FilePath, project.Name));
            }
        }

        return results.OrderBy(f => f.ProjectName).ThenBy(f => f.FilePath).Take(maxResults).ToList();
    }
}
