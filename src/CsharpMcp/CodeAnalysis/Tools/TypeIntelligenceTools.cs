using Microsoft.CodeAnalysis;

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
