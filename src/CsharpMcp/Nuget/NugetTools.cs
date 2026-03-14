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

    [McpServerTool, Description("List all packages present in the local NuGet cache with their available versions.")]
    public string nuget_list_cached() =>
        NugetFormatter.Format(CacheReader.ListCached());

    [McpServerTool, Description("Get metadata, dependencies, and file listing for a cached package.")]
    public string nuget_package_info(
        string id,
        [Description("Package version (optional, uses latest cached if omitted)")] string? version = null,
        [Description("Glob-style filter for file listing e.g. '*.dll', 'lib/**/*.xml' (optional, all files if omitted)")] string? fileFilter = null)
    {
        var resolved = CacheReader.ResolveVersionDirWithError(id, version);
        if (resolved.Error != null) return resolved.Error;

        var info = CacheReader.GetPackageInfo(id, version, fileFilter);
        return info != null ? NugetFormatter.Format(info) : $"Package '{id}' not found in cache.";
    }

    [McpServerTool, Description("List assembly names and target frameworks in a cached package. Use this before nuget_assembly_types to find the right assembly name.")]
    public string nuget_list_assemblies(
        string id,
        [Description("Package version (optional, uses latest cached if omitted)")] string? version = null)
    {
        var resolved = CacheReader.ResolveVersionDirWithError(id, version);
        if (resolved.Error != null) return resolved.Error;
        return NugetFormatter.FormatAssemblyList(CacheReader.ListAssemblies(id, version));
    }

    [McpServerTool, Description("Get public type and member definitions from assemblies in a cached package. Does not include documentation. Use type filter to reduce output for large packages.")]
    public string nuget_assembly_types(
        string id,
        [Description("Package version (optional, uses latest cached if omitted)")] string? version = null,
        [Description("Assembly filename filter e.g. MyLib.dll or MyLib (optional, all assemblies if omitted)")] string? assembly = null,
        [Description("Type name filter - only return types whose name contains this string (optional)")] string? type = null)
    {
        var resolved = CacheReader.ResolveVersionDirWithError(id, version);
        if (resolved.Error != null) return resolved.Error;

        var results = CacheReader.GetAssemblyTypes(id, version, assembly, type);
        if (results.Count == 0 && assembly != null)
        {
            var available = CacheReader.ListAssemblies(id, version);
            if (available.Count > 0)
                return $"No assembly matching '{assembly}'. Available assemblies:\n{NugetFormatter.FormatAssemblyList(available)}";
        }
        if (results.Count == 0 && type != null)
            return $"No types matching '{type}' found in package '{id}'.";

        return NugetFormatter.Format(results);
    }

    [McpServerTool, Description("Get XML documentation for types in a cached package assembly. Use the type filter to reduce token count.")]
    public string nuget_assembly_docs(
        string id,
        [Description("Package version (optional, uses latest cached if omitted)")] string? version = null,
        [Description("Assembly/XML filename filter e.g. MyLib (optional)")] string? assembly = null,
        [Description("Type name filter - only return docs for members containing this string (optional)")] string? type = null)
    {
        var resolved = CacheReader.ResolveVersionDirWithError(id, version);
        if (resolved.Error != null) return resolved.Error;
        return NugetFormatter.Format(CacheReader.GetAssemblyDocs(id, version, assembly, type));
    }
}
