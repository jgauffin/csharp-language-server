using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    public static async Task<List<ReferenceResult>> GetReferencesAsync(Solution solution, Position pos, int maxResults = 200)
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

            if (results.Count >= maxResults) return results;
        }

        return results;
    }

    public static async Task<List<Location>> GetImplementationsAsync(Solution solution, Position pos)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, offset);
        if (symbol is null) return [];

        var impls = (await SymbolFinder.FindImplementationsAsync(symbol, solution)).ToList();

        // Expand any abstract results to their concrete overrides
        var expanded = new List<ISymbol>();
        foreach (var impl in impls)
        {
            if (impl is IMethodSymbol { IsAbstract: true } abstractMethod)
            {
                var overrides = await SymbolFinder.FindOverridesAsync(abstractMethod, solution);
                expanded.AddRange(overrides);
            }
            else
            {
                expanded.Add(impl);
            }
        }
        impls = expanded;

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

    // ── Trace Usages Tree ────────────────────────────────────────────────────

    public record TraceUsageNode(
        string SymbolName,
        Location Location,
        List<TraceUsageNode> Children,
        bool IsCycle,
        int OmittedCount
    );

    public static async Task<TraceUsageNode?> TraceUsagesAsync(
        Solution solution, Position pos, int maxDepth = 3, int maxPerNode = 5)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(doc, offset);
        if (symbol is null) return null;

        var rootNamespace = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        var rootProject = doc.Project.Name;

        return await TraceNodeAsync(symbol, solution, 0, maxDepth, maxPerNode,
            rootNamespace, rootProject, []);
    }

    private static async Task<TraceUsageNode> TraceNodeAsync(
        ISymbol symbol, Solution solution, int depth, int maxDepth, int maxPerNode,
        string rootNamespace, string rootProject, HashSet<string> visited)
    {
        var symbolKey = symbol.ToDisplayString();
        var symbolLoc = GetSymbolSourceLocation(symbol);

        if (visited.Contains(symbolKey))
            return new TraceUsageNode(symbolKey, symbolLoc, [], IsCycle: true, OmittedCount: 0);

        visited.Add(symbolKey);

        if (depth >= maxDepth)
            return new TraceUsageNode(symbolKey, symbolLoc, [], IsCycle: false, OmittedCount: 0);

        var callerSymbols = await FindEnclosingCallersAsync(symbol, solution);
        var total = callerSymbols.Count;

        var ranked = callerSymbols
            .Select(s => (score: ScoreCaller(s, rootNamespace, rootProject, solution), sym: s))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.sym.ToDisplayString())
            .Take(maxPerNode)
            .ToList();

        var omitted = total - ranked.Count;

        var children = new List<TraceUsageNode>();
        foreach (var (_, callerSym) in ranked)
        {
            // Per-branch visited set: only prevents direct A→B→A cycles,
            // allows the same node to appear on independent branches.
            var branchVisited = new HashSet<string>(visited);
            children.Add(await TraceNodeAsync(callerSym, solution, depth + 1, maxDepth, maxPerNode,
                rootNamespace, rootProject, branchVisited));
        }

        return new TraceUsageNode(symbolKey, symbolLoc, children, IsCycle: false, OmittedCount: omitted);
    }

    private static async Task<List<ISymbol>> FindEnclosingCallersAsync(ISymbol symbol, Solution solution)
    {
        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var callers = new Dictionary<string, ISymbol>(StringComparer.Ordinal);

        foreach (var refSymbol in refs)
        foreach (var refLocation in refSymbol.Locations)
        {
            if (!refLocation.Location.IsInSource) continue;
            var tree = refLocation.Location.SourceTree;
            if (tree is null) continue;

            var docIds = solution.GetDocumentIdsWithFilePath(tree.FilePath);
            var refDoc = docIds.Select(id => solution.GetDocument(id)).FirstOrDefault(d => d is not null);
            if (refDoc is null) continue;

            var root = await refDoc.GetSyntaxRootAsync();
            var model = await refDoc.GetSemanticModelAsync();
            if (root is null || model is null) continue;

            var node = root.FindToken(refLocation.Location.SourceSpan.Start).Parent;
            var container = node?.AncestorsAndSelf().FirstOrDefault(n =>
                n is MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or PropertyDeclarationSyntax
                or AccessorDeclarationSyntax
                or LocalFunctionStatementSyntax);

            if (container is null) continue;

            var enclosing = model.GetDeclaredSymbol(container);
            if (enclosing is null) continue;

            callers.TryAdd(enclosing.ToDisplayString(), enclosing);
        }

        return [.. callers.Values];
    }

    private static int ScoreCaller(ISymbol caller, string rootNamespace, string rootProject, Solution solution)
    {
        var score = 0;

        var callerNamespace = caller.ContainingNamespace?.ToDisplayString() ?? "";
        if (!string.IsNullOrEmpty(rootNamespace) && callerNamespace.StartsWith(rootNamespace))
            score += 3;

        var callerLoc = caller.Locations.FirstOrDefault(l => l.IsInSource);
        if (callerLoc is not null)
        {
            var filePath = callerLoc.SourceTree?.FilePath ?? "";

            var docIds = solution.GetDocumentIdsWithFilePath(filePath);
            var projectName = docIds
                .Select(id => solution.GetDocument(id)?.Project.Name)
                .FirstOrDefault(n => n is not null);
            if (projectName == rootProject) score += 2;

            if (filePath.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                filePath.Contains("Spec", StringComparison.OrdinalIgnoreCase))
                score -= 2;

            var fileName = Path.GetFileName(filePath);
            if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
                score -= 10;
        }

        return score;
    }

    private static Location GetSymbolSourceLocation(ISymbol symbol)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc is null) return new Location("", 0, 0);
        return PositionHelper.ToLocation(loc.GetLineSpan());
    }
}
