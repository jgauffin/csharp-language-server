using CsharpMcp;
using CsharpMcp.CodeAnalysis;
using Microsoft.Build.Locator;

namespace CsharpMcp.Tests;

/// <summary>
/// Loads the MultiProject test fixture once per test class via xunit IAsyncLifetime.
/// Each test class that needs a fresh workspace (e.g. rename) gets its own copy.
/// </summary>
public abstract class WorkspaceFixture : IAsyncLifetime
{
    protected RoslynWorkspace Workspace { get; private set; } = null!;

    // Path to the original fixture — tests that mutate must copy it first.
    protected static string FixturePath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestFixtures", "MultiProject"));

    static WorkspaceFixture()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    public virtual async Task InitializeAsync()
    {
        Workspace = await RoslynWorkspace.LoadAsync(FixturePath, projectFilter: _ => true);
    }

    public virtual Task DisposeAsync()
    {
        Workspace.Dispose();
        return Task.CompletedTask;
    }

    protected string FilePath(string project, string file) =>
        Path.Combine(FixturePath, project, file);
}
