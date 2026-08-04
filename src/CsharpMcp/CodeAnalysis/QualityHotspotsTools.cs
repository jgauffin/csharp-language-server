using System.ComponentModel;
using System.Text;
using ArchiMetrics.Analysis;
using ArchiMetrics.Analysis.Common.CodeReview;
using ArchiMetrics.Analysis.Common.Metrics;
using ArchiMetrics.CodeReview.Rules;
using CsharpMcp.CodeAnalysis.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CsharpMcp.CodeAnalysis;

[McpServerToolType]
public class QualityHotspotsTools(RoslynWorkspace workspace, CodeAnalysisAgent agent, ILogger<QualityHotspotsTools> logger)
{
    private static readonly Dictionary<string, QualitySnapshot> Snapshots = new(StringComparer.Ordinal);

    // Minimum per-dimension score (0-1, higher = worse) for an item to be included.
    private const double DefaultMinScore = 0.10;

    [McpServerTool, Description(
        "Find code hotspots that need refactoring. Weights three quality dimensions — maintainability (MI, CC, LOC), " +
        "duplication (code clones), and indirection (hidden coupling) — into a composite score. " +
        "Returns only items above the minimum score threshold. " +
        "Use snapshotLabel to capture a baseline before changes, then compareToSnapshot to see impact. " +
        "Metric values carry a health rating in brackets, [1 - Healthy] to [5 - Fix ASAP], where lower " +
        "is better; call metric_scales for the scales and band boundaries behind them.")]
    public async Task<string> quality_hotspots(
        [Description("Filter to a specific project (glob or substring)")] string? projectName = null,
        [Description("Weight for maintainability index (0.0-1.0, default 0.34)")] double maintainabilityWeight = 0.34,
        [Description("Weight for code duplication (0.0-1.0, default 0.33)")] double duplicationWeight = 0.33,
        [Description("Weight for indirection/coupling (0.0-1.0, default 0.33)")] double indirectionWeight = 0.33,
        [Description("Minimum composite score to include (0.0-1.0, default 0.10). Filters out average-or-better code.")] double minScore = DefaultMinScore,
        [Description("Capture or reference a named snapshot for before/after comparison")] string? snapshotLabel = null,
        [Description("Compare current state against this snapshot label")] string? compareToSnapshot = null,
        int skip = 0,
        int take = 50)
    {
        logger.LogInformation("Tool quality_hotspots invoked");
        if (!workspace.IsReady)
            return $"{workspace.LoadingStatus}. Please retry in a moment.";
        try
        {
            // Capture solution once so all sub-tasks share the same snapshot
            // (avoids redundant compilations from diverging snapshots)
            var solution = workspace.Solution;

            // Handle snapshot comparison mode
            if (compareToSnapshot != null)
            {
                if (!Snapshots.TryGetValue(compareToSnapshot, out var baseline))
                    return $"Unknown snapshot label '{compareToSnapshot}'. Call quality_hotspots with snapshotLabel first.";
                var current = await QualityTools.CaptureSnapshotAsync(solution, agent, workspace.GetCompilationAsync);
                return TextFormatter.Format(QualityTools.CompareSnapshots(baseline, current));
            }

            // Handle snapshot capture mode
            if (snapshotLabel != null)
            {
                var snapshot = await QualityTools.CaptureSnapshotAsync(solution, agent, workspace.GetCompilationAsync);
                Snapshots[snapshotLabel] = snapshot;
                return TextFormatter.FormatSnapshot(snapshotLabel, snapshot);
            }

            // Normalize weights
            var totalWeight = maintainabilityWeight + duplicationWeight + indirectionWeight;
            if (totalWeight <= 0) totalWeight = 1.0;
            var mw = maintainabilityWeight / totalWeight;
            var dw = duplicationWeight / totalWeight;
            var iw = indirectionWeight / totalWeight;

            // Run all analyses in parallel. Each dimension is independent — a failure in one
            // shouldn't nuke the whole tool.
            var metricsTask = SafeDimension(() => agent.GetWorstTypes(solution, projectName: projectName, skip: 0, take: 200), "maintainability");
            var duplicationTask = SafeDimension(() => agent.DetectDuplication(solution, projectName: projectName, skip: 0, take: 200), "duplication");
            var indirectionTask = SafeDimension(() => IndirectionTools.FindIndirectionHotspotsAsync(solution, projectName, skip: 0, take: 200), "indirection");

            await Task.WhenAll(metricsTask, duplicationTask, indirectionTask);

            var metrics = metricsTask.Result;
            var duplication = duplicationTask.Result;
            var indirection = indirectionTask.Result;

            // Build hotspot entries keyed by identifier
            var hotspots = new Dictionary<string, HotspotEntry>(StringComparer.OrdinalIgnoreCase);

            // 1. Maintainability: invert MI onto the composite's 0-1 axis, where 1 = worst.
            // The inversion is a ranking device only — whether a value is actually bad is the
            // library's call, via MetricThresholds, so that this tool and the review rules cannot
            // disagree about the same type.
            if (metrics?.Items.Count > 0)
            {
                foreach (var t in metrics.Items)
                {
                    var score = 1.0 - Math.Clamp(
                        (t.MaintainabilityIndex - MetricThresholds.Maintainability.Minimum) /
                        (MetricThresholds.Maintainability.Maximum - MetricThresholds.Maintainability.Minimum), 0, 1);
                    if (score < minScore) continue;
                    var key = $"{t.NamespaceName}.{t.Name}";
                    var entry = GetOrCreate(hotspots, key, t.Kind.ToString());
                    entry.MaintainabilityScore = score;
                    var rating = MetricThresholds.Label(MetricThresholds.RateMaintainability(t.MaintainabilityIndex));
                    entry.Details.Add(
                        $"MI:{t.MaintainabilityIndex:F0} [{rating}] CC:{t.CyclomaticComplexity} " +
                        $"LOC:{t.LinesOfCode} Stmts:{t.ExecutableStatements} " +
                        $"Efferent:{t.EfferentCoupling} ClassCoupling:{t.ClassCoupling}");
                }
            }

            // 2. Duplication: score by number of instances in clone groups
            if (duplication?.Items.Count > 0)
            {
                foreach (var clone in duplication.Items)
                {
                    // More instances and higher similarity = worse
                    var score = Math.Clamp(clone.Instances.Count / 10.0 * clone.Similarity, 0, 1);
                    if (score < minScore) continue;
                    foreach (var inst in clone.Instances)
                    {
                        var key = inst.MemberName ?? $"{inst.FilePath}:{inst.LineNumber}";
                        var entry = GetOrCreate(hotspots, key, "Clone");
                        entry.FilePath ??= inst.FilePath;
                        entry.Line ??= inst.LineNumber;
                        entry.DuplicationScore = Math.Max(entry.DuplicationScore, score);
                        entry.Details.Add($"Clone({clone.CloneType}, sim:{clone.Similarity:F2}, instances:{clone.Instances.Count})");
                    }
                }
            }

            // 3. Indirection: normalize by max score in set
            if (indirection?.Items.Count > 0)
            {
                var maxIndirectionScore = indirection.Items.Max(o => o.Score);
                if (maxIndirectionScore <= 0) maxIndirectionScore = 1;
                foreach (var o in indirection.Items)
                {
                    var score = Math.Clamp(o.Score / maxIndirectionScore, 0, 1);
                    if (score < minScore) continue;
                    var key = $"{o.ContainingType}.{o.SymbolName}";
                    var entry = GetOrCreate(hotspots, key, o.SymbolKind);
                    entry.FilePath ??= o.Location.FilePath;
                    entry.Line ??= o.Location.Line;
                    entry.IndirectionScore = score;
                    entry.Details.Add($"Indirection score:{o.Score:F1} chains:{o.TotalChainCount} maxDepth:{o.MaxChainDepth}");
                }
            }

            // Compute composite scores, filter by threshold, and sort
            foreach (var entry in hotspots.Values)
            {
                entry.CompositeScore =
                    entry.MaintainabilityScore * mw +
                    entry.DuplicationScore * dw +
                    entry.IndirectionScore * iw;
            }

            var ranked = hotspots.Values
                .Where(e => e.CompositeScore >= minScore)
                .OrderByDescending(e => e.CompositeScore)
                .Skip(skip)
                .Take(take)
                .ToList();

            var filteredOutCount = hotspots.Count - hotspots.Values.Count(e => e.CompositeScore >= minScore);

            return FormatHotspots(ranked, ranked.Count + filteredOutCount, filteredOutCount, minScore, mw, dw, iw);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool quality_hotspots failed");
            return $"Error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Explain the metric scales used by the quality tools: what each metric measures, which " +
        "direction is better, and the band boundaries behind the [1-5] health ratings. " +
        "Call this when a metric value needs interpreting; the reports themselves omit the legend " +
        "to keep responses small.")]
    public string metric_scales()
    {
        logger.LogInformation("Tool metric_scales invoked");
        // Straight from the library, never a copy: the bands it rates against and the bands it
        // describes here have to be the same ones.
        //
        // ClassCoupling is appended here rather than taken from the library because the library
        // does not rate it and so does not describe it — but the reports print it, and a reader
        // who sees a high ClassCoupling next to a mild rating needs to be told that the two are
        // unrelated. Without this line the legend reads as though every printed number is rated.
        return MetricThresholds.DescribeScales() + Environment.NewLine +
               "  ClassCoupling: distinct types referenced. Count only - no good or bad value, no " +
               "band, and NOT part of the rating. Efferent Coupling is the one the rating uses; a " +
               "type can show a high ClassCoupling and still rate well.";
    }

