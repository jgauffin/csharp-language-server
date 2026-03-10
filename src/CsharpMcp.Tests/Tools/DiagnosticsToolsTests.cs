using CsharpMcp.CodeAnalysis.Tools;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class DiagnosticsToolsTests : WorkspaceFixture
{
    [Fact]
    public async Task GetDiagnostics_CleanFile_ReturnsNoErrors()
    {
        var diags = await DiagnosticsTools.GetDiagnosticsAsync(
            Workspace.Solution, FilePath("LibA", "Calculator.cs"));

        diags.ShouldNotContain(d => d.Severity == "Error");
    }

    [Fact]
    public async Task GetAllDiagnostics_CleanSolution_ReturnsNoErrors()
    {
        var diags = await DiagnosticsTools.GetAllDiagnosticsAsync(Workspace.Solution);

        diags.Items.ShouldNotContain(d => d.Severity == "Error");
    }

    [Fact]
    public async Task GetAllDiagnostics_WithProjectScope_OnlyReturnsThatProject()
    {
        var diags = await DiagnosticsTools.GetAllDiagnosticsAsync(
            Workspace.Solution, projectName: "LibA");

        // All diagnostics should come from LibA files
        diags.Items.ShouldAllBe(d => d.FilePath.Contains("LibA"));
    }
}
