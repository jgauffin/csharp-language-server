using CsharpMcp.CodeAnalysis;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Shouldly;

namespace CsharpMcp.Tests;

/// <summary>
/// Proves that changing a project's build files — adding a package, adding a project
/// reference, or editing central build props — is picked up without restarting the server.
/// Before this, the project model was evaluated once at load and never again, so a type
/// from a newly added dependency stayed "not found" for the lifetime of the process.
/// Each test gets its own temp copy of the fixture to avoid cross-test interference.
/// </summary>
public class WorkspaceProjectModelReloadTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private RoslynWorkspace _workspace = null!;

    static WorkspaceProjectModelReloadTests()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CsharpMcpReloadTests_" + Guid.NewGuid());
        CopyDirectory(FixturePath, _tempDir);
        _workspace = await RoslynWorkspace.LoadAsync(_tempDir);
    }

    public Task DisposeAsync()
    {
        _workspace.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ── Project reference added after load ─────────────────────────────────

    [Fact]
    public async Task Project_reference_added_after_load_is_visible_without_restart()
    {
        // LibA is a leaf in the fixture — nothing it references — so adding LibB is a reference
        // that genuinely did not exist when the workspace loaded.
        ProjectReferenceCount("LibA").ShouldBe(0);

        AddProjectReference(CsprojPath("LibA"), @"..\LibB\LibB.csproj");
        await WaitForWatcherAsync();

        // Reading the solution must re-evaluate the build files, not replay the stale model.
        ProjectReferenceCount("LibA").ShouldBe(1);
    }

    [Fact]
    public async Task Type_from_newly_referenced_project_resolves_without_restart()
    {
        // The user-visible symptom: a type from a dependency added after load reports as
        // missing until the server restarts.
        AddProjectReference(CsprojPath("LibA"), @"..\LibB\LibB.csproj");
        await WaitForWatcherAsync();

        var libA = _workspace.Solution.Projects.Single(p => p.Name == "LibA");
        var compilation = await _workspace.GetCompilationAsync(libA);

        compilation.ShouldNotBeNull();
        compilation.GetTypeByMetadataName("LibB.Dog").ShouldNotBeNull();
    }

    // ── csproj property changed after load ─────────────────────────────────

    [Fact]
    public async Task Csproj_property_change_after_load_is_reevaluated()
    {
        var csproj = CsprojPath("LibA");
        var original = await File.ReadAllTextAsync(csproj);
        original.ShouldContain("<Nullable>enable</Nullable>");

        await File.WriteAllTextAsync(csproj, original.Replace(
            "<Nullable>enable</Nullable>",
            "<Nullable>disable</Nullable>"));
        await WaitForWatcherAsync();

        var libA = _workspace.Solution.Projects.Single(p => p.Name == "LibA");
        var options = (Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions)libA.CompilationOptions!;
        options.NullableContextOptions.ShouldBe(NullableContextOptions.Disable);
    }

    // ── Central build files ────────────────────────────────────────────────

    [Fact]
    public async Task Directory_build_props_added_after_load_is_reevaluated()
    {
        // Central build props change every project's compilation without touching any csproj,
        // so watching only *.csproj would miss this.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Directory.Build.props"), @"<Project>
  <PropertyGroup>
    <DefineConstants>$(DefineConstants);CENTRAL_MARKER</DefineConstants>
  </PropertyGroup>
</Project>
");
        await WaitForWatcherAsync();

        var libA = _workspace.Solution.Projects.Single(p => p.Name == "LibA");
        var options = (Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)libA.ParseOptions!;
        options.PreprocessorSymbolNames.ShouldContain("CENTRAL_MARKER");
    }

    // ── Reload must not lose unrelated state ───────────────────────────────

    [Fact]
    public async Task Reload_keeps_pending_source_edits_from_disk()
    {
        // A reload re-reads the build files; source edits made just before it must still
        // be visible afterwards rather than being dropped with the old solution.
        var calcPath = Path.Combine(_tempDir, "LibA", "Calculator.cs");
        var source = await File.ReadAllTextAsync(calcPath);
        await File.WriteAllTextAsync(calcPath, source.Replace("public int Add(", "public int Sum("));

        AddProjectReference(CsprojPath("LibA"), @"..\LibB\LibB.csproj");
        await WaitForWatcherAsync();

        var doc = _workspace.Solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, calcPath, StringComparison.OrdinalIgnoreCase));
        doc.ShouldNotBeNull();
        (await doc.GetTextAsync()).ToString().ShouldContain("public int Sum(");
    }

    [Fact]
    public async Task Unrelated_source_edit_does_not_trigger_project_reload()
    {
        // Reloading MSBuild on every .cs save would make ordinary editing unusably slow on a
        // large solution, so only build-file changes may mark the project model dirty.
        var before = _workspace.ProjectModelReloadCount;

        var calcPath = Path.Combine(_tempDir, "LibA", "Calculator.cs");
        var source = await File.ReadAllTextAsync(calcPath);
        await File.WriteAllTextAsync(calcPath, source.Replace("public int Add(", "public int Sum("));
        await WaitForWatcherAsync();

        _ = _workspace.Solution;
        _workspace.ProjectModelReloadCount.ShouldBe(before);
    }

    [Fact]
    public async Task Repeated_reads_reload_the_project_model_only_once_per_change()
    {
        var before = _workspace.ProjectModelReloadCount;

        AddProjectReference(CsprojPath("LibA"), @"..\LibB\LibB.csproj");
        await WaitForWatcherAsync();

        _ = _workspace.Solution;
        _ = _workspace.Solution;
        _ = _workspace.Solution;

        _workspace.ProjectModelReloadCount.ShouldBe(before + 1);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string FixturePath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestFixtures", "MultiProject"));

    private string CsprojPath(string project) =>
        Path.Combine(_tempDir, project, project + ".csproj");

    private int ProjectReferenceCount(string projectName) =>
        _workspace.Solution.Projects.Single(p => p.Name == projectName).ProjectReferences.Count();

    private static void AddProjectReference(string csprojPath, string relativeReference)
    {
        var xml = File.ReadAllText(csprojPath);
        var itemGroup = $@"  <ItemGroup>
    <ProjectReference Include=""{relativeReference}"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(csprojPath, xml.Replace("</Project>", itemGroup));
    }

    private static async Task WaitForWatcherAsync()
    {
        // FileSystemWatcher events are async; give them time to fire and be enqueued
        await Task.Delay(500);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
