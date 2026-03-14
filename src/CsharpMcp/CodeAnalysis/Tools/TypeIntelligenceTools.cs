using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class TypeIntelligenceTools
{
    public record HoverResult(
        string DisplayString,
        string Kind,
        string? Documentation,
        string? ReturnType
    );

    public static async Task<HoverResult?> GetHoverAsync(Solution solution, Position pos)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var model = await doc.GetSemanticModelAsync();
        var root = await doc.GetSyntaxRootAsync();
        if (model is null || root is null) return null;

        var node = root.FindToken(offset).Parent;
        if (node is null) return null;

        var symbolInfo = model.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? model.GetDeclaredSymbol(node);
        if (symbol is null) return null;

        var xml = symbol.GetDocumentationCommentXml();
        var doc2 = ParseXmlDoc(xml);

        string? returnType = symbol switch
        {
            IMethodSymbol m => m.ReturnType.ToDisplayString(),
            IPropertySymbol p => p.Type.ToDisplayString(),
            IFieldSymbol f => f.Type.ToDisplayString(),
            ILocalSymbol l => l.Type.ToDisplayString(),
            IParameterSymbol p => p.Type.ToDisplayString(),
            _ => null
        };

        return new HoverResult(
            symbol.ToDisplayString(),
            symbol.Kind.ToString(),
            doc2,
            returnType
        );
    }

    public record ParameterHelp(string MethodSignature, List<ParameterDetail> Parameters);

    public record ParameterDetail(string Name, string Type, string? Documentation);

    public static async Task<ParameterHelp?> GetSignatureAsync(Solution solution, Position pos)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var model = await doc.GetSemanticModelAsync();
        var root = await doc.GetSyntaxRootAsync();
        if (model is null || root is null) return null;

        // Walk up to find the enclosing invocation
        var node = root.FindToken(offset).Parent;
        while (node is not null &&
               !node.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression) &&
               !node.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ObjectCreationExpression))
        {
            node = node.Parent;
        }

        if (node is null) return null;

        var symbolInfo = model.GetSymbolInfo(node);
        if (symbolInfo.Symbol is not IMethodSymbol method)
            method = symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (method is null) return null;

        var xml = method.GetDocumentationCommentXml();

        var parameters = method.Parameters.Select(p => new ParameterDetail(
            p.Name,
            p.Type.ToDisplayString(),
            ParseParamDoc(xml, p.Name)
        )).ToList();

        return new ParameterHelp(method.ToDisplayString(), parameters);
    }

    public record XmlDocEntry(
        string SymbolName,
        string Kind,
        string FilePath,
        int Line,
        string? Summary,
        List<(string Name, string Text)> Params,
        string? Returns,
        string? Remarks,
        string? Example,
        List<string> Exceptions
    );

    public static async Task<List<XmlDocEntry>> GetXmlDocAsync(
        Solution solution,
        string namePattern,
        string? kind = null,
        string? projectName = null,
        int maxResults = 100)
    {
        var projects = projectName is not null
            ? solution.Projects.Where(p => ProjectTools.MatchesPattern(p.Name, projectName))
            : solution.Projects;

        var results = new List<XmlDocEntry>();

        foreach (var project in projects)
        {
            var symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                project,
                name => ProjectTools.MatchesPattern(name, namePattern)
            );

            foreach (var sym in symbols)
            {
                if (kind is not null && !SemanticSearchTools.MatchesKind(sym, kind))
                    continue;

                var xml = sym.GetDocumentationCommentXml();
                if (string.IsNullOrWhiteSpace(xml)) continue;

                var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
                if (loc is null) continue;

                var span = loc.GetLineSpan();
                var entry = ParseFullXmlDoc(xml, sym, span);
                if (entry is not null)
                    results.Add(entry);

                if (results.Count >= maxResults) return results;
            }
        }

        return results;
    }

    private static XmlDocEntry? ParseFullXmlDoc(string xml, ISymbol symbol, FileLinePositionSpan span)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);

            var summary = doc.Descendants("summary").FirstOrDefault()?.Value.Trim();
            var returns = doc.Descendants("returns").FirstOrDefault()?.Value.Trim();
            var remarks = doc.Descendants("remarks").FirstOrDefault()?.Value.Trim();
            var example = doc.Descendants("example").FirstOrDefault()?.Value.Trim();

            var parms = doc.Descendants("param")
                .Select(e => (Name: e.Attribute("name")?.Value ?? "", Text: e.Value.Trim()))
                .Where(p => p.Name.Length > 0)
                .ToList();

            var exceptions = doc.Descendants("exception")
                .Select(e =>
                {
                    var cref = e.Attribute("cref")?.Value ?? "";
                    if (cref.StartsWith("T:")) cref = cref[2..];
                    var text = e.Value.Trim();
                    return string.IsNullOrEmpty(text) ? cref : $"{cref}: {text}";
                })
                .Where(s => s.Length > 0)
                .ToList();

            // Skip entries that have no meaningful doc content at all
            if (summary is null && parms.Count == 0 && returns is null && remarks is null && exceptions.Count == 0)
                return null;

            return new XmlDocEntry(
                symbol.ToDisplayString(),
                symbol.Kind.ToString(),
                span.Path,
                span.StartLinePosition.Line + 1,
                summary,
                parms,
                returns,
                remarks,
                example,
                exceptions
            );
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseXmlDoc(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            return doc.Descendants("summary").FirstOrDefault()?.Value.Trim();
        }
        catch { return null; }
    }

    private static string? ParseParamDoc(string? xml, string paramName)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            return doc.Descendants("param")
                .FirstOrDefault(e => e.Attribute("name")?.Value == paramName)
                ?.Value.Trim();
        }
        catch { return null; }
    }
}
