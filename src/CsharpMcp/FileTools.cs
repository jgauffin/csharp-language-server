using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CsharpMcp;

[McpServerToolType]
public class FileTools(ServerConfig config, ILogger<FileTools> logger)
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".vs", ".idea", "packages",
        "TestResults", ".nuget", "artifacts"
    };

    /// <summary>Validate that a path is inside the allowed directory.</summary>
    private string ValidatePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(config.AllowedDir, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path is outside the allowed directory ({config.AllowedDir}): {full}");
        return full;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static bool ShouldSkipDir(string dirName) =>
        SkipDirs.Contains(dirName);

    [McpServerTool, Description("List directories under a given path (non-recursive). Returns the topmost subdirectories. Useful for exploring the file system structure.")]
    public Task<string> list_directory(
        [Description("Absolute path of the directory to list. Omit to list the allowed root directory.")] string? directory = null,
        [Description("Max directories to return")] int maxResults = 50)
    {
        logger.LogInformation("Tool list_directory invoked: dir={Dir}", directory);
        try
        {
            var dir = ValidatePath(directory ?? config.AllowedDir);
            if (!Directory.Exists(dir))
                return Task.FromResult($"Error: Directory not found: {dir}");

            var entries = new List<string>();

            // List subdirectories
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || ShouldSkipDir(name)) continue;
                entries.Add(Normalize(sub) + "/");
                if (entries.Count >= maxResults) break;
            }

            // Also list files (up to remaining capacity)
            int fileSlots = maxResults - entries.Count;
            if (fileSlots > 0)
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    entries.Add(Normalize(f));
                    if (--fileSlots <= 0) break;
                }
            }

            if (entries.Count == 0)
                return Task.FromResult($"{Normalize(dir)} is empty (or contains only skipped directories).");

            var sb = new StringBuilder();
            sb.AppendLine($"{Normalize(dir)} ({entries.Count} entries):");
            foreach (var e in entries)
                sb.AppendLine(e);
            return Task.FromResult(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool list_directory failed");
            return Task.FromResult($"Error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool, Description("Find files by glob pattern (e.g. **/*.cs, src/**/I*.cs). Returns absolute paths. Skips common non-source directories (node_modules, .git, bin, obj, etc.).")]
    public Task<string> glob_search(
        [Description("Glob pattern to match (e.g. **/*.cs, src/**/*.json)")] string pattern,
        [Description("Absolute path of directory to search within. Omit to search the entire workspace.")] string? directory = null,
        [Description("Max results to return")] int maxResults = 500)
    {
        logger.LogInformation("Tool glob_search invoked: pattern={Pattern}, dir={Dir}", pattern, directory);
        try
        {
            var searchRoot = ValidatePath(directory ?? config.RootPath);
            if (!Directory.Exists(searchRoot))
                return Task.FromResult($"Error: Directory not found: {searchRoot}");

            var matcher = new Matcher();
            matcher.AddInclude(pattern);
            foreach (var skip in SkipDirs)
            {
                matcher.AddExclude($"{skip}/**");
                matcher.AddExclude($"**/{skip}/**");
            }

            var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(searchRoot)));
            var files = result.Files
                .Select(f => Normalize(Path.GetFullPath(Path.Combine(searchRoot, f.Path))))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToList();

            if (files.Count == 0)
                return Task.FromResult("No files matched.");

            var sb = new StringBuilder();
            sb.AppendLine($"{files.Count} file(s) matched{(files.Count == maxResults ? $" (capped at {maxResults})" : "")}:");
            foreach (var f in files)
                sb.AppendLine(f);
            return Task.FromResult(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool glob_search failed");
            return Task.FromResult($"Error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool, Description("Search file contents using a regex pattern (like ripgrep). Returns matching lines with absolute file paths and line numbers. Skips common non-source directories and binary files.")]
    public Task<string> regex_search(
        [Description("Regex pattern to search for")] string pattern,
        [Description("Glob to filter which files to search (e.g. **/*.cs). Defaults to all files.")] string? fileGlob = null,
        [Description("Absolute path of directory to search within. Omit to search the entire workspace.")] string? directory = null,
        [Description("Case insensitive search")] bool ignoreCase = false,
        [Description("Number of context lines before and after each match")] int context = 0,
        [Description("Max total matches to return")] int maxResults = 200)
    {
        logger.LogInformation("Tool regex_search invoked: pattern={Pattern}, glob={Glob}, dir={Dir}", pattern, fileGlob, directory);
        try
        {
            var searchRoot = ValidatePath(directory ?? config.RootPath);
            if (!Directory.Exists(searchRoot))
                return Task.FromResult($"Error: Directory not found: {searchRoot}");

            var options = RegexOptions.Compiled;
            if (ignoreCase) options |= RegexOptions.IgnoreCase;
            var regex = new Regex(pattern, options, TimeSpan.FromSeconds(5));

            // Collect files to search
            var fileMatcher = new Matcher();
            fileMatcher.AddInclude(fileGlob ?? "**/*");
            foreach (var skip in SkipDirs)
            {
                fileMatcher.AddExclude($"{skip}/**");
                fileMatcher.AddExclude($"**/{skip}/**");
            }
            var matchResult = fileMatcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(searchRoot)));
            var files = matchResult.Files.Select(f => Path.GetFullPath(Path.Combine(searchRoot, f.Path))).ToList();

            var sb = new StringBuilder();
            int totalMatches = 0;
            bool capped = false;

            foreach (var file in files)
            {
                if (capped) break;
                if (!File.Exists(file)) continue;

                // Skip likely binary files
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (IsBinaryExtension(ext)) continue;

                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; }

                var absPath = Normalize(file);
                var fileHasMatch = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (capped) break;
                    if (!regex.IsMatch(lines[i])) continue;

                    if (!fileHasMatch)
                    {
                        if (sb.Length > 0) sb.AppendLine();
                        sb.AppendLine($"── {absPath} ──");
                        fileHasMatch = true;
                    }

                    int start = Math.Max(0, i - context);
                    int end = Math.Min(lines.Length - 1, i + context);

                    for (int c = start; c <= end; c++)
                    {
                        var prefix = c == i ? ">" : " ";
                        sb.AppendLine($"{prefix} {c + 1,5}: {lines[c]}");
                    }
                    if (context > 0) sb.AppendLine();

                    totalMatches++;
                    if (totalMatches >= maxResults)
                    {
                        capped = true;
                        break;
                    }
                }
            }

            if (totalMatches == 0)
                return Task.FromResult("No matches found.");

            var header = $"{totalMatches} match(es){(capped ? $" (capped at {maxResults})" : "")}:\n";
            return Task.FromResult(header + sb.ToString().TrimEnd());
        }
        catch (RegexParseException ex)
        {
            return Task.FromResult($"Error: Invalid regex: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool regex_search failed");
            return Task.FromResult($"Error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool, Description("Read a file's contents. Returns lines with line numbers. Supports optional line range to read a portion of large files.")]
    public Task<string> read_file(
        [Description("Absolute file path")] string filePath,
        [Description("Starting line number (1-based). Omit to start from the beginning.")] int? startLine = null,
        [Description("Ending line number (1-based, inclusive). Omit to read to end.")] int? endLine = null)
    {
        logger.LogInformation("Tool read_file invoked: path={Path}, start={Start}, end={End}", filePath, startLine, endLine);
        try
        {
            var fullPath = ValidatePath(filePath);
            if (!File.Exists(fullPath))
                return Task.FromResult($"Error: File not found: {fullPath}");

            var lines = File.ReadAllLines(fullPath);
            int total = lines.Length;

            int start = Math.Max(0, (startLine ?? 1) - 1);
            int end = Math.Min(total, endLine ?? total);

            if (start >= total)
                return Task.FromResult($"Error: startLine {startLine} exceeds file length ({total} lines)");

            var sb = new StringBuilder();
            sb.AppendLine($"{Normalize(fullPath)} ({total} lines){(startLine.HasValue || endLine.HasValue ? $" [showing {start + 1}-{end}]" : "")}:");

            int gutterWidth = end.ToString().Length;
            for (int i = start; i < end; i++)
                sb.AppendLine($"{(i + 1).ToString().PadLeft(gutterWidth)}: {lines[i]}");

            return Task.FromResult(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool read_file failed");
            return Task.FromResult($"Error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsBinaryExtension(string ext) => ext switch
    {
        ".dll" or ".exe" or ".pdb" or ".obj" or ".bin" or ".zip" or ".gz" or ".tar" or
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".svg" or
        ".woff" or ".woff2" or ".ttf" or ".eot" or
        ".nupkg" or ".snk" or ".pfx" or ".suo" => true,
        _ => false
    };
}
