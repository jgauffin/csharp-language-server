namespace CsharpMcp.CodeAnalysis.Tools;

public static class EfficiencyTools
{
    public record PositionAnalysis(
        TypeIntelligenceTools.HoverResult? Hover,
        List<DiagnosticsTools.DiagnosticEntry> Diagnostics,
        List<CodeStructureTools.SymbolEntry> FileSymbols
    );

    public static async Task<PositionAnalysis> AnalyzePositionAsync(
        Microsoft.CodeAnalysis.Solution solution,
        Position pos)
    {
        var hover = await TypeIntelligenceTools.GetHoverAsync(solution, pos);
        var diagnostics = await DiagnosticsTools.GetDiagnosticsAsync(solution, pos.FilePath);
        var symbols = await CodeStructureTools.GetSymbolsAsync(solution, pos.FilePath);

        return new PositionAnalysis(hover, diagnostics, symbols);
    }

    public static async Task<List<PositionAnalysis>> BatchAnalyzeAsync(
        Microsoft.CodeAnalysis.Solution solution,
        List<Position> positions)
    {
        var tasks = positions.Select(p => AnalyzePositionAsync(solution, p));
        return [.. await Task.WhenAll(tasks)];
    }
}