    [McpServerTool, Description("Generate an ISO 5055 automated source code quality report. Analyzes security, reliability, performance efficiency, and maintainability. Returns violation counts, violations per KLOC, pass/fail status, and covered CWE IDs with per-violation details.")]
    public async Task<string> generate_iso5055_report(
        [Description("Max violations to show per category (default 20). Summary counts always reflect the full analysis.")] int maxViolationsPerCategory = 20)
    {
        logger.LogInformation("Tool generate_iso5055_report invoked");
        if (!workspace.IsReady)
            return $"{workspace.LoadingStatus}. Please retry in a moment.";
        try
        {
            var syntaxRules = AllRules.GetSyntaxRules(spellChecker: null);
            var symbolRules = AllRules.GetSymbolRules();
            var inspector = new NodeReviewer(syntaxRules, symbolRules);
            var allRules = syntaxRules.Cast<IEvaluation>().Concat(symbolRules);
            // Pass solution explicitly so it shares the same snapshot (and compilations)
            // as other tools running in parallel, instead of falling back to the
            // stale MSBuildWorkspace.CurrentSolution.
            var result = await agent.GenerateIso5055Report(inspector, allRules, workspace.Solution);
            return TextFormatter.FormatIso5055Report(result, maxViolationsPerCategory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool generate_iso5055_report failed");
            return $"Error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private async Task<T?> SafeDimension<T>(Func<Task<T>> action, string dimension) where T : class
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quality dimension '{Dimension}' failed and will be skipped", dimension);
            return null;
        }
    }

    private static HotspotEntry GetOrCreate(Dictionary<string, HotspotEntry> map, string key, string kind)
    {
        if (!map.TryGetValue(key, out var entry))
        {
            entry = new HotspotEntry { Name = key, Kind = kind };
            map[key] = entry;
        }
        return entry;
    }

    private static string FormatHotspots(List<HotspotEntry> ranked, int totalCount, int filteredOut, double minScore, double mw, double dw, double iw)
    {
        if (ranked.Count == 0)
            return filteredOut > 0
                ? $"No hotspots above threshold {minScore:F2}. ({filteredOut} item(s) filtered out as average-or-better quality.)"
                : "No quality hotspots found.";

        var sb = new StringBuilder();
        sb.Append("Showing ").Append(ranked.Count).Append(" of ").Append(totalCount)
          .Append(" hotspot(s), ranked by composite score");
        if (filteredOut > 0)
            sb.Append(" (").Append(filteredOut).Append(" below threshold ").Append(minScore.ToString("F2")).Append(')');
        sb.AppendLine(":");
        sb.Append("Weights: Maint=").Append(mw.ToString("F2"))
          .Append(" Dup=").Append(dw.ToString("F2"))
          .Append(" Indirection=").Append(iw.ToString("F2"))
          .AppendLine();
        sb.AppendLine("Scores run 0.00-1.00 where higher is worse — the opposite of the raw MI on each detail line.");
        sb.AppendLine();

        foreach (var e in ranked)
        {
            sb.Append(e.Name).Append(" (").Append(e.Kind).Append(')');
            if (e.FilePath != null)
                sb.Append("  ").Append(e.FilePath).Append(':').Append(e.Line);
            sb.AppendLine();
            sb.Append("  Score: ").Append(e.CompositeScore.ToString("F3"));
            // "Maint" rather than "MI": this is the inverted 0-1 sub-score, which runs the opposite
            // way from the raw MI printed on the detail line below it.
            if (e.MaintainabilityScore > 0) sb.Append("  Maint: ").Append(e.MaintainabilityScore.ToString("F2"));
            if (e.DuplicationScore > 0) sb.Append("  Dup: ").Append(e.DuplicationScore.ToString("F2"));
            if (e.IndirectionScore > 0) sb.Append("  Indirection: ").Append(e.IndirectionScore.ToString("F2"));
            sb.AppendLine();
            foreach (var detail in e.Details)
                sb.Append("  ").AppendLine(detail);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private class HotspotEntry
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? FilePath { get; set; }
        public int? Line { get; set; }
        public double CompositeScore { get; set; }
        public double MaintainabilityScore { get; set; }
        public double DuplicationScore { get; set; }
        public double IndirectionScore { get; set; }
        public List<string> Details { get; } = new();
    }
}
