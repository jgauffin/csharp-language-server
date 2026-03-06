using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class NavigationTools
{
    public static async Task<Location?> GetDefinitionAsync(Solution solution, Position pos)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, offset);
        if (symbol is null) return null;

        var definition = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution);
        var target = definition ?? symbol;

        var loc = target.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc is null) return null;

        var span = loc.GetLineSpan();
        return PositionHelper.ToLocation(span, target.ToDisplayString());
    }

    public record ReferenceResult(Location Location, bool IsWrite);

    public static async Task<List<ReferenceResult>> GetReferencesAsync(Solution solution, Position pos)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, offset);
        if (symbol is null) return [];

        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var results = new List<ReferenceResult>();

        foreach (var refSymbol in refs)
        foreach (var location in refSymbol.Locations)
        {
            var span = location.Location.GetLineSpan();
            results.Add(new ReferenceResult(
                PositionHelper.ToLocation(span),
                false
            ));
        }

        return results;
    }

    public static async Task<List<Location>> GetImplementationsAsync(Solution solution, Position pos)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, offset);
        if (symbol is null) return [];

        var impls = (await SymbolFinder.FindImplementationsAsync(symbol, solution)).ToList();

        // For interface/abstract members, also search for overrides transitively
        if (symbol is IMethodSymbol method && impls.Count == 0)
        {
            // Find implementations via containing type hierarchy
            var containingType = method.ContainingType;
            if (containingType is not null)
            {
                var typeImpls = containingType.TypeKind == TypeKind.Interface
                    ? await SymbolFinder.FindImplementationsAsync(containingType, solution)
                    : [.. (await SymbolFinder.FindDerivedClassesAsync(containingType, solution))];

                foreach (var typeImpl in typeImpls.OfType<INamedTypeSymbol>())
                {
                    var implMember = typeImpl.FindImplementationForInterfaceMember(method)
                        ?? typeImpl.GetMembers(method.Name)
                            .OfType<IMethodSymbol>()
                            .FirstOrDefault(m => m.IsOverride);
                    if (implMember is not null)
                        impls.Add(implMember);
                }
            }
        }

        return impls
            .SelectMany(s => s.Locations.Where(l => l.IsInSource))
            .Select(l => PositionHelper.ToLocation(l.GetLineSpan(), l.SourceTree?.FilePath))
            .ToList();
    }

    public record CallInfo(string SymbolName, Location Location, CallDirection Direction);

    public enum CallDirection { Caller, Callee }

    public static async Task<List<CallInfo>> GetCallHierarchyAsync(Solution solution, Position pos)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, offset);

        if (symbol is not IMethodSymbol method) return [];

        var results = new List<CallInfo>();

        // Callers
        var callers = await SymbolFinder.FindCallersAsync(method, solution);
        foreach (var caller in callers)
        foreach (var loc in caller.Locations)
        {
            var span = loc.GetLineSpan();
            results.Add(new CallInfo(
                caller.CallingSymbol.ToDisplayString(),
                PositionHelper.ToLocation(span),
                CallDirection.Caller
            ));
        }

        // Callees: find all method calls within this method's body
        var methodRef = await SymbolFinder.FindReferencesAsync(method, solution);
        // For callees we inspect the method syntax directly
        var syntaxRef = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (syntaxRef is not null)
        {
            var calleeDoc = solution.GetDocumentIdsWithFilePath(syntaxRef.SourceTree!.FilePath)
                .Select(id => solution.GetDocument(id))
                .FirstOrDefault(d => d is not null);

            if (calleeDoc is not null)
            {
                var model = await calleeDoc.GetSemanticModelAsync();
                var root = await calleeDoc.GetSyntaxRootAsync();

                if (model is not null && root is not null)
                {
                    var methodNode = root.FindNode(syntaxRef.SourceSpan);
                    var invocations = methodNode
                        .DescendantNodes()
                        .Where(n => n.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression));

                    foreach (var inv in invocations)
                    {
                        var invSymbol = model.GetSymbolInfo(inv).Symbol;
                        if (invSymbol is null) continue;

                        var invLoc = inv.GetLocation().GetLineSpan();
                        results.Add(new CallInfo(
                            invSymbol.ToDisplayString(),
                            PositionHelper.ToLocation(invLoc),
                            CallDirection.Callee
                        ));
                    }
                }
            }
        }

        return results;
    }

    public record TypeHierarchyResult(
        string Name,
        string? BaseType,
        List<string> Interfaces,
        List<string> DerivedTypes
    );

    public static async Task<TypeHierarchyResult?> GetTypeHierarchyAsync(Solution solution, Position pos)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, offset);

        if (symbol is not INamedTypeSymbol type) return null;

        var derived = await SymbolFinder.FindDerivedClassesAsync(type, solution);

        return new TypeHierarchyResult(
            type.ToDisplayString(),
            type.BaseType?.ToDisplayString(),
            type.Interfaces.Select(i => i.ToDisplayString()).ToList(),
            derived.Select(d => d.ToDisplayString()).ToList()
        );
    }
}
