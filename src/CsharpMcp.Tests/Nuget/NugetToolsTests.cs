using CsharpMcp.Nuget;
using Shouldly;

namespace CsharpMcp.Tests.Nuget;

[Collection("NuGet")]
public class NugetToolsTests : IDisposable
{
    readonly string _cacheDir;
    readonly string? _originalEnv;
    readonly NugetTools _tools = new();

    public NugetToolsTests()
    {
        _originalEnv = Environment.GetEnvironmentVariable("NUGET_CACHE_PATH");
        _cacheDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDir);
        Environment.SetEnvironmentVariable("NUGET_CACHE_PATH", _cacheDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NUGET_CACHE_PATH", _originalEnv);
        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    [Fact]
    public void ListCached_ReturnsPackageSummaries()
    {
        CreatePackage("mypkg", "1.0.0");
        CreatePackage("mypkg", "2.0.0");

        var result = _tools.nuget_list_cached();

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("mypkg");
        result[0].Versions.Length.ShouldBe(2);
    }

    [Fact]
    public void PackageInfo_ReturnsMetadataOrErrorString()
    {
        var result = _tools.nuget_package_info("nonexistent");
        result.ShouldBeOfType<string>();
        ((string)result).ShouldContain("not found");
    }

    [Fact]
    public void PackageInfo_WithValidPackage_ReturnsPackageInfo()
    {
        CreatePackageWithNuspec("testpkg", "1.0.0");

        var result = _tools.nuget_package_info("testpkg");
        result.ShouldBeOfType<PackageInfo>();
    }

    [Fact]
    public async Task Search_MatchesFromCache_ReturnsCachedResults()
    {
        CreatePackage("serilog", "3.1.0");
        CreatePackage("serilog.sinks.console", "5.0.0");

        var result = await _tools.nuget_search("serilog");

        result.ShouldBeOfType<List<SearchResult>>();
        var list = (List<SearchResult>)result;
        list.Count.ShouldBe(2);
        list.ShouldAllBe(r => r.FromCache);
    }

    [Fact]
    public void AssemblyTypes_NonExistentPackage_ReturnsEmpty()
    {
        var result = _tools.nuget_assembly_types("nonexistent");
        result.ShouldBeEmpty();
    }

    [Fact]
    public void AssemblyDocs_WithXml_ReturnsDocs()
    {
        var dir = CreatePackage("docpkg", "1.0.0");
        var xmlDir = Path.Combine(dir, "lib", "net8.0");
        Directory.CreateDirectory(xmlDir);
        File.WriteAllText(Path.Combine(xmlDir, "DocPkg.xml"), """
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:DocPkg.Foo"><summary>A foo.</summary></member>
              </members>
            </doc>
            """);

        var result = _tools.nuget_assembly_docs("docpkg");

        result.Count.ShouldBe(1);
        result[0].Summary.ShouldBe("A foo.");
    }

    [Fact]
    public void AssemblyDocs_TypeFilter_NarrowsResults()
    {
        var dir = CreatePackage("docpkg", "1.0.0");
        var xmlDir = Path.Combine(dir, "lib", "net8.0");
        Directory.CreateDirectory(xmlDir);
        File.WriteAllText(Path.Combine(xmlDir, "DocPkg.xml"), """
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:DocPkg.Foo"><summary>A</summary></member>
                <member name="T:DocPkg.Bar"><summary>B</summary></member>
              </members>
            </doc>
            """);

        var result = _tools.nuget_assembly_docs("docpkg", type: "Bar");

        result.Count.ShouldBe(1);
        result[0].MemberId.ShouldContain("Bar");
    }

    string CreatePackage(string id, string version)
    {
        var dir = Path.Combine(_cacheDir, id.ToLowerInvariant(), version);
        Directory.CreateDirectory(dir);
        return dir;
    }

    void CreatePackageWithNuspec(string id, string version)
    {
        var dir = CreatePackage(id, version);
        File.WriteAllText(Path.Combine(dir, $"{id}.nuspec"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.nuget.org/packaging/2010/07/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <description>Test</description>
                <authors>Test</authors>
              </metadata>
            </package>
            """);
    }
}
