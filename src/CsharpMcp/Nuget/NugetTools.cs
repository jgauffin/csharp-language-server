using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CsharpMcp.Nuget;

[McpServerToolType]
public class NugetTools
{
    [McpServerTool, Description("Search for NuGet packages. Checks local cache first, falls back to remote source.")]
    public async Task<object> nuget_search(
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
            return cached;

        return await RemoteClient.SearchAsync(query, source, take);
    }

    [McpServerTool, Description("List all packages present in the local NuGet cache with their available versions.")]
    public List<PackageSummary> nuget_list_cached() =>
        CacheReader.ListCached();

    [McpServerTool, Description("Get metadata, dependencies, and file listing for a cached package.")]
    public object nuget_package_info(
        string id,
        [Description("Package version (optional, latest cached if omitted)")] string? version = null) =>
        CacheReader.GetPackageInfo(id, version)
        ?? (object)$"Package '{id}' not found in cache";

    [McpServerTool, Description("Get public type and member definitions from assemblies in a cached package. Does not include documentation.")]
    public List<AssemblyInfo> nuget_assembly_types(
        string id,
        [Description("Package version (optional)")] string? version = null,
        [Description("Assembly filename filter e.g. MyLib.dll (optional, all assemblies if omitted)")] string? assembly = null) =>
        CacheReader.GetAssemblyTypes(id, version, assembly);

    [McpServerTool, Description("Get XML documentation for types in a cached package assembly. Use the type filter to reduce token count.")]
    public List<DocEntry> nuget_assembly_docs(
        string id,
        [Description("Package version (optional)")] string? version = null,
        [Description("Assembly/XML filename filter e.g. MyLib (optional)")] string? assembly = null,
        [Description("Type name filter - only return docs for members containing this string (optional)")] string? type = null) =>
        CacheReader.GetAssemblyDocs(id, version, assembly, type);
}
