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

    // Background loading state. Tools check IsReady before touching the solution so
    // MCP requests return a fast "still loading" response instead of timing out.
    // Readiness flips true once projects are loaded; compilation warming continues in
    // the background after that — tools work without it, they just pay first-call cost.
    private volatile bool _isReady;
    private volatile bool _isWarmed;
    private int _loadedProjects;
    private int _totalProjects;
    private volatile string? _loadError;
    private Task _readyTask = Task.CompletedTask;

    // Bounded LRU cache of compilations. Each Compilation pins all parsed syntax trees and
    // metadata references for its project, so for large solutions this is the biggest
    // sustained memory cost. Capping the cache at MaxCachedCompilations keeps memory in check;
    // evicted projects pay a re-compile on next use.
    private const int MaxCachedCompilations = 8;
    private sealed class CacheEntry
    {
        public VersionStamp Version;
        public Compilation Compilation = null!;
        public long LastAccessTick;
    }
    private readonly ConcurrentDictionary<ProjectId, CacheEntry> _compilationCache = new();
    private long _accessTick;
    private readonly Lock _evictionLock = new();

    // Per-project locks to prevent concurrent compilations of the same project.
    // Without this, parallel tool calls each trigger their own compilation.
    private readonly ConcurrentDictionary<ProjectId, SemaphoreSlim> _compilationLocks = new();

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

    public bool IsReady => _isReady;
    public Task WhenReady => _readyTask;
    public string LoadingStatus
    {
        get
        {
            if (_loadError is not null) return $"Workspace load failed: {_loadError}";
            if (_isReady)
            {
                return _isWarmed
                    ? $"Workspace ready ({_totalProjects} projects loaded, compilations warmed)"
                    : $"Workspace ready ({_totalProjects} projects loaded, compilations warming in background)";
            }
            var total = _totalProjects;
            var loaded = _loadedProjects;
            return total == 0
                ? "Workspace loading: discovering projects..."
                : $"Workspace loading: {loaded}/{total} projects loaded";
        }
    }

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
        // Filter at the source: in a mixed-language repo the watcher fires for .cs files
        // anywhere under root (e.g. inside bin/, obj/, or a transitive node_modules), and we
        // don't want those swelling the pending queue.
        _watcher.Changed += (_, e) => EnqueueIfTracked(e.FullPath, ChangeKind.Updated);
        _watcher.Created += (_, e) => EnqueueIfTracked(e.FullPath, ChangeKind.Updated);
        _watcher.Deleted += (_, e) => EnqueueIfTracked(e.FullPath, ChangeKind.Deleted);
        _watcher.Renamed += (_, e) =>
        {
            var full = Path.GetFullPath(e.FullPath);
            if (IsExcludedPath(full)) return;
            _pendingChanges.Enqueue(new(full, ChangeKind.Renamed, Path.GetFullPath(e.OldFullPath)));
        };
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "FileSystemWatcher buffer overflow — scheduling full resync");
            _fullResyncNeeded = true;
        };
    }

    private void EnqueueIfTracked(string path, ChangeKind kind)
    {
        var full = Path.GetFullPath(path);
        if (IsExcludedPath(full)) return;
        _pendingChanges.Enqueue(new(full, kind));
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
        _compilationCache.Clear();

        var solution = _currentSolution;

        // Collect all .cs files currently on disk
        var diskFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(RootPath, "*.cs", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(file);
            if (IsExcludedPath(fullPath)) continue;
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

    /// <summary>
    /// Creates the workspace and kicks off project loading + compilation warming on a background task.
    /// Returns immediately so MCP can start serving requests; tools check <see cref="IsReady"/> and
    /// return a "still loading" response until the background task completes.
    /// </summary>
    public static RoslynWorkspace Create(string rootPath, ILoggerFactory? loggerFactory = null, Func<string, bool>? projectFilter = null)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RoslynWorkspace>();
        var workspace = MSBuildWorkspace.Create();
        var instance = new RoslynWorkspace(workspace, rootPath, logger);
        instance._readyTask = Task.Run(() => instance.LoadInBackgroundAsync(projectFilter));
        return instance;
    }

    /// <summary>
    /// Creates the workspace and awaits full project load + compilation warming before returning.
    /// Use <see cref="Create"/> in production to avoid blocking MCP startup; this overload is kept
    /// for tests and tooling that need a fully-loaded workspace synchronously.
    /// </summary>
    public static async Task<RoslynWorkspace> LoadAsync(string rootPath, ILoggerFactory? loggerFactory = null, Func<string, bool>? projectFilter = null)
    {
        var instance = Create(rootPath, loggerFactory, projectFilter);
        await instance.WhenReady;
        return instance;
    }

    private async Task LoadInBackgroundAsync(Func<string, bool>? projectFilter)
    {
        try
        {
            // Prefer a solution file at the root when present — it's authoritative about which
            // projects belong to the workspace, avoids a recursive directory walk, and won't
            // accidentally pick up unrelated csprojs from sibling subtrees.
            var solutionFile = FindSolutionFile(RootPath);
            if (solutionFile is not null)
            {
                _logger.LogInformation("Loading solution {SolutionPath}", solutionFile);
                await _workspace.OpenSolutionAsync(solutionFile).ConfigureAwait(false);

                var projects = _workspace.CurrentSolution.Projects.ToArray();
                _totalProjects = projects.Length;
                foreach (var project in projects)
                {
                    var key = project.Name;
                    if (project.FilePath is not null)
                        key = Path.GetFileNameWithoutExtension(project.FilePath);
                    lock (_lock)
                    {
                        _projects[key] = project;
                    }
                    Interlocked.Increment(ref _loadedProjects);
                }
                _logger.LogInformation("Loaded {Count} projects from solution", projects.Length);
            }
            else
            {
                var filter = projectFilter ?? (p => !IsExcludedPath(p));
                var csprojFiles = Directory.GetFiles(RootPath, "*.csproj", SearchOption.AllDirectories)
                    .Where(filter)
                    .ToArray();
                _totalProjects = csprojFiles.Length;
                _logger.LogInformation("Found {Count} .csproj files under {RootPath}", csprojFiles.Length, RootPath);

                foreach (var path in csprojFiles)
                {
                    var fullPath = Path.GetFullPath(path);
                    var alreadyLoaded = _workspace.CurrentSolution.Projects
                        .Any(p => string.Equals(p.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

                    Project project;
                    if (alreadyLoaded)
                    {
                        project = _workspace.CurrentSolution.Projects
                            .First(p => string.Equals(p.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
                        _logger.LogDebug("Project already loaded: {ProjectPath}", fullPath);
                    }
                    else
                    {
                        _logger.LogInformation("Loading project: {ProjectPath}", fullPath);
                        project = await _workspace.OpenProjectAsync(path).ConfigureAwait(false);
                        _logger.LogInformation("Loaded project {ProjectName} with {DocCount} documents",
                            project.Name, project.Documents.Count());
                    }

                    var name = Path.GetFileNameWithoutExtension(path);
                    lock (_lock)
                    {
                        _projects[name] = project;
                    }
                    Interlocked.Increment(ref _loadedProjects);
                }
            }

            lock (_lock)
            {
                _currentSolution = _workspace.CurrentSolution;
            }

            foreach (var diag in _workspace.Diagnostics)
            {
                _logger.LogWarning("MSBuild: {Message}", diag.Message);
            }

            _isReady = true;
            _logger.LogInformation("Workspace ready: {ProjectCount} projects loaded, warming compilations in background", _projects.Count);

            // Fire-and-forget compilation warming. Tools don't need this to be complete;
            // they just pay the compilation cost on first use without it.
            _ = Task.Run(WarmCompilationsInBackgroundAsync);
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            _logger.LogError(ex, "Workspace background load failed");
            throw;
        }
    }

    private async Task WarmCompilationsInBackgroundAsync()
    {
        // Sequential, not parallel: a parallel warm would hold every project's Compilation
        // in memory simultaneously before the LRU could trim, spiking RAM badly on large
        // solutions. Sequential warming + LRU eviction keeps the peak bounded.
        try
        {
            var solution = _currentSolution;
            int count = 0;
            foreach (var project in solution.Projects)
            {
                await GetCompilationAsync(project).ConfigureAwait(false);
                count++;
            }
            _isWarmed = true;
            _logger.LogInformation("Warmed compilations for {Count} projects (cache bounded to {Cap} most-recent)", count, MaxCachedCompilations);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Compilation warming failed (non-fatal — tools still work, just slower on first use)");
        }
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
    /// Returns a (possibly cached) compilation for the given project.
    /// The cache key includes the project's transitive dependency version,
    /// so a cache hit means neither this project nor any of its dependencies
    /// have changed since the last compilation.
    /// Uses per-project locking so concurrent callers share one compilation
    /// instead of each triggering their own. The cache is bounded; least-recently-used
    /// entries are evicted when it exceeds MaxCachedCompilations.
    /// </summary>
    public async Task<Compilation?> GetCompilationAsync(Project project)
    {
        var version = await project.GetDependentVersionAsync();

        if (_compilationCache.TryGetValue(project.Id, out var cached) && cached.Version == version)
        {
            cached.LastAccessTick = Interlocked.Increment(ref _accessTick);
            return cached.Compilation;
        }

        var sem = _compilationLocks.GetOrAdd(project.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            // Re-check after acquiring lock — another caller may have compiled while we waited
            if (_compilationCache.TryGetValue(project.Id, out cached) && cached.Version == version)
            {
                cached.LastAccessTick = Interlocked.Increment(ref _accessTick);
                return cached.Compilation;
            }

            var compilation = await project.GetCompilationAsync();
            if (compilation is not null)
            {
                _compilationCache[project.Id] = new CacheEntry
                {
                    Version = version,
                    Compilation = compilation,
                    LastAccessTick = Interlocked.Increment(ref _accessTick),
                };
                EvictIfNeeded();
            }
            return compilation;
        }
        finally
        {
            sem.Release();
        }
    }

    private void EvictIfNeeded()
    {
        if (_compilationCache.Count <= MaxCachedCompilations) return;
        lock (_evictionLock)
        {
            while (_compilationCache.Count > MaxCachedCompilations)
            {
                ProjectId? oldestId = null;
                long oldestTick = long.MaxValue;
                foreach (var kv in _compilationCache)
                {
                    if (kv.Value.LastAccessTick < oldestTick)
                    {
                        oldestTick = kv.Value.LastAccessTick;
                        oldestId = kv.Key;
                    }
                }
                if (oldestId is null) break;
                _compilationCache.TryRemove(oldestId, out _);
            }
        }
    }

    /// <summary>
    /// Pre-compiles all projects so that subsequent tool calls hit the cache.
    /// Call after workspace load to avoid paying compilation cost on first tool use.
    /// Note: only the <see cref="MaxCachedCompilations"/> most-recently-touched projects
    /// remain cached at the end; older ones are evicted.
    /// </summary>
    public async Task WarmCompilationsAsync()
    {
        var solution = Solution;
        foreach (var project in solution.Projects)
            await GetCompilationAsync(project);
        _logger.LogInformation("Warmed compilations for {Count} projects (cache bounded to {Cap} most-recent)", solution.Projects.Count(), MaxCachedCompilations);
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

    /// <summary>
    /// Returns the path to a solution file at the workspace root, or null if none exist.
    /// Checks only the top-level directory — nested solutions (in samples, tests-of-tests,
    /// etc.) are intentionally ignored.
    /// </summary>
    private static string? FindSolutionFile(string rootPath)
    {
        foreach (var pattern in new[] { "*.slnx", "*.sln", "*.slnf" })
        {
            var match = Directory.EnumerateFiles(rootPath, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match is not null) return match;
        }
        return null;
    }

    /// <summary>
    /// Paths to skip during project discovery and file-change tracking.
    /// Includes build output, package caches, IDE/SCM metadata, and the JS ecosystem's node_modules
    /// — which can otherwise dominate a recursive walk in mixed-language repos.
    /// </summary>
    public static bool IsExcludedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/")
            || normalized.Contains("/obj/")
            || normalized.Contains("/node_modules/")
            || normalized.Contains("/.git/")
            || normalized.Contains("/.vs/")
            || normalized.Contains("/.idea/")
            || normalized.Contains("/packages/");
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _workspace.Dispose();
    }
}
