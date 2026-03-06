using CsharpMcp.CodeAnalysis.Tools;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class SemanticSearchToolsTests : WorkspaceFixture
{
    [Fact]
    public async Task Find_ByName_FindsSymbolsAcrossProjects()
    {
        var results = await SemanticSearchTools.FindAsync(Workspace.Solution, "Calculator");

        results.ShouldNotBeEmpty();
        results.ShouldContain(r => r.Name == "Calculator" && r.FilePath.EndsWith("Calculator.cs"));
    }

    [Fact]
    public async Task Find_WithKindFilter_ReturnsOnlyMatchingKind()
    {
        var results = await SemanticSearchTools.FindAsync(
            Workspace.Solution, "Add", kind: "Method");

        results.ShouldNotBeEmpty();
        results.ShouldAllBe(r => r.Kind == "Method");
    }

    [Fact]
    public async Task Find_WithProjectScope_LimitsToProject()
    {
        var results = await SemanticSearchTools.FindAsync(
            Workspace.Solution, "Dog", projectName: "LibB");

        results.ShouldNotBeEmpty();
        results.ShouldAllBe(r => r.FilePath.Contains("LibB"));
    }

    [Fact]
    public async Task GetWorkspaceSymbols_PartialName_FindsMatches()
    {
        var results = await SemanticSearchTools.GetWorkspaceSymbolsAsync(
            Workspace.Solution, "Anim");

        results.ShouldNotBeEmpty();
        results.ShouldContain(r => r.Name.Contains("Animal"));
    }
}
