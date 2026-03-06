using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace CsharpMcp.Nuget;

static class RemoteClient
{
    static string SourceUrl => Environment.GetEnvironmentVariable("NUGET_SOURCE")
        ?? "https://api.nuget.org/v3/index.json";

    static readonly SourceCacheContext CacheCtx = new();

    public static async Task<List<SearchResult>> SearchAsync(string query, string? sourceOverride, int take = 20)
    {
        var url = sourceOverride ?? SourceUrl;
        var repository = Repository.Factory.GetCoreV3(url);
        var resource = await repository.GetResourceAsync<PackageSearchResource>();

        var results = await resource.SearchAsync(
            query,
            new SearchFilter(includePrerelease: false),
            skip: 0,
            take: take,
            log: NullLogger.Instance,
            cancellationToken: CancellationToken.None
        );

        return results.Select(r => new SearchResult(
            r.Identity.Id,
            r.Identity.Version.ToString(),
            r.Description,
            r.Authors,
            r.DownloadCount ?? 0,
            FromCache: false
        )).ToList();
    }
}
