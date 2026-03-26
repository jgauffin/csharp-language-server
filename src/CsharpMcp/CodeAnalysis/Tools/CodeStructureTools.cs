using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class CodeStructureTools
{
    public record SymbolEntry(string Name, string Kind, int Line, int Column, string ContainingType);

    public static async Task<List<SymbolEntry>> GetSymbolsAsync(Solution solution, string filePath)
    {
        var doc = ResolveDocument(solution, filePath);
        var root = await doc.GetSyntaxRootAsync();
        if (root is null) return [];

        var model = await doc.GetSemanticModelAsync();
        if (model is null) return [];

        return root.DescendantNodes()
            .Where(IsDeclaration)
            .Select(n =>
            {
                var sym = model.GetDeclaredSymbol(n);
                if (sym is null) return null;
                var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
                var linePos = (loc ?? n.GetLocation()).GetLineSpan().StartLinePosition;
                var containing = sym.ContainingType?.Name ?? sym.ContainingNamespace?.ToString() ?? "";
                return new SymbolEntry(sym.Name, sym.Kind.ToString(), linePos.Line + 1, linePos.Character + 1, containing);
            })
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
    }

    public record OutlineNode(string Name, string Kind, int Line, int Column, List<OutlineNode> Children);

    public static async Task<List<OutlineNode>> GetOutlineAsync(Solution solution, string filePath)
    {
        var doc = ResolveDocument(solution, filePath);
        var root = await doc.GetSyntaxRootAsync();
        if (root is null) return [];

        var model = await doc.GetSemanticModelAsync();
        if (model is null) return [];

        return BuildOutline(root, model, depth: 0);
    }

    public record ImportEntry(string Name, bool IsStatic, bool IsGlobal, string? Alias);

    public static async Task<List<ImportEntry>> GetImportsAsync(Solution solution, string filePath)
    {
        var doc = ResolveDocument(solution, filePath);
        var root = await doc.GetSyntaxRootAsync() as CompilationUnitSyntax;
        if (root is null) return [];

        return root.Usings.Select(u => new ImportEntry(
            u.Name?.ToString() ?? "",
            u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword),
            u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword),
            u.Alias?.Name.ToString()
        )).ToList();
    }

    public static string RenderOutline(List<OutlineNode> items)
    {
        var lines = new List<string>();
        void Render(List<OutlineNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                lines.Add($"{new string(' ', depth)}{node.Name} :{node.Line}:{node.Column} {node.Kind}");
                Render(node.Children, depth + 1);
            }
        }
        Render(items, 0);
        return string.Join('\n', lines);
    }

    private static List<OutlineNode> BuildOutline(SyntaxNode node, SemanticModel model, int depth)
    {
        if (depth > 10) return []; // guard against pathological nesting

        var results = new List<OutlineNode>();
        foreach (var child in node.ChildNodes())
        {
            if (!IsDeclaration(child)) continue;

            var sym = model.GetDeclaredSymbol(child);
            if (sym is null) continue;

            var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
            var linePos = (loc ?? child.GetLocation()).GetLineSpan().StartLinePosition;
            var children = BuildOutline(child, model, depth + 1);
            results.Add(new OutlineNode(sym.Name, sym.Kind.ToString(), linePos.Line + 1, linePos.Character + 1, children));
        }
        return results;
    }

    private static bool IsDeclaration(SyntaxNode n) => n is
        TypeDeclarationSyntax or
        MethodDeclarationSyntax or
        PropertyDeclarationSyntax or
        FieldDeclarationSyntax or
        ConstructorDeclarationSyntax or
        EnumDeclarationSyntax or
        InterfaceDeclarationSyntax or
        DelegateDeclarationSyntax or
        EventDeclarationSyntax or
        NamespaceDeclarationSyntax or
        FileScopedNamespaceDeclarationSyntax;

    private static Document ResolveDocument(Solution solution, string filePath) =>
        PositionHelper.ResolveDocument(solution, filePath);
}
