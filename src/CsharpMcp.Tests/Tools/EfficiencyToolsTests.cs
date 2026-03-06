using CsharpMcp.CodeAnalysis;
using CsharpMcp.CodeAnalysis.Tools;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class EfficiencyToolsTests : WorkspaceFixture
{
    [Fact]
    public async Task AnalyzePosition_ReturnsHoverDiagnosticsAndSymbols()
    {
        var pos = new Position(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16);

        var result = await EfficiencyTools.AnalyzePositionAsync(Workspace.Solution, pos);

        result.Hover.ShouldNotBeNull();
        result.Diagnostics.ShouldNotBeNull();
        result.FileSymbols.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task BatchAnalyze_MultiplePositions_ReturnsOneResultEach()
    {
        var positions = new List<Position>
        {
            new(FilePath("LibA", "Calculator.cs"), Line: 7, Column: 16),
            new(FilePath("LibA", "Animal.cs"), Line: 8, Column: 12),
        };

        var results = await EfficiencyTools.BatchAnalyzeAsync(Workspace.Solution, positions);

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.FileSymbols.Count > 0);
    }
}
