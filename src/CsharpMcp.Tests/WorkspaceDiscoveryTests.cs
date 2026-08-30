using System.Diagnostics;
using CsharpMcp.CodeAnalysis;
using Shouldly;

namespace CsharpMcp.Tests;

/// <summary>
/// Project discovery walks the workspace root. It must prune build output and package caches
/// while descending, and it must never follow a directory link, because package managers link
/// package directories back at their own ancestors.
/// </summary>
public class WorkspaceDiscoveryTests : IDisposable
{
    private readonly string _root;

    public WorkspaceDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CsharpMcpDiscoveryTests_" + Guid.NewGuid());
        WriteProject("App", "App.csproj");
        WriteProject(Path.Combine("App", "bin", "Debug"), "Stale.csproj");
        WriteProject(Path.Combine("App", "obj"), "Generated.csproj");
        WriteProject(Path.Combine("node_modules", "some-package"), "Bundled.csproj");
    }

    public void Dispose()
    {
        // Delete the link before the tree so a recursive delete never has a cycle to consider.
        var link = Path.Combine(_root, "App", "link-to-root");
        try { Directory.Delete(link); } catch { /* may not exist */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void EnumerateFiles_FindsProjectsOutsideExcludedDirectories()
    {
        var found = RoslynWorkspace.EnumerateFiles(_root, "*.csproj").ToArray();

        found.ShouldHaveSingleItem();
        Path.GetFileName(found[0]).ShouldBe("App.csproj");
    }

    [Fact]
    public void EnumerateFiles_SkipsBuildOutputAndPackageCaches()
    {
        var found = RoslynWorkspace.EnumerateFiles(_root, "*.csproj").Select(Path.GetFileName).ToArray();

        found.ShouldNotContain("Stale.csproj");
        found.ShouldNotContain("Generated.csproj");
        found.ShouldNotContain("Bundled.csproj");
    }

    [Fact]
    public void EnumerateFiles_DoesNotFollowADirectoryLinkBackToAnAncestor()
    {
        // Reproduces the bun/pnpm workspace layout that made discovery recurse until the process
        // was killed: a linked package directory whose target is one of its own ancestors.
        CreateJunction(Path.Combine(_root, "App", "link-to-root"), _root);

        var found = RoslynWorkspace.EnumerateFiles(_root, "*.csproj").ToArray();

        found.ShouldHaveSingleItem();
        Path.GetFileName(found[0]).ShouldBe("App.csproj");
    }

    [Fact]
    public void EnumerateFiles_FailsWhenTheWalkOutlivesItsTimeout()
    {
        var ex = Should.Throw<TimeoutException>(
            () => RoslynWorkspace.EnumerateFiles(_root, "*.csproj", TimeSpan.Zero).ToArray());

        ex.Message.ShouldContain("directory cycle");
    }

    private void WriteProject(string relativeDir, string projectName)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, projectName), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    /// <summary>
    /// Junctions are reparse points like symlinks, but creating one needs no privilege, so the
    /// test runs the same on a developer machine and on CI.
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start cmd.exe to create a junction.");
        var error = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J '{link}' -> '{target}' failed: {error.Trim()}");
    }
}
