using System.Text;
using ArchiMetrics.Analysis;
using ArchiMetrics.Analysis.Common.Metrics;
using CsharpMcp.CodeAnalysis.Tools;

namespace CsharpMcp.CodeAnalysis;

public static class TextFormatter
{
    // Location-based formats

    public static string Format(Location? loc)
    {
        if (loc is null) return "No definition found.";
        var s = $"{loc.FilePath}:{loc.Line}:{loc.Column}";
        if (loc.Preview != null) s += $" {loc.Preview}";
        return s;
    }

    public static string FormatLocations(List<Location> locs)
    {
        if (locs.Count == 0) return "No results found.";
        var sb = new StringBuilder();
        foreach (var loc in locs)
            sb.AppendLine(Format(loc));
        return sb.ToString().TrimEnd();
    }

    // References

    public static string Format(List<NavigationTools.ReferenceResult> refs)
    {
        if (refs.Count == 0) return "No references found.";
        var sb = new StringBuilder();
        foreach (var r in refs)
        {
            sb.Append(r.Location.FilePath).Append(':').Append(r.Location.Line).Append(':').Append(r.Location.Column);
            if (r.IsWrite) sb.Append(" [write]");
            if (r.Location.Preview != null) sb.Append(' ').Append(r.Location.Preview);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Call hierarchy

    public static string Format(List<NavigationTools.CallInfo> calls)
    {
        if (calls.Count == 0) return "No call hierarchy found.";
        var sb = new StringBuilder();
        foreach (var c in calls)
        {
            sb.Append(c.Direction == NavigationTools.CallDirection.Caller ? "← " : "→ ");
            sb.Append(c.SymbolName).Append(' ');
            sb.Append(c.Location.FilePath).Append(':').Append(c.Location.Line).Append(':').Append(c.Location.Column);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Type hierarchy

    public static string Format(NavigationTools.TypeHierarchyResult? h)
    {
        if (h is null) return "No type found at position.";
        var sb = new StringBuilder();
        if (h.BaseType != null) sb.Append("Base: ").AppendLine(h.BaseType);
        sb.Append("Type: ").AppendLine(h.Name);
        if (h.Interfaces.Count > 0)
            sb.Append("  Implements: ").AppendLine(string.Join(", ", h.Interfaces));
        if (h.DerivedTypes.Count > 0)
            sb.Append("  Derived: ").AppendLine(string.Join(", ", h.DerivedTypes));
        return sb.ToString().TrimEnd();
    }

    // Hover

    public static string Format(TypeIntelligenceTools.HoverResult? hover)
    {
        if (hover is null) return "No symbol found at position.";
        var sb = new StringBuilder();
        sb.AppendLine(hover.DisplayString);
        sb.Append("Kind: ").AppendLine(hover.Kind);
        if (hover.ReturnType != null) sb.Append("Type: ").AppendLine(hover.ReturnType);
        if (hover.Documentation != null) sb.AppendLine(hover.Documentation);
        return sb.ToString().TrimEnd();
    }

    // Signature help

    public static string Format(TypeIntelligenceTools.ParameterHelp? sig)
    {
        if (sig is null) return "No signature found at position.";
        var sb = new StringBuilder();
        sb.AppendLine(sig.MethodSignature);
        foreach (var p in sig.Parameters)
        {
            sb.Append("  ").Append(p.Type).Append(' ').Append(p.Name);
            if (p.Documentation != null) sb.Append(" - ").Append(p.Documentation);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Symbols

    public static string Format(List<CodeStructureTools.SymbolEntry> symbols)
    {
        if (symbols.Count == 0) return "No symbols found.";
        var sb = new StringBuilder();
        foreach (var s in symbols)
        {
            sb.Append(s.Name).Append(' ').Append(s.Kind).Append(" :").Append(s.Line);
            if (!string.IsNullOrEmpty(s.ContainingType)) sb.Append(' ').Append(s.ContainingType);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Imports

    public static string Format(List<CodeStructureTools.ImportEntry> imports)
    {
        if (imports.Count == 0) return "No imports found.";
        var sb = new StringBuilder();
        foreach (var i in imports)
        {
            if (i.IsGlobal) sb.Append("global ");
            sb.Append("using ");
            if (i.IsStatic) sb.Append("static ");
            if (i.Alias != null) sb.Append(i.Alias).Append(" = ");
            sb.AppendLine(i.Name);
        }
        return sb.ToString().TrimEnd();
    }

    // Find / workspace symbols

    public static string Format(List<SemanticSearchTools.FindResult> results)
    {
        if (results.Count == 0) return "No symbols found.";
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.Append(r.Name).Append(' ').Append(r.Kind).Append(' ');
            sb.Append(r.FilePath).Append(':').Append(r.Line);
            if (!string.IsNullOrEmpty(r.ContainingType)) sb.Append(" (").Append(r.ContainingType).Append(')');
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Diagnostics

    public static string Format(List<DiagnosticsTools.DiagnosticEntry> diags)
    {
        if (diags.Count == 0) return "No diagnostics.";
        var sb = new StringBuilder();
        foreach (var d in diags)
            sb.Append(d.Id).Append(' ').Append(d.Severity).Append(' ')
              .Append(d.FilePath).Append(':').Append(d.Line).Append(':').Append(d.Column)
              .Append(' ').AppendLine(d.Message);
        return sb.ToString().TrimEnd();
    }

    public static string Format(DiagnosticsTools.DiagnosticPage result)
    {
        if (result.Items.Count == 0) return "No diagnostics.";
        var sb = new StringBuilder();
        sb.Append("Showing ").Append(result.Items.Count).Append(" of ").Append(result.TotalCount)
          .AppendLine(" diagnostic(s), sorted by severity:");
        sb.AppendLine();
        foreach (var d in result.Items)
            sb.Append(d.Id).Append(' ').Append(d.Severity).Append(' ')
              .Append(d.FilePath).Append(':').Append(d.Line).Append(':').Append(d.Column)
              .Append(' ').AppendLine(d.Message);
        return sb.ToString().TrimEnd();
    }

    // Rename

    public static string Format(RefactoringTools.RenamePreview preview)
    {
        var sb = new StringBuilder();
        sb.Append("Rename to: ").AppendLine(preview.NewName);
        foreach (var c in preview.Changes)
            sb.Append("  ").Append(c.FilePath).Append(':').Append(c.Line).Append(':').Append(c.Column)
              .Append(" \"").Append(c.OldText).Append("\" → \"").Append(c.NewText).AppendLine("\"");
        if (preview.AffectedFiles.Count > 0)
            sb.Append("Affected files: ").AppendLine(string.Join(", ", preview.AffectedFiles));
        return sb.ToString().TrimEnd();
    }

    // Completions

    public static string Format(List<CompletionTools.CompletionItem> items)
    {
        if (items.Count == 0) return "No completions available.";
        var sb = new StringBuilder();
        foreach (var c in items)
        {
            sb.Append(c.Kind).Append(' ').Append(c.Name);
            if (c.InsertText != null) sb.Append(" [insert: ").Append(c.InsertText).Append(']');
            if (c.Documentation != null) sb.Append(" - ").Append(c.Documentation);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Code actions

    public static string Format(List<CodeActionTools.CodeActionEntry> actions)
    {
        if (actions.Count == 0) return "No code actions available.";
        var sb = new StringBuilder();
        foreach (var a in actions)
        {
            sb.Append('[').Append(a.DiagnosticId).Append("] ").AppendLine(a.DiagnosticMessage);
            sb.Append("  Fix: ").AppendLine(a.Title);
            foreach (var c in a.Changes)
                sb.Append("    ").Append(c.FilePath).Append(':').Append(c.StartLine).Append(':').Append(c.StartColumn)
                  .Append(" \"").Append(c.OldText).Append("\" → \"").Append(c.NewText).AppendLine("\"");
        }
        return sb.ToString().TrimEnd();
    }

    // Project files

    public static string Format(List<ProjectTools.ProjectFileEntry> files)
    {
        if (files.Count == 0) return "No files found.";
        var sb = new StringBuilder();
        foreach (var f in files)
            sb.Append(f.ProjectName).Append(": ").AppendLine(f.FilePath);
        return sb.ToString().TrimEnd();
    }

    // Position analysis

    public static string Format(EfficiencyTools.PositionAnalysis analysis)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Hover");
        sb.AppendLine(Format(analysis.Hover));
        sb.AppendLine();
        sb.AppendLine("## Diagnostics");
        sb.AppendLine(Format(analysis.Diagnostics));
        sb.AppendLine();
        sb.AppendLine("## Symbols");
        sb.AppendLine(Format(analysis.FileSymbols));
        return sb.ToString().TrimEnd();
    }

    public static string Format(List<EfficiencyTools.PositionAnalysis> analyses)
    {
        if (analyses.Count == 0) return "No results.";
        var sb = new StringBuilder();
        for (int i = 0; i < analyses.Count; i++)
        {
            if (i > 0) sb.AppendLine().AppendLine("---");
            sb.AppendLine(Format(analyses[i]));
        }
        return sb.ToString().TrimEnd();
    }

    // Namespace metrics

    public static string Format(PagedResult<INamespaceMetric> result)
    {
        if (result.Items.Count == 0) return "No metrics available.";
        var sb = new StringBuilder();
        sb.Append("Showing ").Append(result.Items.Count).Append(" of ").Append(result.TotalCount)
          .AppendLine(" namespace(s), sorted by worst maintainability index:");
        sb.AppendLine();

        foreach (var ns in result.Items)
        {
            sb.Append("## ").AppendLine(ns.Name);
            sb.Append("LOC: ").Append(ns.LinesOfCode)
              .Append("  MI: ").Append(ns.MaintainabilityIndex.ToString("F1"))
              .Append("  CC: ").Append(ns.CyclomaticComplexity)
              .Append("  Abstractness: ").Append(ns.Abstractness.ToString("F2"))
              .Append("  DOI: ").Append(ns.DepthOfInheritance)
              .Append("  Coupling: ").Append(ns.ClassCoupling)
              .AppendLine();

            foreach (var type in ns.TypeMetrics)
            {
                sb.AppendLine();
                sb.Append("  ").Append(type.Name)
                  .Append(" (").Append(type.Kind).Append(", ").Append(type.AccessModifier).AppendLine(")");
                sb.Append("  LOC: ").Append(type.LinesOfCode)
                  .Append("  MI: ").Append(type.MaintainabilityIndex.ToString("F1"))
                  .Append("  CC: ").Append(type.CyclomaticComplexity)
                  .Append("  DOI: ").Append(type.DepthOfInheritance)
                  .Append("  Coupling: ").Append(type.ClassCoupling)
                  .Append("  Instability: ").Append(type.Instability.ToString("F2"));
                if (type.IsAbstract) sb.Append("  [abstract]");
                sb.AppendLine();

                foreach (var member in type.MemberMetrics)
                {
                    sb.Append("    ").Append(member.Name)
                      .Append(" (").Append(member.AccessModifier).Append(')');
                    if (member.CodeFile != null)
                        sb.Append(' ').Append(member.CodeFile).Append(':').Append(member.LineNumber);
                    sb.AppendLine();
                    sb.Append("    LOC: ").Append(member.LinesOfCode)
                      .Append("  MI: ").Append(member.MaintainabilityIndex.ToString("F1"))
                      .Append("  CC: ").Append(member.CyclomaticComplexity)
                      .Append("  Coupling: ").Append(member.ClassCoupling)
                      .Append("  Params: ").Append(member.NumberOfParameters)
                      .Append("  Locals: ").Append(member.NumberOfLocalVariables)
                      .AppendLine();
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    // Namespace summaries (flat)

    public static string Format(PagedResult<NamespaceSummary> result)
    {
        if (result.Items.Count == 0) return "No metrics available.";
        var sb = new StringBuilder();
        sb.Append("Showing ").Append(result.Items.Count).Append(" of ").Append(result.TotalCount)
          .AppendLine(" namespace(s), sorted by worst maintainability index:");
        sb.AppendLine();
        foreach (var ns in result.Items)
        {
            sb.Append(ns.Name)
              .Append("  MI: ").Append(ns.MaintainabilityIndex.ToString("F1"))
              .Append("  CC: ").Append(ns.CyclomaticComplexity)
              .Append("  LOC: ").Append(ns.LinesOfCode)
              .Append("  Abstractness: ").Append(ns.Abstractness.ToString("F2"))
              .Append("  DOI: ").Append(ns.DepthOfInheritance)
              .Append("  Coupling: ").Append(ns.ClassCoupling)
              .Append("  Types: ").Append(ns.TypeCount)
              .AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Type summaries (flat)

    public static string Format(PagedResult<TypeSummary> result)
    {
        if (result.Items.Count == 0) return "No metrics available.";
        var sb = new StringBuilder();
        sb.Append("Showing ").Append(result.Items.Count).Append(" of ").Append(result.TotalCount)
          .AppendLine(" type(s), sorted by worst maintainability index:");
        sb.AppendLine();
        foreach (var t in result.Items)
        {
            sb.Append(t.NamespaceName).Append('.').Append(t.Name)
              .Append(" (").Append(t.Kind).Append(", ").Append(t.AccessModifier).Append(')');
            if (t.IsAbstract) sb.Append(" [abstract]");
            sb.AppendLine();
            sb.Append("  MI: ").Append(t.MaintainabilityIndex.ToString("F1"))
              .Append("  CC: ").Append(t.CyclomaticComplexity)
              .Append("  LOC: ").Append(t.LinesOfCode)
              .Append("  DOI: ").Append(t.DepthOfInheritance)
              .Append("  Coupling: ").Append(t.ClassCoupling)
              .Append("  Instability: ").Append(t.Instability.ToString("F2"))
              .Append("  Members: ").Append(t.MemberCount)
              .AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Duplication

    public static string Format(PagedResult<CloneClass> result)
    {
        if (result.Items.Count == 0) return "No code duplication detected.";
        var sb = new StringBuilder();
        sb.Append("Showing ").Append(result.Items.Count).Append(" of ").Append(result.TotalCount)
          .AppendLine(" clone group(s), sorted by most instances:");
        sb.AppendLine();
        foreach (var clone in result.Items)
        {
            sb.Append("Clone (").Append(clone.CloneType)
              .Append(", similarity: ").Append(clone.Similarity.ToString("F2")).AppendLine(")");
            foreach (var inst in clone.Instances)
            {
                sb.Append("  ").Append(inst.FilePath)
                  .Append(':').Append(inst.LineNumber)
                  .Append('-').Append(inst.EndLineNumber);
                if (inst.MemberName != null)
                    sb.Append(' ').Append(inst.MemberName);
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Needs docs or refactor

    public static string Format(PagedResult<NeedsDocsOrRefactorCandidate> result)
    {
        if (result.Items.Count == 0) return "No candidates found — code looks clean.";
        var sb = new StringBuilder();
        sb.Append("Showing ").Append(result.Items.Count).Append(" of ").Append(result.TotalCount)
          .AppendLine(" candidate(s), sorted by worst opacity:");
        sb.AppendLine();
        foreach (var c in result.Items)
        {
            sb.Append(c.FilePath).Append(':').Append(c.LineNumber)
              .Append('-').Append(c.EndLineNumber);
            if (c.MemberName != null)
                sb.Append(' ').Append(c.MemberName);
            sb.AppendLine();
            sb.Append("  Opacity: ").Append(c.OpacityScore.ToString("F2"))
              .Append("  Name-Body Sim: ").Append(c.NameBodySimilarity.ToString("F2"))
              .Append("  CC: ").Append(c.CyclomaticComplexity)
              .Append("  Nesting: ").Append(c.NestingDepth)
              .Append("  Magic Literals: ").Append(c.MagicLiteralCount)
              .AppendLine();
            if (c.Reasons.Count > 0)
                sb.Append("  Reasons: ").AppendLine(string.Join(", ", c.Reasons));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // Quality snapshot confirmation

    public static string Format(QualitySnapshot snapshot)
    {
        return $"Quality snapshot captured at {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}\n" +
               $"Errors: {snapshot.ErrorCount}  Warnings: {snapshot.WarningCount}  Types measured: {snapshot.TypeMetrics.Count}";
    }

    // Quality comparison report

    public static string Format(QualityComparison result)
    {
        var sb = new StringBuilder();
        sb.Append("Quality Report (vs snapshot from ")
          .Append(result.Before.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss"))
          .AppendLine(")");
        sb.AppendLine();

        // Diagnostics delta
        var errDelta = result.After.ErrorCount - result.Before.ErrorCount;
        var warnDelta = result.After.WarningCount - result.Before.WarningCount;
        sb.AppendLine("Diagnostics");
        sb.Append("  Errors: ").Append(result.Before.ErrorCount).Append(" -> ").Append(result.After.ErrorCount)
          .Append(" (").Append(FormatDelta(errDelta)).Append(')');
        sb.Append("  Warnings: ").Append(result.Before.WarningCount).Append(" -> ").Append(result.After.WarningCount)
          .Append(" (").Append(FormatDelta(warnDelta)).AppendLine(")");

        // Improved
        sb.AppendLine();
        sb.Append("Improved (").Append(result.Improved.Count).AppendLine(")");
        foreach (var d in result.Improved)
            AppendTypeDelta(sb, d, result.Before, result.After);

        // Degraded
        sb.AppendLine();
        sb.Append("Degraded (").Append(result.Degraded.Count).AppendLine(")");
        foreach (var d in result.Degraded)
            AppendTypeDelta(sb, d, result.Before, result.After);

        // New types
        sb.AppendLine();
        sb.Append("New (").Append(result.NewTypes.Count).AppendLine(")");
        foreach (var t in result.NewTypes)
            sb.Append("  ").Append(t.FullName)
              .Append("  MI: ").Append(t.MI.ToString("F1"))
              .Append("  CC: ").Append(t.CC)
              .Append("  LOC: ").Append(t.LOC)
              .AppendLine();

        // Removed types
        sb.AppendLine();
        sb.Append("Removed (").Append(result.RemovedTypes.Count).AppendLine(")");
        foreach (var t in result.RemovedTypes)
            sb.Append("  ").Append(t.FullName).AppendLine();

        // Summary
        if (result.Before.TypeMetrics.Count > 0 && result.After.TypeMetrics.Count > 0)
        {
            var avgBefore = result.Before.TypeMetrics.Values.Average(t => t.MI);
            var avgAfter = result.After.TypeMetrics.Values.Average(t => t.MI);
            var avgDelta = avgAfter - avgBefore;
            sb.AppendLine();
            sb.Append("Summary: Net MI change ").Append(FormatDelta(avgDelta, "F1"))
              .Append(" avg across ").Append(result.After.TypeMetrics.Count).Append(" types");
        }

        return sb.ToString().TrimEnd();
    }

    // Git fallback report

    public static string Format(GitFallbackReport result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("No quality snapshot found. Showing current state of git-changed files.");
        sb.AppendLine("(Run quality_snapshot first to enable delta tracking.)");
        sb.AppendLine();

        if (result.ChangedFiles.Count == 0)
        {
            sb.Append("No changed .cs files detected.");
            return sb.ToString();
        }

        sb.Append("Changed files: ").AppendLine(result.ChangedFiles.Count.ToString());

        if (result.Diagnostics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Diagnostics on Changed Files");
            foreach (var d in result.Diagnostics)
                sb.Append("  ").Append(d.Id).Append(' ').Append(d.Severity).Append(' ')
                  .Append(d.FilePath).Append(':').Append(d.Line).Append(':').Append(d.Column)
                  .Append(' ').AppendLine(d.Message);
        }

        if (result.TypeMetrics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Workspace Type Metrics (worst first)");
            foreach (var t in result.TypeMetrics.Take(30))
                sb.Append("  ").Append(t.FullName)
                  .Append("  MI: ").Append(t.MI.ToString("F1"))
                  .Append("  CC: ").Append(t.CC)
                  .Append("  LOC: ").Append(t.LOC)
                  .Append("  Coupling: ").Append(t.Coupling)
                  .AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendTypeDelta(StringBuilder sb, TypeDelta d, QualitySnapshot before, QualitySnapshot after)
    {
        sb.Append("  ").Append(d.FullName);
        if (before.TypeMetrics.TryGetValue(d.FullName, out var b) &&
            after.TypeMetrics.TryGetValue(d.FullName, out var a))
        {
            if (Math.Abs(d.MIDelta) >= 0.1)
                sb.Append("  MI: ").Append(b.MI.ToString("F1")).Append(" -> ").Append(a.MI.ToString("F1"))
                  .Append(" (").Append(FormatDelta(d.MIDelta, "F1")).Append(')');
            if (d.CCDelta != 0)
                sb.Append("  CC: ").Append(b.CC).Append(" -> ").Append(a.CC)
                  .Append(" (").Append(FormatDelta(d.CCDelta)).Append(')');
            if (d.LOCDelta != 0)
                sb.Append("  LOC: ").Append(b.LOC).Append(" -> ").Append(a.LOC)
                  .Append(" (").Append(FormatDelta(d.LOCDelta)).Append(')');
            if (d.CouplingDelta != 0)
                sb.Append("  Coupling: ").Append(b.Coupling).Append(" -> ").Append(a.Coupling)
                  .Append(" (").Append(FormatDelta(d.CouplingDelta)).Append(')');
        }
        sb.AppendLine();
    }

    private static string FormatDelta(int delta) =>
        delta > 0 ? $"+{delta}" : delta.ToString();

    private static string FormatDelta(double delta, string format = "F0") =>
        delta > 0 ? $"+{delta.ToString(format)}" : delta.ToString(format);
}
