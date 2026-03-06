using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;

namespace CsharpMcp.CodeAnalysis.Tools;

public static class CompletionTools
{
    public record CompletionItem(
        string Name,
        string Kind,
        string? SortText,
        string? InsertText,
        string? Documentation
    );

    public static async Task<List<CompletionItem>> GetCompletionsAsync(
        Solution solution,
        Position pos,
        int maxResults = 50)
    {
        var (doc, offset) = await PositionHelper.ResolveAsync(solution, pos);

        var completionService = CompletionService.GetService(doc);
        if (completionService is null)
            return [];

        var completions = await completionService.GetCompletionsAsync(doc, offset);
        if (completions is null)
            return [];

        var items = completions.ItemsList;
        if (items.Count == 0)
            return [];

        var results = new List<CompletionItem>(Math.Min(items.Count, maxResults));

        foreach (var item in items.Take(maxResults))
        {
            string? documentation = null;
            try
            {
                var description = await completionService.GetDescriptionAsync(doc, item);
                if (description is not null)
                    documentation = description.Text;
            }
            catch
            {
                // Some items may not have descriptions available
            }

            results.Add(new CompletionItem(
                Name: item.DisplayText,
                Kind: item.Tags.Length > 0 ? item.Tags[0] : "Unknown",
                SortText: item.SortText,
                InsertText: item.FilterText != item.DisplayText ? item.FilterText : null,
                Documentation: documentation
            ));
        }

        return results;
    }
}
