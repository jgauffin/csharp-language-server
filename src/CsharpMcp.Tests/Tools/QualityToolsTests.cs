using ArchiMetrics.Analysis;
using CsharpMcp.CodeAnalysis;
using CsharpMcp.CodeAnalysis.Tools;
using Shouldly;

namespace CsharpMcp.Tests.Tools;

public class QualityToolsTests : WorkspaceFixture
{
    [Fact]
    public void CompareSnapshots_NoChanges_ReturnsEmptyDeltas()
    {
        var metrics = new Dictionary<string, TypeMetricEntry>
        {
            ["Ns.Foo"] = new("Ns.Foo", 75.0, 10, 100, 5, 0.8),
            ["Ns.Bar"] = new("Ns.Bar", 60.0, 20, 200, 10, 0.9),
        };
        var before = new QualitySnapshot(DateTime.Now.AddMinutes(-10), 1, 5, metrics);
        var after = new QualitySnapshot(DateTime.Now, 1, 5, new Dictionary<string, TypeMetricEntry>(metrics));

        var result = QualityTools.CompareSnapshots(before, after);

        result.Improved.ShouldBeEmpty();
        result.Degraded.ShouldBeEmpty();
        result.NewTypes.ShouldBeEmpty();
        result.RemovedTypes.ShouldBeEmpty();
    }

    [Fact]
    public void CompareSnapshots_ImprovedType_ClassifiedCorrectly()
    {
        var before = new QualitySnapshot(DateTime.Now.AddMinutes(-10), 0, 0,
            new Dictionary<string, TypeMetricEntry>
            {
                ["Ns.Foo"] = new("Ns.Foo", 60.0, 15, 200, 10, 0.8),
            });
        var after = new QualitySnapshot(DateTime.Now, 0, 0,
            new Dictionary<string, TypeMetricEntry>
            {
                ["Ns.Foo"] = new("Ns.Foo", 75.0, 8, 120, 5, 0.7),
            });

        var result = QualityTools.CompareSnapshots(before, after);

        result.Improved.Count.ShouldBe(1);
        result.Improved[0].FullName.ShouldBe("Ns.Foo");
        result.Improved[0].MIDelta.ShouldBe(15.0);
        result.Improved[0].CCDelta.ShouldBe(-7);
        result.Degraded.ShouldBeEmpty();
    }

    [Fact]
    public void CompareSnapshots_DegradedType_ClassifiedCorrectly()
    {
        var before = new QualitySnapshot(DateTime.Now.AddMinutes(-10), 0, 0,
            new Dictionary<string, TypeMetricEntry>
            {
                ["Ns.Foo"] = new("Ns.Foo", 80.0, 5, 50, 3, 0.5),
            });
        var after = new QualitySnapshot(DateTime.Now, 0, 0,
            new Dictionary<string, TypeMetricEntry>
            {
                ["Ns.Foo"] = new("Ns.Foo", 65.0, 12, 150, 8, 0.9),
            });

        var result = QualityTools.CompareSnapshots(before, after);

        result.Degraded.Count.ShouldBe(1);
        result.Degraded[0].MIDelta.ShouldBe(-15.0);
        result.Degraded[0].CCDelta.ShouldBe(7);
        result.Improved.ShouldBeEmpty();
    }

    [Fact]
    public void CompareSnapshots_NewAndRemovedTypes_DetectedCorrectly()
    {
        var before = new QualitySnapshot(DateTime.Now.AddMinutes(-10), 0, 0,
            new Dictionary<string, TypeMetricEntry>
            {
                ["Ns.Old"] = new("Ns.Old", 70.0, 10, 100, 5, 0.8),
            });
        var after = new QualitySnapshot(DateTime.Now, 0, 0,
            new Dictionary<string, TypeMetricEntry>
            {
                ["Ns.New"] = new("Ns.New", 85.0, 3, 40, 2, 0.5),
            });

        var result = QualityTools.CompareSnapshots(before, after);

        result.NewTypes.Count.ShouldBe(1);
        result.NewTypes[0].FullName.ShouldBe("Ns.New");
        result.RemovedTypes.Count.ShouldBe(1);
        result.RemovedTypes[0].FullName.ShouldBe("Ns.Old");
    }

    [Fact]
    public void CompareSnapshots_DiagnosticDeltas_Tracked()
    {
        var before = new QualitySnapshot(DateTime.Now.AddMinutes(-10), 3, 12,
            new Dictionary<string, TypeMetricEntry>());
        var after = new QualitySnapshot(DateTime.Now, 1, 10,
            new Dictionary<string, TypeMetricEntry>());

        var result = QualityTools.CompareSnapshots(before, after);

        result.Before.ErrorCount.ShouldBe(3);
        result.After.ErrorCount.ShouldBe(1);
        result.Before.WarningCount.ShouldBe(12);
        result.After.WarningCount.ShouldBe(10);
    }

