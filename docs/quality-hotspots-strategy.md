# Quality Hotspots: Composite Code Quality Scoring

## Problem

The current ArchiMetrics-powered quality tools expose 8+ separate tools (metrics, duplication, opacity, indirection, namespace browsing, workspace summary, snapshots, reports). This is too many for MCP agent consumers — they need a single entry point to answer: **"What code needs refactoring?"**

## Solution: `quality_hotspots` Tool

A single tool that weights multiple quality dimensions and returns a ranked list of the worst files/methods.

### Quality Dimensions

Each dimension produces a normalized score (0.0 = pristine, 1.0 = worst):

| Dimension | Source | What it measures | Key metrics |
|-----------|--------|-----------------|-------------|
| **Maintainability** | `GetWorstTypes()` | Structural complexity | MI (inverted), CC, LOC, coupling, instability |
| **Duplication** | `DetectDuplication()` | Code clones | Clone instance count, similarity %, token overlap |
| **Opacity** | `FindNeedsDocsOrRefactor()` | Hard-to-understand code | Embedding similarity, CC, nesting depth, magic literals |
| **Indirection** | `FindIndirectionHotspotsAsync()` | Hidden coupling via call chains | Indirect caller count, max/avg chain depth, call chain score |

### Composite Scoring

```
compositeScore = (maintainabilityScore * maintainabilityWeight)
               + (duplicationScore * duplicationWeight)
               + (opacityScore * opacityWeight)
               + (indirectionScore * indirectionWeight)
```

Default weights: 0.25 each (equal weighting). Caller can override to emphasize specific concerns.

### Score Normalization

Each dimension operates on different scales. Normalize to 0.0-1.0:

- **Maintainability Index (MI)**: Roslyn range is 0-100 where 100=best. Normalize: `1.0 - (MI / 100.0)`
- **Cyclomatic Complexity (CC)**: Unbounded. Use percentile rank within the analyzed set.
- **Duplication**: Proportion of tokens in a type that appear in clone groups.
- **Opacity score**: Already 0.0-1.0 from the embeddings/metrics analysis.
- **Indirection score**: Use the existing `(indirectCount * 2.0) + (maxChainDepth * 3.0) + (avgDepth * 1.5)` formula, then normalize via percentile rank.

For dimensions that produce per-method scores (opacity, indirection), roll up to per-type by taking the max or mean of member scores.

### Output: Ranked Hotspot List

```
record HotspotEntry(
    string FilePath,
    int Line,
    string TypeOrMember,        // e.g. "MyService.ProcessOrder"
    string Kind,                // Type, Method
    double CompositeScore,      // 0.0 - 1.0
    double MaintainabilityScore,
    double DuplicationScore,
    double OpacityScore,
    double IndirectionScore
);
```

Sorted by `CompositeScore` descending. Paged with `skip`/`take`.

### Snapshot Comparison Mode

When `snapshotLabel` is provided:
- If no snapshot with that label exists: capture current state as a named snapshot
- If snapshot exists and `compareToSnapshot` is provided: capture current state, diff against named snapshot, return deltas

Reuses existing `QualityTools.CaptureSnapshotAsync()` and `CompareSnapshots()`.

### API Surface

```csharp
[McpServerTool, Description("Find code hotspots that need refactoring. Weights maintainability, duplication, opacity, and indirection into a composite score.")]
public async Task<string> quality_hotspots(
    [Description("Filter to a specific project")] string? projectName = null,
    [Description("Weight for maintainability index (0.0-1.0)")] double maintainabilityWeight = 0.25,
    [Description("Weight for code duplication (0.0-1.0)")] double duplicationWeight = 0.25,
    [Description("Weight for code opacity/readability (0.0-1.0)")] double opacityWeight = 0.25,
    [Description("Weight for indirection/coupling (0.0-1.0)")] double indirectionWeight = 0.25,
    [Description("Snapshot label to capture or compare")] string? snapshotLabel = null,
    [Description("Compare current state against this snapshot label")] string? compareToSnapshot = null,
    int skip = 0,
    int take = 50
)
```

### Implementation Strategy

1. Call all 4 dimension methods in parallel (`Task.WhenAll`)
2. Build a dictionary keyed by fully-qualified type name
3. For each type, normalize each dimension's raw score to 0.0-1.0
4. Compute composite score using weights
5. Sort descending, apply skip/take
6. Format as plain text (not JSON — saves tokens for agent consumers)

### ArchiMetrics Changes Needed

The `CodeAnalysisAgent` class needs:
- A method that returns raw metric data (not formatted text) for all 4 dimensions
- Or: expose the existing `GetWorstTypes()`, `DetectDuplication()`, `FindNeedsDocsOrRefactor()` return types publicly so the MCP layer can compose them

Ideally, add a `GetCompositeHotspots()` method to `CodeAnalysisAgent` that:
1. Runs all analyses in parallel
2. Normalizes scores internally
3. Returns `PagedResult<HotspotEntry>` with composite + per-dimension scores

This keeps the scoring logic in ArchiMetrics (where it belongs) and the MCP tool stays thin.
