using Shouldly;

namespace CsharpMcp.Tests;

/// <summary>
/// The workspace root is bound to the launch directory once, at parse time. Re-reading the
/// process working directory later would silently repoint the workspace.
/// </summary>
public class ServerConfigTests : IDisposable
{
    private readonly string _launchDir;

    public ServerConfigTests()
    {
        _launchDir = Path.Combine(Path.GetTempPath(), "CsharpMcpConfigTests_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(_launchDir, "sub"));
        Directory.CreateDirectory(Path.Combine(_launchDir, "siblings"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_launchDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Root_DefaultsToLaunchDirectory()
    {
        var config = ServerConfig.Parse([], _launchDir);

        config.RootPath.ShouldBe(Path.GetFullPath(_launchDir));
    }

    [Fact]
    public void Root_IsOverriddenByRootArgument()
    {
        var expected = Path.Combine(_launchDir, "sub");

        var config = ServerConfig.Parse(["--root", expected], _launchDir);

        config.RootPath.ShouldBe(Path.GetFullPath(expected));
    }

    [Fact]
    public void Root_IsOverriddenByPositionalDirectory()
    {
        var expected = Path.Combine(_launchDir, "sub");

        var config = ServerConfig.Parse([expected], _launchDir);

        config.RootPath.ShouldBe(Path.GetFullPath(expected));
    }

    [Fact]
    public void Root_ArgumentWinsOverPositionalDirectoryRegardlessOfOrder()
    {
        var expected = Path.GetFullPath(Path.Combine(_launchDir, "sub"));
        var positional = Path.Combine(_launchDir, "siblings");

        ServerConfig.Parse(["--root", expected, positional], _launchDir).RootPath.ShouldBe(expected);
        ServerConfig.Parse([positional, "--root", expected], _launchDir).RootPath.ShouldBe(expected);
    }

    [Fact]
    public void Root_ResolvesRelativePathsAgainstLaunchDirectoryNotProcessDirectory()
    {
        // "sub" exists under the launch directory but not under the test host's working directory,
        // so resolving against the process directory would fail outright.
        var config = ServerConfig.Parse(["--root", "sub"], _launchDir);

        config.RootPath.ShouldBe(Path.GetFullPath(Path.Combine(_launchDir, "sub")));
    }

    [Fact]
    public void AllowedDir_ResolvesRelativePathsAgainstLaunchDirectory()
    {
        var config = ServerConfig.Parse(["--root", "sub", "--allowed-dir", "siblings"], _launchDir);

        config.AllowedDir.ShouldBe(Path.GetFullPath(Path.Combine(_launchDir, "siblings")));
    }

    [Fact]
    public void AllowedDir_DefaultsToRoot()
    {
        var config = ServerConfig.Parse(["--root", "sub"], _launchDir);

        config.AllowedDir.ShouldBe(config.RootPath);
    }

    [Fact]
    public void Root_ThrowsWhenTheDirectoryDoesNotExist()
    {
        var missing = Path.Combine(_launchDir, "nope");

        Should.Throw<ArgumentException>(() => ServerConfig.Parse(["--root", missing], _launchDir));
    }
}
