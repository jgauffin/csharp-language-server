using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class IndirectionTools
{
    // ── Public result records ────────────────────────────────────────────────

    public record CallChainStep(string SymbolName, Location Location);

    public record CallChain(List<CallChainStep> Steps)
    {
        public int Depth => Steps.Count - 1;
    }

    public record IndirectionOffender(
        string SymbolName,
        string SymbolKind,
        string ContainingType,
        Location Location,
        double Score,
        int TotalChainCount,
        int MaxChainDepth,
        double AvgChainDepth,
        int DirectCallerCount,
        int IndirectCallerCount,
        List<CallChain> WorstChains);

    public record IndirectionResult(
        List<IndirectionOffender> Items,
        int TotalCount,
        string AnalysisScope);

    // ── Internal helpers ─────────────────────────────────────────────────────

    private record SymbolMeta(string DisplayName, string Kind, string ContainingType, Location Location);

    private record CandidateMetrics(
        string SymbolKey,
        int DirectCallerCount,
        int IndirectCallerCount,
        int MaxChainDepth,
        double AvgChainDepth,
        double Score);

    private const int MaxCandidates = 500;

    // ── Entry point ──────────────────────────────────────────────────────────

    public static async Task<IndirectionResult> FindIndirectionHotspotsAsync(
        Solution solution,
        string? projectName = null,
        int maxDepth = 5,
        int minDirectCallers = 3,
        int maxChainsPerOffender = 5,
        bool includeTests = false,
        int skip = 0,
        int take = 30)
    {
        // Phase 1: Build call graph
        var (forward, reverse, symbolInfo) =
            await BuildCallGraphAsync(solution, projectName, includeTests);

        // Phase 2: Pre-filter candidates
        var candidates = PreFilterCandidates(forward, reverse, minDirectCallers);

        // Phase 3 Pass 1: Metrics-only BFS
        var metrics = ComputeMetricsOnly(reverse, candidates, maxDepth);

        // Phase 3 Pass 2: Path reconstruction for top offenders
        var pathBudget = Math.Min(metrics.Count, (skip + take) * 3);
        var topKeys = metrics.Take(pathBudget).Select(m => m.SymbolKey).ToHashSet(StringComparer.Ordinal);
        var offenders = ReconstructPaths(reverse, symbolInfo, metrics, topKeys, maxDepth, maxChainsPerOffender);

        // Phase 4: Paginate
        var totalCount = offenders.Count;
        var paged = offenders.Skip(skip).Take(take).ToList();

        var scope = projectName is not null ? $"Project: {projectName}" : "Solution";
        return new IndirectionResult(paged, totalCount, scope);
    }

    // ── Phase 1: Build call graph ────────────────────────────────────────────

    private static async Task<(
        Dictionary<string, HashSet<string>> Forward,
        Dictionary<string, HashSet<string>> Reverse,
        Dictionary<string, SymbolMeta> SymbolInfo
    )> BuildCallGraphAsync(Solution solution, string? projectName, bool includeTests)
    {
        var forward = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var reverse = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var symbolInfo = new Dictionary<string, SymbolMeta>(StringComparer.Ordinal);

        var projects = projectName is not null
            ? solution.Projects.Where(p => ProjectTools.MatchesPattern(p.Name, projectName))
            : solution.Projects;

        foreach (var project in projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is null) continue;
                if (ShouldSkipFile(document.FilePath, includeTests)) continue;

                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var node in root.DescendantNodes())
                {
                    ISymbol? callee = null;

                    if (node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax)
                    {
                        callee = model.GetSymbolInfo(node).Symbol;
                    }
                    else if (node is MemberAccessExpressionSyntax)
                    {
                        var sym = model.GetSymbolInfo(node).Symbol;
                        var memberType = sym switch
                        {
                            IPropertySymbol p => p.Type,
                            IFieldSymbol f => f.Type,
                            _ => null
                        };
                        if (memberType is null || !ExposesDomainType(memberType))
                            continue;
                        callee = sym;
                    }
                    else
                    {
                        continue;
                    }

                    if (callee is null) continue;
                    if (!callee.Locations.Any(l => l.IsInSource)) continue;

                    var container = node.AncestorsAndSelf().FirstOrDefault(n =>
                        n is MethodDeclarationSyntax
                        or ConstructorDeclarationSyntax
                        or PropertyDeclarationSyntax
                        or AccessorDeclarationSyntax
                        or LocalFunctionStatementSyntax);
                    if (container is null) continue;

                    var caller = model.GetDeclaredSymbol(container);
                    if (caller is null) continue;

                    var callerKey = caller.ToDisplayString();
                    var calleeKey = callee.ToDisplayString();
                    if (callerKey == calleeKey) continue; // skip self-calls

                    if (!forward.TryGetValue(callerKey, out var callees))
                        forward[callerKey] = callees = new(StringComparer.Ordinal);
                    callees.Add(calleeKey);

                    if (!reverse.TryGetValue(calleeKey, out var callers))
                        reverse[calleeKey] = callers = new(StringComparer.Ordinal);
                    callers.Add(callerKey);

                    RecordMeta(symbolInfo, callerKey, caller);
                    RecordMeta(symbolInfo, calleeKey, callee);
                }
            }
        }

        return (forward, reverse, symbolInfo);
    }

    private static void RecordMeta(Dictionary<string, SymbolMeta> map, string key, ISymbol symbol)
    {
        if (map.ContainsKey(key)) return;

        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var location = loc is not null
            ? PositionHelper.ToLocation(loc.GetLineSpan())
            : new Location("", 0, 0);

        map[key] = new SymbolMeta(
            symbol.ToDisplayString(),
            symbol.Kind.ToString(),
            symbol.ContainingType?.ToDisplayString() ?? "",
            location);
    }

    private static bool ExposesDomainType(ITypeSymbol type)
    {
        // Unwrap Nullable<T>
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        // Unwrap arrays and System.Collections.* generics (Dictionary<K,V> -> V, List<T> -> T)
        if (type is IArrayTypeSymbol array)
        {
            type = array.ElementType;
        }
        else if (type is INamedTypeSymbol { IsGenericType: true } generic
            && generic.ContainingNamespace?.ToDisplayString().StartsWith("System.Collections", StringComparison.Ordinal) == true)
        {
            type = generic.TypeArguments[^1];
        }

        // Re-unwrap Nullable in case the element type was Nullable<T>
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable2)
            type = nullable2.TypeArguments[0];

        if (type.TypeKind == TypeKind.Enum) return false;

        // Catches all primitives, String, Object, DateTime, IntPtr, etc.
        if (type.SpecialType != SpecialType.None) return false;

        // Skip Guid, TimeSpan, DateTimeOffset, Uri, DateOnly, TimeOnly, Task<T>, etc.
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool ShouldSkipFile(string filePath, bool includeTests)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!includeTests &&
            (filePath.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
             filePath.Contains("Spec", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    // ── Phase 2: Pre-filter candidates ───────────────────────────────────────

    private static List<string> PreFilterCandidates(
        Dictionary<string, HashSet<string>> forward,
        Dictionary<string, HashSet<string>> reverse,
        int minDirectCallers)
    {
        return reverse
            .Where(kv => kv.Value.Count >= minDirectCallers)
            .OrderByDescending(kv =>
            {
                var inDegree = kv.Value.Count;
                var outDegree = forward.TryGetValue(kv.Key, out var callees) ? callees.Count : 0;
                return (double)inDegree / Math.Max(outDegree, 1);
            })
            .Take(MaxCandidates)
            .Select(kv => kv.Key)
            .ToList();
    }

    // ── Phase 3 Pass 1: Metrics-only BFS ─────────────────────────────────────

    private static List<CandidateMetrics> ComputeMetricsOnly(
        Dictionary<string, HashSet<string>> reverse,
        List<string> candidates,
        int maxDepth)
    {
        var results = new List<CandidateMetrics>(candidates.Count);

        foreach (var symbolKey in candidates)
        {
            if (!reverse.TryGetValue(symbolKey, out var directCallers)) continue;

            var visited = new HashSet<string>(StringComparer.Ordinal) { symbolKey };
            var queue = new Queue<(string Node, int Depth)>();

            foreach (var caller in directCallers)
            {
                if (visited.Add(caller))
                    queue.Enqueue((caller, 1));
            }

            int directCount = directCallers.Count;
            int indirectCount = 0;
            int maxChainDepth = directCallers.Count > 0 ? 1 : 0;
            double depthSum = directCallers.Count; // each direct caller contributes depth 1

            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();

                if (depth >= 2)
                    indirectCount++;

                if (depth > maxChainDepth)
                    maxChainDepth = depth;

                depthSum += depth;

                if (depth >= maxDepth) continue;

                if (reverse.TryGetValue(current, out var nextCallers))
                {
                    foreach (var next in nextCallers)
                    {
                        if (visited.Add(next))
                            queue.Enqueue((next, depth + 1));
                    }
                }
            }

            if (indirectCount == 0) continue;

            var totalChains = directCount + indirectCount;
            var avgDepth = totalChains > 0 ? depthSum / totalChains : 0;
            var score = (indirectCount * 2.0) + (maxChainDepth * 3.0) + (avgDepth * 1.5);

            results.Add(new CandidateMetrics(symbolKey, directCount, indirectCount,
                maxChainDepth, Math.Round(avgDepth, 2), Math.Round(score, 2)));
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    // ── Phase 3 Pass 2: Path reconstruction ──────────────────────────────────

    private static List<IndirectionOffender> ReconstructPaths(
        Dictionary<string, HashSet<string>> reverse,
        Dictionary<string, SymbolMeta> symbolInfo,
        List<CandidateMetrics> metrics,
        HashSet<string> topKeys,
        int maxDepth,
        int maxChainsPerOffender)
    {
        var results = new List<IndirectionOffender>();

        foreach (var m in metrics)
        {
            if (!topKeys.Contains(m.SymbolKey)) continue;
            if (!symbolInfo.TryGetValue(m.SymbolKey, out var meta)) continue;
            if (!reverse.TryGetValue(m.SymbolKey, out var directCallers)) continue;

            // BFS with full path tracking
            var visited = new HashSet<string>(StringComparer.Ordinal) { m.SymbolKey };
            var queue = new Queue<(string Node, List<string> Path)>();

            foreach (var caller in directCallers)
            {
                if (visited.Add(caller))
                    queue.Enqueue((caller, [m.SymbolKey, caller]));
            }

            var allChains = new List<List<string>>();

            while (queue.Count > 0)
            {
                var (current, path) = queue.Dequeue();
                var depth = path.Count - 1;

                if (depth >= 2)
                {
                    var chain = new List<string>(path);
                    chain.Reverse(); // entry point -> ... -> target
                    allChains.Add(chain);
                }

                if (depth >= maxDepth) continue;

                if (reverse.TryGetValue(current, out var nextCallers))
                {
                    foreach (var next in nextCallers)
                    {
                        if (visited.Add(next))
                            queue.Enqueue((next, [.. path, next]));
                    }
                }
            }

            var worstChains = allChains
                .OrderByDescending(c => c.Count)
                .Take(maxChainsPerOffender)
                .Select(pathKeys => new CallChain(
                    pathKeys.Select(k => new CallChainStep(
                        k,
                        symbolInfo.TryGetValue(k, out var si) ? si.Location : new Location("", 0, 0)
                    )).ToList()
                ))
                .ToList();

            results.Add(new IndirectionOffender(
                meta.DisplayName,
                meta.Kind,
                meta.ContainingType,
                meta.Location,
                m.Score,
                m.DirectCallerCount + m.IndirectCallerCount,
                m.MaxChainDepth,
                m.AvgChainDepth,
                m.DirectCallerCount,
                m.IndirectCallerCount,
                worstChains));
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
    }
}
