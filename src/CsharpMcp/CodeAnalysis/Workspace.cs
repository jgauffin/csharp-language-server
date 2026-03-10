using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsharpMcp.CodeAnalysis;

/// <summary>
/// Owns the MSBuildWorkspace and provides access to loaded projects.
/// Discovers all .csproj files under rootPath on startup.
/// Watches for file changes and keeps documents in sync with disk.
/// </summary>
public sealed class RoslynWorkspace : IDisposable
{
    private readonly MSBuildWorkspace _workspace;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentQueue<FileChange> _pendingChanges = new();
    private readonly Lock _lock = new();
    private readonly ILogger<RoslynWorkspace> _logger;
    private Solution _currentSolution;
    private volatile bool _fullResyncNeeded;

    // projectName (no extension) -> Project
    private readonly Dictionary<string, Project> _projects = new(StringComparer.OrdinalIgnoreCase);

    private enum ChangeKind { Updated, Deleted, Renamed }
    private readonly record struct FileChange(string FullPath, ChangeKind Kind, string? OldFullPath = null);

    static RoslynWorkspace()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    public string RootPath { get; }
    public Workspace InnerWorkspace => _workspace;

    private RoslynWorkspace(MSBuildWorkspace workspace, string rootPath, ILogger<RoslynWorkspace> logger)
    {
        RootPath = rootPath;
        _workspace = workspace;
        _logger = logger;
        _currentSolution = workspace.CurrentSolution;
        _watcher = new FileSystemWatcher(rootPath, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            InternalBufferSize = 65536,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, e) => _pendingChanges.Enqueue(new(Path.GetFullPath(e.FullPath), ChangeKind.Updated));
        _watcher.Created += (_, e) => _pendingChanges.Enqueue(new(Path.GetFullPath(e.FullPath), ChangeKind.Updated));
        _watcher.Deleted += (_, e) => _pendingChanges.Enqueue(new(Path.GetFullPath(e.FullPath), ChangeKind.Deleted));
        _watcher.Renamed += (_, e) => _pendingChanges.Enqueue(
            new(Path.GetFullPath(e.FullPath), ChangeKind.Renamed, Path.GetFullPath(e.OldFullPath)));
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "FileSystemWatcher buffer overflow — scheduling full resync");
            _fullResyncNeeded = true;
        };
    }

    /// <summary>
    /// Flushes any pending file changes into the workspace solution so that
    /// subsequent reads see the latest content from disk.
    /// </summary>
    private void ApplyPendingChanges()
    {
        if (_pendingChanges.IsEmpty && !_fullResyncNeeded) return;

        lock (_lock)
        {
            if (_fullResyncNeeded)
            {
                _fullResyncNeeded = false;
                // Drain stale queue
                while (_pendingChanges.TryDequeue(out _)) { }
                FullResync();
                return;
            }

            if (_pendingChanges.IsEmpty) return;

            var changes = new List<FileChange>();
            while (_pendingChanges.TryDequeue(out var change))
                changes.Add(change);

            _logger.LogDebug("Applying {Count} pending file changes", changes.Count);

            // Deduplicate: keep the LAST change per file path so we don't
            // process a stale Update when a later Delete was the final state.
            var lastChange = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                lastChange[change.FullPath] = change;
                // For renames, also process the removal of the old path
                if (change.Kind == ChangeKind.Renamed && change.OldFullPath is not null)
                    lastChange[change.OldFullPath] = new(change.OldFullPath, ChangeKind.Deleted);
            }

            var solution = _currentSolution;

            foreach (var change in lastChange.Values)
            {
                switch (change.Kind)
                {
                    case ChangeKind.Deleted:
                    {
                        var docId = solution.GetDocumentIdsWithFilePath(change.FullPath).FirstOrDefault();
                        if (docId is not null)
                            solution = solution.RemoveDocument(docId);
                        break;
                    }
                    case ChangeKind.Renamed:
                    {
                        // Old path already handled as a Deleted entry above
                        solution = AddOrUpdateDocument(solution, change.FullPath);
                        break;
                    }
                    case ChangeKind.Updated:
                    default:
                        solution = AddOrUpdateDocument(solution, change.FullPath);
                        break;
                }
            }

            _currentSolution = solution;
        }
    }

    /// <summary>
    /// Re-reads all .cs files from disk and reconciles with the current solution.
    /// Called when FileSystemWatcher reports a buffer overflow.
    /// </summary>
    private void FullResync()
    {
        _logger.LogInformation("Performing full resync of all .cs files");

        var solution = _currentSolution;

        // Collect all .cs files currently on disk
        var diskFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(RootPath, "*.cs", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(file);
            if (IsBuildOutputPath(fullPath)) continue;
            diskFiles.Add(fullPath);
        }

        // Remove documents that no longer exist on disk
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath is not null && !diskFiles.Contains(doc.FilePath))
                    solution = solution.RemoveDocument(doc.Id);
            }
        }

        // Add or update documents that exist on disk
        foreach (var filePath in diskFiles)
        {
            solution = AddOrUpdateDocument(solution, filePath);
        }

        _currentSolution = solution;
        _logger.LogInformation("Full resync complete");
    }

    private Solution AddOrUpdateDocument(Solution solution, string filePath)
    {
        if (!File.Exists(filePath)) return solution;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var text = SourceText.From(stream);

            var docId = solution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
            if (docId is not null)
                return solution.WithDocumentText(docId, text);

            // New file: add to the project whose directory is the closest ancestor
            var project = FindContainingProject(solution, filePath);
            if (project is null) return solution;

            var docName = Path.GetFileName(filePath);
            return solution.AddDocument(DocumentId.CreateNewId(project.Id), docName, text, filePath: filePath);
        }
        catch (IOException)
        {
            // File may be locked; re-enqueue for next flush
            _pendingChanges.Enqueue(new(filePath, ChangeKind.Updated));
            return solution;
        }
    }

    private static Project? FindContainingProject(Solution solution, string filePath)
    {
        Project? best = null;
        int bestLen = -1;
        foreach (var project in solution.Projects)
        {
            var projDir = Path.GetDirectoryName(project.FilePath);
            if (projDir is null) continue;
            if (!filePath.StartsWith(projDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (projDir.Length > bestLen)
            {
                best = project;
                bestLen = projDir.Length;
            }
        }
        return best;
    }

    public static async Task<RoslynWorkspace> LoadAsync(string rootPath, ILoggerFactory? loggerFactory = null, Func<string, bool>? projectFilter = null)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RoslynWorkspace>();
        var workspace = MSBuildWorkspace.Create();
        var instance = new RoslynWorkspace(workspace, rootPath, logger);

        var filter = projectFilter ?? (p => !IsBuildOutputPath(p));
        var csprojFiles = Directory.GetFiles(rootPath, "*.csproj", SearchOption.AllDirectories)
            .Where(filter)
            .ToArray();
        logger.LogInformation("Found {Count} .csproj files under {RootPath}", csprojFiles.Length, rootPath);

        foreach (var path in csprojFiles)
        {
            var fullPath = Path.GetFullPath(path);
            var alreadyLoaded = workspace.CurrentSolution.Projects
                .Any(p => string.Equals(p.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

            Project project;
            if (alreadyLoaded)
            {
                project = workspace.CurrentSolution.Projects
                    .First(p => string.Equals(p.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
                logger.LogDebug("Project already loaded: {ProjectPath}", fullPath);
            }
            else
            {
                logger.LogInformation("Loading project: {ProjectPath}", fullPath);
                project = await workspace.OpenProjectAsync(path);
                logger.LogInformation("Loaded project {ProjectName} with {DocCount} documents",
                    project.Name, project.Documents.Count());
            }

            var name = Path.GetFileNameWithoutExtension(path);
            instance._projects[name] = project;
        }

        // Sync our snapshot with the fully-loaded workspace solution
        instance._currentSolution = workspace.CurrentSolution;

        foreach (var diag in workspace.Diagnostics)
        {
            logger.LogWarning("MSBuild: {Message}", diag.Message);
        }

        logger.LogInformation("Workspace ready: {ProjectCount} projects loaded", instance._projects.Count);
        return instance;
    }

    /// <summary>Returns the named project, or throws if not found.</summary>
    public Project GetProject(string name)
    {
        if (!_projects.TryGetValue(name, out var project))
            throw new ArgumentException($"Project '{name}' not found. Available: {string.Join(", ", _projects.Keys)}");
        return project;
    }

    public IReadOnlyCollection<Project> AllProjects => _projects.Values;

    /// <summary>
    /// Returns the current solution snapshot after applying any pending file changes.
    /// </summary>
    public Solution Solution
    {
        get
        {
            ApplyPendingChanges();
            lock (_lock) return _currentSolution;
        }
    }

    /// <summary>
    /// Applies a solution change and updates the workspace.
    /// Used by rename to persist changes.
    /// </summary>
    public bool TryApplyChanges(Solution solution)
    {
        lock (_lock)
        {
            var result = _workspace.TryApplyChanges(solution);
            // Always update our snapshot — even if workspace rejected the disk write,
            // we want the in-memory solution to reflect the new state.
            _currentSolution = _workspace.CurrentSolution;
            return result;
        }
    }

    private static bool IsBuildOutputPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/") || normalized.Contains("/obj/");
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _workspace.Dispose();
    }
}
