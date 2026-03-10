namespace CsharpMcp;

public sealed record ServerConfig(string RootPath, string Name, string? Description, string? ModelDir)
{
    public static ServerConfig Parse(string[] args)
    {
        string? name = null;
        string? description = null;
        string? directory = null;
        string? modelDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--name" && i + 1 < args.Length)
                name = args[++i];
            else if (args[i] == "--description" && i + 1 < args.Length)
                description = args[++i];
            else if (args[i] == "--model-dir" && i + 1 < args.Length)
                modelDir = args[++i];
            else if (!args[i].StartsWith("--"))
                directory ??= args[i];
        }

        var rootPath = Path.GetFullPath(directory ?? Directory.GetCurrentDirectory());

        if (!Directory.Exists(rootPath))
            throw new ArgumentException($"Root path does not exist: {rootPath}");

        return new ServerConfig(rootPath, name ?? "csharp-language-mcp", description, modelDir);
    }
}
