using CsharpMcp.CodeAnalysis;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Shouldly;

namespace CsharpMcp.Tests;

/// <summary>
/// Tests that the workspace picks up file edits, new files, deletes, and renames
/// from disk via the FileSystemWatcher integration.
/// Each test gets its own temp copy of the fixture to avoid cross-test interference.
/// </summary>
public class WorkspaceFileWatchingTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private RoslynWorkspace _workspace = null!;

    static WorkspaceFileWatchingTests()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CsharpMcpWatchTests_" + Guid.NewGuid());
        CopyDirectory(FixturePath, _tempDir);
        _workspace = await RoslynWorkspace.LoadAsync(_tempDir);
    }

    public Task DisposeAsync()
    {
        _workspace.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // ── Edit existing file ─────────────────────────────────────────────────

    [Fact]
    public async Task Solution_ReflectsEditedFileContent()
    {
        var calcPath = FilePath("LibA", "Calculator.cs");
        var original = await File.ReadAllTextAsync(calcPath);
        var modified = original.Replace("public int Add(", "public int Sum(");

        await File.WriteAllTextAsync(calcPath, modified);
        await WaitForWatcherAsync();

        var doc = FindDocument(_workspace.Solution, calcPath);
        doc.ShouldNotBeNull();
        var text = (await doc.GetTextAsync()).ToString();
        text.ShouldContain("public int Sum(");
        text.ShouldNotContain("public int Add(");
    }

    [Fact]
    public async Task Solution_ReflectsMultipleEditsToSameFile()
    {
        var calcPath = FilePath("LibA", "Calculator.cs");

        // First edit
        var content = await File.ReadAllTextAsync(calcPath);
        await File.WriteAllTextAsync(calcPath, content.Replace("public int Add(", "public int Sum("));
        await WaitForWatcherAsync();

        var text1 = (await FindDocument(_workspace.Solution, calcPath)!.GetTextAsync()).ToString();
        text1.ShouldContain("public int Sum(");

        // Second edit
        await File.WriteAllTextAsync(calcPath, text1.Replace("public int Multiply(", "public int Mul("));
        await WaitForWatcherAsync();

        var text2 = (await FindDocument(_workspace.Solution, calcPath)!.GetTextAsync()).ToString();
        text2.ShouldContain("public int Sum(");
        text2.ShouldContain("public int Mul(");
    }

    // ── New file ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Solution_PicksUpNewFile()
    {
        var newFilePath = FilePath("LibA", "NewHelper.cs");
        await File.WriteAllTextAsync(newFilePath, @"namespace LibA;
public static class NewHelper
{
    public static int Double(int x) => x * 2;
}
");
        await WaitForWatcherAsync();

        var doc = FindDocument(_workspace.Solution, newFilePath);
        doc.ShouldNotBeNull();
        var text = (await doc.GetTextAsync()).ToString();
        text.ShouldContain("public static int Double(");
    }

    [Fact]
    public async Task Solution_NewFileIsAddedToCorrectProject()
    {
        var newFilePath = FilePath("LibB", "Cat.cs");
        await File.WriteAllTextAsync(newFilePath, @"namespace LibB;
public class Cat { }
");
        await WaitForWatcherAsync();

        var doc = FindDocument(_workspace.Solution, newFilePath);
        doc.ShouldNotBeNull();

        var project = _workspace.Solution.GetProject(doc.Project.Id);
        project.ShouldNotBeNull();
        Path.GetFileNameWithoutExtension(project.FilePath!).ShouldBe("LibB");
    }

    // ── Delete file ────────────────────────────────────────────────────────

    [Fact]
    public async Task Solution_RemovesDeletedFile()
    {
        var calcPath = FilePath("LibA", "Calculator.cs");
        FindDocument(_workspace.Solution, calcPath).ShouldNotBeNull();

        File.Delete(calcPath);
        await WaitForWatcherAsync();

        FindDocument(_workspace.Solution, calcPath).ShouldBeNull();
    }

    // ── Rename file ────────────────────────────────────────────────────────

    [Fact]
    public async Task Solution_HandlesRenamedFile()
    {
        var oldPath = FilePath("LibA", "Calculator.cs");
        var newPath = FilePath("LibA", "MathHelper.cs");
        FindDocument(_workspace.Solution, oldPath).ShouldNotBeNull();

        File.Move(oldPath, newPath);
        await WaitForWatcherAsync();

        FindDocument(_workspace.Solution, oldPath).ShouldBeNull();
        var doc = FindDocument(_workspace.Solution, newPath);
        doc.ShouldNotBeNull();
        var text = (await doc.GetTextAsync()).ToString();
        text.ShouldContain("public int Add(");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string FixturePath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestFixtures", "MultiProject"));

    private string FilePath(string project, string file) =>
        Path.Combine(_tempDir, project, file);

    private static Document? FindDocument(Solution solution, string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        return solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, normalized, StringComparison.OrdinalIgnoreCase));
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
