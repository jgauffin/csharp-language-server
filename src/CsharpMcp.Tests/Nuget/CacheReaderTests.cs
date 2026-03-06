using CsharpMcp.Nuget;
using Shouldly;

namespace CsharpMcp.Tests.Nuget;

[Collection("NuGet")]
public class CacheReaderTests : IDisposable
{
    readonly string _cacheDir;
    readonly string? _originalEnv;

    public CacheReaderTests()
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
    public void ListCached_EmptyCache_ReturnsEmpty()
    {
        var result = CacheReader.ListCached();
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ListCached_WithPackages_ReturnsPackagesAndVersions()
    {
        CreateFakePackage("newtonsoft.json", "13.0.3");
        CreateFakePackage("newtonsoft.json", "12.0.1");
        CreateFakePackage("serilog", "3.1.0");

        var result = CacheReader.ListCached();

        result.Count.ShouldBe(2);

        var nj = result.Single(p => p.Id == "newtonsoft.json");
        nj.Versions.ShouldContain("13.0.3");
        nj.Versions.ShouldContain("12.0.1");

        var sl = result.Single(p => p.Id == "serilog");
        sl.Versions.ShouldContain("3.1.0");
    }

    [Fact]
    public void GetPackageInfo_ExistingPackage_ReturnsMetadata()
    {
        CreateFakePackageWithNuspec("testlib", "1.0.0",
            description: "A test library",
            authors: "Test Author");

        var info = CacheReader.GetPackageInfo("testlib", "1.0.0");

        info.ShouldNotBeNull();
        info.Id.ShouldBe("TestLib");
        info.Version.ShouldBe("1.0.0");
        info.Description.ShouldBe("A test library");
        info.Authors.ShouldBe("Test Author");
    }

    [Fact]
    public void GetPackageInfo_LatestVersion_WhenVersionOmitted()
    {
        CreateFakePackageWithNuspec("testlib", "1.0.0", description: "old");
        CreateFakePackageWithNuspec("testlib", "2.0.0", description: "new");

        var info = CacheReader.GetPackageInfo("testlib", null);

        info.ShouldNotBeNull();
        info.Version.ShouldBe("2.0.0");
    }

    [Fact]
    public void GetPackageInfo_NonExistent_ReturnsNull()
    {
        var info = CacheReader.GetPackageInfo("nonexistent", null);
        info.ShouldBeNull();
    }

    [Fact]
    public void GetPackageInfo_ListsFiles()
    {
        CreateFakePackageWithNuspec("testlib", "1.0.0");
        // Add an extra file
        var libDir = Path.Combine(_cacheDir, "testlib", "1.0.0", "lib", "net8.0");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "testlib.dll"), "");

        var info = CacheReader.GetPackageInfo("testlib", "1.0.0");

        info.ShouldNotBeNull();
        info.Files.ShouldContain(f => f.Contains("testlib.nuspec"));
        info.Files.ShouldContain(f => f.Contains("testlib.dll"));
    }

    [Fact]
    public void GetAssemblyDocs_WithXmlDocs_ReturnsEntries()
    {
        var versionDir = CreateFakePackage("testlib", "1.0.0");
        var xmlDir = Path.Combine(versionDir, "lib", "net8.0");
        Directory.CreateDirectory(xmlDir);
        File.WriteAllText(Path.Combine(xmlDir, "TestLib.xml"), """
            <?xml version="1.0"?>
            <doc>
              <assembly><name>TestLib</name></assembly>
              <members>
                <member name="T:TestLib.MyClass">
                  <summary>A test class.</summary>
                </member>
                <member name="M:TestLib.MyClass.DoWork(System.String)">
                  <summary>Does work.</summary>
                  <param name="input">The input.</param>
                  <returns>A result.</returns>
                </member>
                <member name="T:TestLib.OtherClass">
                  <summary>Another class.</summary>
                </member>
              </members>
            </doc>
            """);

        var docs = CacheReader.GetAssemblyDocs("testlib", "1.0.0", null, null);

        docs.Count.ShouldBe(3);
        docs.ShouldContain(d => d.MemberId == "T:TestLib.MyClass" && d.Summary == "A test class.");
        docs.ShouldContain(d => d.MemberId == "M:TestLib.MyClass.DoWork(System.String)"
            && d.Returns == "A result."
            && d.Params.Length == 1
            && d.Params[0].Name == "input");
    }

    [Fact]
    public void GetAssemblyDocs_WithTypeFilter_FiltersResults()
    {
        var versionDir = CreateFakePackage("testlib", "1.0.0");
        var xmlDir = Path.Combine(versionDir, "lib", "net8.0");
        Directory.CreateDirectory(xmlDir);
        File.WriteAllText(Path.Combine(xmlDir, "TestLib.xml"), """
            <?xml version="1.0"?>
            <doc>
              <members>
                <member name="T:TestLib.MyClass"><summary>A</summary></member>
                <member name="M:TestLib.MyClass.Run"><summary>B</summary></member>
                <member name="T:TestLib.OtherClass"><summary>C</summary></member>
              </members>
            </doc>
            """);

        var docs = CacheReader.GetAssemblyDocs("testlib", "1.0.0", null, "MyClass");

        docs.Count.ShouldBe(2);
        docs.ShouldAllBe(d => d.MemberId.Contains("MyClass"));
    }

    [Fact]
    public void GetAssemblyDocs_WithAssemblyFilter_MatchesByName()
    {
        var versionDir = CreateFakePackage("testlib", "1.0.0");
        var xmlDir = Path.Combine(versionDir, "lib", "net8.0");
        Directory.CreateDirectory(xmlDir);
        File.WriteAllText(Path.Combine(xmlDir, "TestLib.xml"), """
            <?xml version="1.0"?>
            <doc><members><member name="T:A"><summary>yes</summary></member></members></doc>
            """);
        File.WriteAllText(Path.Combine(xmlDir, "Other.xml"), """
            <?xml version="1.0"?>
            <doc><members><member name="T:B"><summary>no</summary></member></members></doc>
            """);

        var docs = CacheReader.GetAssemblyDocs("testlib", "1.0.0", "TestLib", null);

        docs.Count.ShouldBe(1);
        docs[0].MemberId.ShouldBe("T:A");
    }

    [Fact]
    public void GetAssemblyTypes_NonExistentPackage_ReturnsEmpty()
    {
        var result = CacheReader.GetAssemblyTypes("nonexistent", null, null);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetAssemblyTypes_NoDlls_ReturnsEmpty()
    {
        CreateFakePackage("testlib", "1.0.0");

        var result = CacheReader.GetAssemblyTypes("testlib", "1.0.0", null);
        result.ShouldBeEmpty();
    }

    string CreateFakePackage(string id, string version)
    {
        var dir = Path.Combine(_cacheDir, id.ToLowerInvariant(), version);
        Directory.CreateDirectory(dir);
        return dir;
    }

    void CreateFakePackageWithNuspec(string id, string version,
        string? description = null, string? authors = null)
    {
        var dir = CreateFakePackage(id, version);
        var nuspec = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.nuget.org/packaging/2010/07/nuspec.xsd">
              <metadata>
                <id>TestLib</id>
                <version>{version}</version>
                <description>{description ?? "desc"}</description>
                <authors>{authors ?? "author"}</authors>
              </metadata>
            </package>
            """;
        File.WriteAllText(Path.Combine(dir, $"{id}.nuspec"), nuspec);
    }
}
