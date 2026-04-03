using ArchiMetrics.Analysis;
using ArchiMetrics.Analysis.Common.CodeReview;
using ArchiMetrics.CodeReview.Rules;
using CsharpMcp.CodeAnalysis;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class Iso5055ToolsTests : WorkspaceFixture
{
    [Fact]
    public async Task GenerateIso5055Report_ReturnsReport()
    {
        var agent = new CodeAnalysisAgent(Workspace.InnerWorkspace, FixturePath);
        var syntaxRules = AllRules.GetSyntaxRules(spellChecker: null);
        var symbolRules = AllRules.GetSymbolRules();
        var inspector = new NodeReviewer(syntaxRules, symbolRules);
        var allRules = syntaxRules.Cast<IEvaluation>().Concat(symbolRules);

        var report = await agent.GenerateIso5055Report(inspector, allRules);

        ((int)report.TotalLinesOfCode).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task GenerateIso5055Report_CoversCweIds()
    {
        var agent = new CodeAnalysisAgent(Workspace.InnerWorkspace, FixturePath);
        var syntaxRules = AllRules.GetSyntaxRules(spellChecker: null);
        var symbolRules = AllRules.GetSymbolRules();
        var inspector = new NodeReviewer(syntaxRules, symbolRules);
        var allRules = syntaxRules.Cast<IEvaluation>().Concat(symbolRules);

        var report = await agent.GenerateIso5055Report(inspector, allRules);

        var cweIds = (IEnumerable<string>)report.CoveredCweIds;
        cweIds.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GenerateIso5055Report_HasCategories()
    {
        var agent = new CodeAnalysisAgent(Workspace.InnerWorkspace, FixturePath);
        var syntaxRules = AllRules.GetSyntaxRules(spellChecker: null);
        var symbolRules = AllRules.GetSymbolRules();
        var inspector = new NodeReviewer(syntaxRules, symbolRules);
        var allRules = syntaxRules.Cast<IEvaluation>().Concat(symbolRules);

        var report = await agent.GenerateIso5055Report(inspector, allRules);

        // All four ISO 5055 quality characteristics should be present
        ((object)report.Security).ShouldNotBeNull();
        ((object)report.Reliability).ShouldNotBeNull();
        ((object)report.PerformanceEfficiency).ShouldNotBeNull();
        ((object)report.Maintainability).ShouldNotBeNull();
    }

    [Fact]
    public async Task GenerateIso5055Report_DetectsViolations()
    {
        var agent = new CodeAnalysisAgent(Workspace.InnerWorkspace, FixturePath);
        var syntaxRules = AllRules.GetSyntaxRules(spellChecker: null);
        var symbolRules = AllRules.GetSymbolRules();
        var inspector = new NodeReviewer(syntaxRules, symbolRules);
        var allRules = syntaxRules.Cast<IEvaluation>().Concat(symbolRules);

        var report = await agent.GenerateIso5055Report(inspector, allRules);

        // The Vulnerabilities.cs fixture should trigger at least some violations
        var totalViolations = (int)report.Security.ViolationCount
                            + (int)report.Reliability.ViolationCount
                            + (int)report.PerformanceEfficiency.ViolationCount
                            + (int)report.Maintainability.ViolationCount;
        totalViolations.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task FormatIso5055Report_ProducesReadableOutput()
    {
        var agent = new CodeAnalysisAgent(Workspace.InnerWorkspace, FixturePath);
        var syntaxRules = AllRules.GetSyntaxRules(spellChecker: null);
        var symbolRules = AllRules.GetSymbolRules();
        var inspector = new NodeReviewer(syntaxRules, symbolRules);
        var allRules = syntaxRules.Cast<IEvaluation>().Concat(symbolRules);

        var report = await agent.GenerateIso5055Report(inspector, allRules);
        var text = TextFormatter.FormatIso5055Report(report);

        text.ShouldContain("ISO 5055 Report");
        text.ShouldContain("LOC:");
        text.ShouldContain("Security");
        text.ShouldContain("Reliability");
        text.ShouldContain("Performance Efficiency");
        text.ShouldContain("Maintainability");
        text.ShouldContain("Violations:");
        text.ShouldContain("Per KLOC:");
        text.ShouldContain("Pass:");
    }
}
