using ArchiMetrics.Analysis;
using Microsoft.CodeAnalysis;

namespace CsharpMcp.CodeAnalysis.Tools;

public record TypeMetricEntry(
    string FullName,
    double MI,
    int CC,
    int LOC,
    int Coupling,
    double Instability);

public record QualitySnapshot(
    DateTime CapturedAt,
    int ErrorCount,
    int WarningCount,
    Dictionary<string, TypeMetricEntry> TypeMetrics);

public record TypeDelta(
    string FullName,
    double MIDelta,
    int CCDelta,
    int LOCDelta,
    int CouplingDelta);

public record QualityComparison(
    QualitySnapshot Before,
    QualitySnapshot After,
    List<TypeDelta> Improved,
    List<TypeDelta> Degraded,
    List<TypeMetricEntry> NewTypes,
    List<TypeMetricEntry> RemovedTypes);

public static class QualityTools
{
    public static async Task<QualitySnapshot> CaptureSnapshotAsync(
        Solution solution, CodeAnalysisAgent agent, Func<Project, Task<Compilation?>>? getCompilation = null)
    {
        var diagnosticsTask = DiagnosticsTools.GetAllDiagnosticsAsync(solution, getCompilation: getCompilation);
        var metricsTask = agent.GetWorstTypes(solution, skip: 0, take: 50);

        await Task.WhenAll(diagnosticsTask, metricsTask);

        var diag = diagnosticsTask.Result;
        var metrics = metricsTask.Result;

        var errorCount = diag.Items.Count(d => d.Severity == "Error");
        var warningCount = diag.Items.Count(d => d.Severity == "Warning");

        var typeMetrics = new Dictionary<string, TypeMetricEntry>(StringComparer.Ordinal);
        foreach (var t in metrics.Items)
        {
            var fullName = $"{t.NamespaceName}.{t.Name}";
            typeMetrics.TryAdd(fullName, new TypeMetricEntry(
                fullName, t.MaintainabilityIndex, t.CyclomaticComplexity,
                t.LinesOfCode, t.ClassCoupling, t.Instability));
        }

        return new QualitySnapshot(DateTime.Now, errorCount, warningCount, typeMetrics);
    }

    public static QualityComparison CompareSnapshots(QualitySnapshot before, QualitySnapshot after)
    {
        var improved = new List<TypeDelta>();
        var degraded = new List<TypeDelta>();
        var newTypes = new List<TypeMetricEntry>();
        var removedTypes = new List<TypeMetricEntry>();

        var beforeKeys = new HashSet<string>(before.TypeMetrics.Keys, StringComparer.Ordinal);
        var afterKeys = new HashSet<string>(after.TypeMetrics.Keys, StringComparer.Ordinal);

        // Types in both snapshots
        foreach (var key in beforeKeys.Intersect(afterKeys))
        {
            var b = before.TypeMetrics[key];
            var a = after.TypeMetrics[key];

            var miDelta = a.MI - b.MI;
            var ccDelta = a.CC - b.CC;
            var locDelta = a.LOC - b.LOC;
            var couplingDelta = a.Coupling - b.Coupling;

            if (Math.Abs(miDelta) < 0.1 && ccDelta == 0 && locDelta == 0 && couplingDelta == 0)
                continue; // no meaningful change

            var delta = new TypeDelta(key, miDelta, ccDelta, locDelta, couplingDelta);

            // Classify by MI direction (higher MI = better)
            if (miDelta > 0.1)
                improved.Add(delta);
            else if (miDelta < -0.1)
                degraded.Add(delta);
            else if (ccDelta < 0)
                improved.Add(delta);
            else if (ccDelta > 0)
                degraded.Add(delta);
            else
                improved.Add(delta); // LOC/coupling improved with stable MI
        }

        // New types (in after but not before)
        foreach (var key in afterKeys.Except(beforeKeys))
            newTypes.Add(after.TypeMetrics[key]);

        // Removed types (in before but not after)
        foreach (var key in beforeKeys.Except(afterKeys))
            removedTypes.Add(before.TypeMetrics[key]);

        // Sort: worst degradation first, best improvement first
        improved.Sort((a, b) => b.MIDelta.CompareTo(a.MIDelta));
        degraded.Sort((a, b) => a.MIDelta.CompareTo(b.MIDelta));

        return new QualityComparison(before, after, improved, degraded, newTypes, removedTypes);
    }

}
