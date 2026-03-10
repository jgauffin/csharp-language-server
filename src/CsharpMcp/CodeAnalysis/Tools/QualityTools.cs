using System.Diagnostics;
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

public record GitFallbackReport(
    List<string> ChangedFiles,
    List<DiagnosticsTools.DiagnosticEntry> Diagnostics,
    List<TypeMetricEntry> TypeMetrics);

public static class QualityTools
{
    public static async Task<QualitySnapshot> CaptureSnapshotAsync(
        Solution solution, CodeAnalysisAgent agent)
    {
        var diagnosticsTask = DiagnosticsTools.GetAllDiagnosticsAsync(solution);
        var metricsTask = agent.GetWorstTypes(solution, skip: 0, take: 10000);

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

    public static async Task<List<string>> GetGitChangedCsFilesAsync(string rootPath)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Unstaged + staged changes vs HEAD
        await RunGitAndCollect(rootPath, "diff --name-only HEAD", files);
        // Untracked files
        await RunGitAndCollect(rootPath, "ls-files --others --exclude-standard", files);

        return files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static async Task RunGitAndCollect(string rootPath, string args, HashSet<string> files)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = rootPath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fullPath = Path.GetFullPath(Path.Combine(rootPath, line.Replace('/', Path.DirectorySeparatorChar)));
                files.Add(fullPath);
            }
        }
        catch
        {
            // git not available or not a repo — silently ignore
        }
    }

    public static async Task<GitFallbackReport> BuildGitFallbackReportAsync(
        Solution solution, CodeAnalysisAgent agent, string rootPath)
    {
        var changedFiles = await GetGitChangedCsFilesAsync(rootPath);

        if (changedFiles.Count == 0)
            return new GitFallbackReport([], [], []);

        // Get diagnostics for changed files
        var diagnostics = new List<DiagnosticsTools.DiagnosticEntry>();
        foreach (var file in changedFiles)
        {
            try
            {
                var fileDiags = await DiagnosticsTools.GetDiagnosticsAsync(solution, file);
                diagnostics.AddRange(fileDiags);
            }
            catch
            {
                // File might not be part of the solution
            }
        }

        // Get all type metrics and filter to types in changed files
        var allMetrics = await agent.GetWorstTypes(solution, skip: 0, take: 10000);
        var changedFileNames = new HashSet<string>(
            changedFiles.Select(Path.GetFileNameWithoutExtension).Where(n => n is not null)!,
            StringComparer.OrdinalIgnoreCase);

        // We can't directly map types to files via ArchiMetrics, so get all metrics
        // and return them — the report focuses on diagnostics per changed file
        var typeMetrics = allMetrics.Items
            .Select(t => new TypeMetricEntry(
                $"{t.NamespaceName}.{t.Name}",
                t.MaintainabilityIndex, t.CyclomaticComplexity,
                t.LinesOfCode, t.ClassCoupling, t.Instability))
            .ToList();

        return new GitFallbackReport(changedFiles, diagnostics, typeMetrics);
    }
}
