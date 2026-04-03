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
    public void Packages_NoId_ReturnsPackageSummaries()
    {
        CreatePackage("mypkg", "1.0.0");
        CreatePackage("mypkg", "2.0.0");

        var result = _tools.nuget_packages();

        result.ShouldContain("mypkg");
        result.ShouldContain("2.0.0");
        result.ShouldContain("1.0.0");
    }

    [Fact]
    public void Packages_WithMissingId_ReturnsError()
    {
        var result = _tools.nuget_packages(id: "nonexistent");
        result.ShouldContain("not found");
    }

    [Fact]
    public void Packages_WithValidId_ReturnsFormatted()
    {
        CreatePackageWithNuspec("testpkg", "1.0.0");

        var result = _tools.nuget_packages(id: "testpkg");
        result.ShouldContain("Package: testpkg 1.0.0");
        result.ShouldContain("Description: Test");
    }

    [Fact]
    public async Task Search_MatchesFromCache_ReturnsCachedResults()
    {
        CreatePackage("serilog", "3.1.0");
        CreatePackage("serilog.sinks.console", "5.0.0");

        var result = await _tools.nuget_search("serilog");

        result.ShouldContain("serilog");
        result.ShouldContain("serilog.sinks.console");
        result.ShouldContain("[cached]");
    }

    [Fact]
    public void Explore_NonExistentPackage_ReturnsError()
    {
        var result = _tools.nuget_explore("nonexistent");
        result.ShouldContain("not found");
    }

    [Fact]
    public void Explore_WithDocs_ReturnsDocs()
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

        var result = _tools.nuget_explore("docpkg", includeDocs: true);

        result.ShouldContain("T:DocPkg.Foo");
        result.ShouldContain("A foo.");
    }

    [Fact]
    public void Explore_TypeFilter_NarrowsResults()
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

        var result = _tools.nuget_explore("docpkg", type: "Bar", includeDocs: true);

        result.ShouldContain("Bar");
        result.ShouldNotContain("Foo");
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