    [Fact]
    public void Format_QualitySnapshot_ContainsKeyInfo()
    {
        var snapshot = new QualitySnapshot(
            new DateTime(2026, 3, 10, 14, 30, 0), 2, 8,
            new Dictionary<string, TypeMetricEntry>
            {
                ["Ns.A"] = new("Ns.A", 70, 10, 100, 5, 0.8),
                ["Ns.B"] = new("Ns.B", 80, 5, 50, 3, 0.5),
            });

        var text = TextFormatter.Format(snapshot);

        text.ShouldContain("2026-03-10 14:30:00");
        text.ShouldContain("Errors: 2");
        text.ShouldContain("Warnings: 8");
        text.ShouldContain("Types measured: 2");
    }

    [Fact]
    public void Format_QualityComparison_ShowsDeltas()
    {
        var before = new QualitySnapshot(new DateTime(2026, 3, 10, 14, 0, 0), 3, 12,
            new Dictionary<string, TypeMetricEntry>
            {
                ["Ns.Foo"] = new("Ns.Foo", 60.0, 15, 200, 10, 0.8),
            });
        var after = new QualitySnapshot(new DateTime(2026, 3, 10, 15, 0, 0), 1, 10,
            new Dictionary<string, TypeMetricEntry>
            {
                ["Ns.Foo"] = new("Ns.Foo", 75.0, 8, 120, 5, 0.7),
            });
        var comparison = QualityTools.CompareSnapshots(before, after);

        var text = TextFormatter.Format(comparison);

        text.ShouldContain("Quality Report");
        text.ShouldContain("Errors: 3 -> 1 (-2)");
        text.ShouldContain("Warnings: 12 -> 10 (-2)");
        text.ShouldContain("Improved (1)");
        text.ShouldContain("Ns.Foo");
        // MI values formatted with current culture (may use comma or dot)
        text.ShouldContain("MI:");
        text.ShouldContain("CC: 15 -> 8 (-7)");
    }

    [Fact]
    public void Format_GitFallbackReport_ShowsChangedFiles()
    {
        var report = new GitFallbackReport(
            ["src/Foo.cs", "src/Bar.cs"],
            [new DiagnosticsTools.DiagnosticEntry("CS0168", "Warning", "unused var", "src/Foo.cs", 10, 5)],
            [new TypeMetricEntry("Ns.Foo", 65.0, 12, 150, 8, 0.9)]);

        var text = TextFormatter.Format(report);

        text.ShouldContain("No quality snapshot found");
        text.ShouldContain("Changed files: 2");
        text.ShouldContain("CS0168");
        text.ShouldContain("Ns.Foo");
        text.ShouldContain("MI:");
    }

    [Fact]
    public void Format_GitFallbackReport_EmptyChanges()
    {
        var report = new GitFallbackReport([], [], []);

        var text = TextFormatter.Format(report);

        text.ShouldContain("No changed .cs files detected");
    }

    [Fact]
    public async Task CaptureSnapshot_ReturnsValidSnapshot()
    {
        var agent = new CodeAnalysisAgent(Workspace.InnerWorkspace, FixturePath);

        var snapshot = await QualityTools.CaptureSnapshotAsync(Workspace.Solution, agent);

        snapshot.TypeMetrics.Count.ShouldBeGreaterThan(0);
        snapshot.CapturedAt.ShouldBeGreaterThan(DateTime.MinValue);
    }

    [Fact]
    public async Task FullFlow_SnapshotThenReport_ProducesComparison()
    {
        var agent = new CodeAnalysisAgent(Workspace.InnerWorkspace, FixturePath);

        var before = await QualityTools.CaptureSnapshotAsync(Workspace.Solution, agent);
        var after = await QualityTools.CaptureSnapshotAsync(Workspace.Solution, agent);
        var comparison = QualityTools.CompareSnapshots(before, after);

        // Same code, no changes expected
        comparison.Improved.ShouldBeEmpty();
        comparison.Degraded.ShouldBeEmpty();
        comparison.NewTypes.ShouldBeEmpty();
        comparison.RemovedTypes.ShouldBeEmpty();

        // Format should not throw
        var text = TextFormatter.Format(comparison);
        text.ShouldContain("Quality Report");
    }
}
