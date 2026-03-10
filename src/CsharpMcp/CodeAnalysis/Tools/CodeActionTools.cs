using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class CodeActionTools
{
    public record CodeActionEntry(
        string Title,
        string DiagnosticId,
        string DiagnosticMessage,
        List<CodeActionChange> Changes
    );

    public record CodeActionChange(string FilePath, int StartLine, int StartColumn, string OldText, string NewText);

    private static readonly Lazy<ImmutableArray<CodeFixProvider>> CSharpCodeFixProviders = new(() =>
    {
        var assembly = typeof(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions)
            .Assembly;

        // Also load CSharp.Features assembly
        var featuresAssembly = Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features");

        var providers = new List<CodeFixProvider>();
        foreach (var asm in new[] { assembly, featuresAssembly })
        {
            try
            {
                foreach (var type in asm.GetTypes())
                {
                    if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
                        continue;

                    var exportAttr = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
                    if (exportAttr is null)
                        continue;

                    try
                    {
                        if (Activator.CreateInstance(type) is CodeFixProvider provider)
                            providers.Add(provider);
                    }
                    catch
                    {
                        // Some providers may require specific constructor args
                    }
                }
            }
            catch
            {
                // Assembly scanning can fail for some types
            }
        }

        return providers.ToImmutableArray();
    });

    /// <summary>
    /// Gets code actions (quick fixes) available for diagnostics in a file.
    /// If line/column are provided, only returns fixes for diagnostics at that position.
    /// </summary>
    public static async Task<List<CodeActionEntry>> GetCodeActionsAsync(
        Solution solution,
        string filePath,
        int? line = null,
        int? column = null,
        int maxResults = 50)
    {
        var doc = PositionHelper.ResolveDocument(solution, filePath);
        var model = await doc.GetSemanticModelAsync();
        if (model is null) return [];

        var text = await doc.GetTextAsync();
        var diagnostics = model.GetDiagnostics();

        // Filter to actionable diagnostics (errors and warnings)
        var targetDiagnostics = diagnostics
            .Where(d => d.Location.IsInSource &&
                        (d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning));

        // If position specified, filter to diagnostics at that position
        if (line is not null && column is not null)
        {
            var offset = PositionHelper.ToOffset(text, line.Value, column.Value);
            targetDiagnostics = targetDiagnostics
                .Where(d => d.Location.SourceSpan.Contains(offset) ||
                            d.Location.SourceSpan.Start == offset);
        }

        var diagList = targetDiagnostics.ToList();
        if (diagList.Count == 0) return [];

        // Build lookup: diagnostic ID -> providers that can fix it
        var providers = CSharpCodeFixProviders.Value;
        var idToProviders = new Dictionary<string, List<CodeFixProvider>>();
        foreach (var provider in providers)
        {
            foreach (var id in provider.FixableDiagnosticIds)
            {
                if (!idToProviders.TryGetValue(id, out var list))
                {
                    list = [];
                    idToProviders[id] = list;
                }
                list.Add(provider);
            }
        }

        var results = new List<CodeActionEntry>();

        foreach (var diagnostic in diagList)
        {
            if (!idToProviders.TryGetValue(diagnostic.Id, out var matchingProviders))
                continue;

            foreach (var provider in matchingProviders)
            {
                var actions = new List<CodeAction>();
                var context = new CodeFixContext(
                    doc,
                    diagnostic,
                    (action, _) => actions.Add(action),
                    CancellationToken.None);

                try
                {
                    await provider.RegisterCodeFixesAsync(context);
                }
                catch
                {
                    continue;
                }

                foreach (var action in actions)
                {
                    var operations = await action.GetOperationsAsync(CancellationToken.None);
                    var changes = new List<CodeActionChange>();

                    foreach (var op in operations.OfType<ApplyChangesOperation>())
                    {
                        var changedSolution = op.ChangedSolution;
                        foreach (var projectChanges in changedSolution.GetChanges(solution).GetProjectChanges())
                        {
                            foreach (var changedDocId in projectChanges.GetChangedDocuments())
                            {
                                var originalDoc = solution.GetDocument(changedDocId);
                                var changedDoc = changedSolution.GetDocument(changedDocId);
                                if (originalDoc is null || changedDoc is null) continue;

                                var originalText = await originalDoc.GetTextAsync();
                                var changedText = await changedDoc.GetTextAsync();

                                foreach (var change in changedText.GetChangeRanges(originalText))
                                {
                                    var linePos = originalText.Lines.GetLinePosition(change.Span.Start);
                                    var oldContent = originalText.GetSubText(change.Span).ToString();
                                    var newContent = changedText.GetSubText(
                                        new TextSpan(change.Span.Start, change.NewLength)).ToString();

                                    changes.Add(new CodeActionChange(
                                        originalDoc.FilePath ?? "",
                                        linePos.Line + 1,
                                        linePos.Character + 1,
                                        oldContent,
                                        newContent));
                                }
                            }
                        }
                    }

                    if (changes.Count > 0)
                    {
                        results.Add(new CodeActionEntry(
                            action.Title,
                            diagnostic.Id,
                            diagnostic.GetMessage(),
                            changes));

                        if (results.Count >= maxResults) return results;
                    }
                }
            }
        }

        return results;
    }
}
