namespace CsharpMcp;

public sealed record ServerConfig(
    string RootPath,
    string Name,
    string? Description,
    string AllowedDir,
    bool EnableQuality = true,
    bool EnableNuget = true)
{
    /// <summary>Parses the command line, rooting the server at the directory it was launched in.</summary>
    public static ServerConfig Parse(string[] args) => Parse(args, Directory.GetCurrentDirectory());

    /// <summary>Parses the command line against an explicit launch directory.</summary>
    public static ServerConfig Parse(string[] args, string launchDirectory)
    {
        string? name = null;
        string? description = null;
        string? directory = null;
        string? allowedDir = null;
        bool enableQuality = true;
        bool enableNuget = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--name" && i + 1 < args.Length)
                name = args[++i];
            else if (args[i] == "--description" && i + 1 < args.Length)
                description = args[++i];
            else if (args[i] == "--root" && i + 1 < args.Length)
                directory = args[++i];
            else if (args[i] == "--allowed-dir" && i + 1 < args.Length)
                allowedDir = args[++i];
            else if (args[i] == "--no-quality")
                enableQuality = false;
            else if (args[i] == "--no-nuget")
                enableNuget = false;
            else if (!args[i].StartsWith("--"))
                directory ??= args[i];
        }

        // The root is bound to the launch directory once, here, and every path downstream resolves
        // against it. Reading the working directory again later would silently repoint the workspace.
        var launchRoot = Path.GetFullPath(launchDirectory);
        var rootPath = Path.GetFullPath(directory ?? launchRoot, launchRoot);

        if (!Directory.Exists(rootPath))
            throw new ArgumentException($"Root path does not exist: {rootPath}");

        var resolvedAllowedDir = Path.GetFullPath(allowedDir ?? rootPath, launchRoot);
        if (!Directory.Exists(resolvedAllowedDir))
            throw new ArgumentException($"Allowed directory does not exist: {resolvedAllowedDir}");

        return new ServerConfig(rootPath, name ?? "csharp-language-mcp", description, resolvedAllowedDir, enableQuality, enableNuget);
    }
}
