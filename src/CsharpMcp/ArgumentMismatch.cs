using System.Text;
using System.Text.Json;

namespace CsharpMcp;

/// <summary>
/// Compares the arguments an agent sent against a tool's declared input schema.
/// </summary>
public static class ArgumentMismatch
{
    /// <summary>
    /// Returns a message naming the unusable arguments and listing the ones the tool accepts,
    /// or null when the arguments fit the schema.
    /// </summary>
    public static string? Describe(JsonElement inputSchema, IEnumerable<string>? suppliedArguments)
    {
        var accepted = PropertyNames(inputSchema);
        if (accepted.Count == 0) return null;

        var required = RequiredNames(inputSchema);
        var supplied = suppliedArguments?.ToList() ?? [];

        var unknown = supplied.Where(name => !accepted.Contains(name)).ToList();
        var missing = required.Where(name => !supplied.Contains(name)).ToList();
        if (unknown.Count == 0 && missing.Count == 0) return null;

        var message = new StringBuilder();
        if (unknown.Count > 0)
            message.Append($"unknown parameter{Plural(unknown)} {Quote(unknown)}. ");
        if (missing.Count > 0)
            message.Append($"missing required parameter{Plural(missing)} {Quote(missing)}. ");

        message.Append("Accepted parameters: ");
        message.Append(string.Join(", ", accepted.Select(name => required.Contains(name) ? $"{name} (required)" : name)));
        message.Append('.');
        return message.ToString();
    }

    private static List<string> PropertyNames(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToList()
            : [];

    private static List<string> RequiredNames(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];

    private static string Plural(List<string> names) => names.Count == 1 ? "" : "s";

    private static string Quote(List<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));
}
