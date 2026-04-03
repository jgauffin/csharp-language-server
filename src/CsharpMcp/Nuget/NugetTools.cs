using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CsharpMcp.Nuget;

[McpServerToolType]
public class NugetTools
{
    [McpServerTool, Description("Search for NuGet packages. Checks local cache first, falls back to remote source.")]
    public async Task<string> nuget_search(
        string query,
        [Description("NuGet source URL (optional, overrides NUGET_SOURCE env)")] string? source = null,
        [Description("Max results to return")] int take = 20)
    {
        var cached = CacheReader.ListCached()
            .Where(p => p.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(take)
            .Select(p => new SearchResult(p.Id, p.Versions.FirstOrDefault() ?? "", null, null, 0, FromCache: true))
            .ToList();

        if (cached.Count > 0)
            return NugetFormatter.Format(cached);

        return NugetFormatter.Format(await RemoteClient.SearchAsync(query, source, take));
    }

    [McpServerTool, Description("List cached NuGet packages, or get metadata/dependencies/files for a specific package. Omit id to list all cached packages. Provide id to get package details.")]
    public string nuget_packages(
        [Description("Package id to get details for. Omit to list all cached packages.")] string? id = null,
        [Description("Package version (optional, uses latest cached if omitted)")] string? version = null,
        [Description("Glob-style filter for file listing e.g. '*.dll', 'lib/**/*.xml' (optional, all files if omitted)")] string? fileFilter = null)
    {
        if (id is null)
            return NugetFormatter.Format(CacheReader.ListCached());

        var resolved = CacheReader.ResolveVersionDirWithError(id, version);
        if (resolved.Error != null) return resolved.Error;

        var info = CacheReader.GetPackageInfo(id, version, fileFilter);
        return info != null ? NugetFormatter.Format(info) : $"Package '{id}' not found in cache.";
    }

    [McpServerTool, Description("Explore assemblies, types, and documentation in a cached NuGet package. Without assembly param: lists assemblies and target frameworks. With assembly: shows public types and members. With includeDocs: also includes XML documentation.")]
    public string nuget_explore(
        string id,
        [Description("Package version (optional, uses latest cached if omitted)")] string? version = null,
        [Description("Assembly filename filter e.g. MyLib.dll or MyLib (optional, lists all assemblies if omitted)")] string? assembly = null,
        [Description("Type name filter - only return types whose name contains this string (optional)")] string? type = null,
        [Description("Include XML documentation in the output")] bool includeDocs = false)
    {
        var resolved = CacheReader.ResolveVersionDirWithError(id, version);
        if (resolved.Error != null) return resolved.Error;

        // No assembly specified → list available assemblies
        if (assembly is null && type is null && !includeDocs)
            return NugetFormatter.FormatAssemblyList(CacheReader.ListAssemblies(id, version));

        // Get types
        var results = CacheReader.GetAssemblyTypes(id, version, assembly, type);
        if (results.Count == 0 && assembly != null)
        {
            var available = CacheReader.ListAssemblies(id, version);
            if (available.Count > 0)
                return $"No assembly matching '{assembly}'. Available assemblies:\n{NugetFormatter.FormatAssemblyList(available)}";
        }
        if (results.Count == 0 && type != null)
            return $"No types matching '{type}' found in package '{id}'.";

        var output = NugetFormatter.Format(results);

        // Append docs if requested
        if (includeDocs)
        {
            var docs = CacheReader.GetAssemblyDocs(id, version, assembly, type);
            var docsOutput = NugetFormatter.Format(docs);
            if (!string.IsNullOrEmpty(docsOutput) && docsOutput != "No documentation found.")
                output += "\n\n── Documentation ──\n" + docsOutput;
        }

        return output;
    }
}
