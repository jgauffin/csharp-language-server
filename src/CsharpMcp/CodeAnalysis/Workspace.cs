using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

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
    private Solution _currentSolution;

    // projectName (no extension) -> Project
    private readonly Dictionary<string, Project> _projects = new(StringComparer.OrdinalIgnoreCase);

    private enum ChangeKind { Updated, Deleted, Renamed }
    private readonly record struct FileChange(string FullPath, ChangeKind Kind, string? OldFullPath = null);

    static RoslynWorkspace()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    private RoslynWorkspace(MSBuildWorkspace workspace, string rootPath)
    {
        _workspace = workspace;
        _currentSolution = workspace.CurrentSolution;
        _watcher = new FileSystemWatcher(rootPath, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, e) => _pendingChanges.Enqueue(new(Path.GetFullPath(e.FullPath), ChangeKind.Updated));
        _watcher.Created += (_, e) => _pendingChanges.Enqueue(new(Path.GetFullPath(e.FullPath), ChangeKind.Updated));
        _watcher.Deleted += (_, e) => _pendingChanges.Enqueue(new(Path.GetFullPath(e.FullPath), ChangeKind.Deleted));
        _watcher.Renamed += (_, e) => _pendingChanges.Enqueue(
            new(Path.GetFullPath(e.FullPath), ChangeKind.Renamed, Path.GetFullPath(e.OldFullPath)));
    }

    /// <summary>
    /// Flushes any pending file changes into the workspace solution so that
    /// subsequent reads see the latest content from disk.
    /// </summary>
    private void ApplyPendingChanges()
    {
        if (_pendingChanges.IsEmpty) return;

        lock (_lock)
        {
            if (_pendingChanges.IsEmpty) return;

            var changes = new List<FileChange>();
            while (_pendingChanges.TryDequeue(out var change))
                changes.Add(change);

            var solution = _currentSolution;
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var change in changes)
            {
                if (!processed.Add(change.FullPath)) continue;

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
                        // Remove old path, then fall through to add/update new path
                        if (change.OldFullPath is not null)
                        {
                            var oldDocId = solution.GetDocumentIdsWithFilePath(change.OldFullPath).FirstOrDefault();
                            if (oldDocId is not null)
                                solution = solution.RemoveDocument(oldDocId);
                        }
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

    public static async Task<RoslynWorkspace> LoadAsync(string rootPath)
    {
        var workspace = MSBuildWorkspace.Create();
        var instance = new RoslynWorkspace(workspace, rootPath);

        var csprojFiles = Directory.GetFiles(rootPath, "*.csproj", SearchOption.AllDirectories);
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
            }
            else
            {
                project = await workspace.OpenProjectAsync(path);
            }

            var name = Path.GetFileNameWithoutExtension(path);
            instance._projects[name] = project;
        }

        // Sync our snapshot with the fully-loaded workspace solution
        instance._currentSolution = workspace.CurrentSolution;
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

    public void Dispose()
    {
        _watcher.Dispose();
        _workspace.Dispose();
    }
}
