using System.Collections.Concurrent;
using System.Diagnostics;
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
    // Separate watcher for MSBuild files. A .cs edit only changes a document's text, but a
    // build-file edit changes what the project *is* — its package and project references,
    // its compilation options. Those can only be recovered by re-evaluating MSBuild, so they
    // are tracked apart from the document-level changes above.
    private readonly FileSystemWatcher _buildFileWatcher;
    // Keyed by path rather than queued: ApplyPendingChanges collapses to the last change per file
    // anyway, so a queue would grow with the event count while an idle server never drains it.
    // Bounded by the number of distinct files touched, and by MaxPendingChanges beyond that.
    private readonly ConcurrentDictionary<string, ChangeKind> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxPendingChanges = 10_000;
    private readonly Lock _lock = new();
    private readonly ILogger<RoslynWorkspace> _logger;
    private Solution _currentSolution;
    private volatile bool _fullResyncNeeded;

    // Set when a build file changes; cleared by the reload on the next solution read.
    // The reload is lazy rather than eager because re-evaluating MSBuild costs seconds on a
    // large solution, and a burst of build-file writes (a restore, a branch switch) would
    // otherwise trigger one reload per file instead of a single reload before the next answer.
    private volatile bool _projectModelDirty;
    private int _projectModelReloadCount;
    private readonly Func<string, bool> _projectFilter;
    // Serializes reloads without holding _lock across the async MSBuild evaluation.
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

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

    private enum ChangeKind { Updated, Deleted }

    // Directory walks skip reparse points. Package managers (bun, pnpm) link package directories
    // back at their own ancestors, and following one makes discovery recurse until the process is
    // killed, with the walker's path stack growing every lap. DiscoveryTimeout catches any cycle
    // that survives that, so a runaway walk reports itself instead of never becoming ready.
    private static readonly EnumerationOptions ShallowOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        MatchType = MatchType.Simple,
    };
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMinutes(2);

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

    private RoslynWorkspace(MSBuildWorkspace workspace, string rootPath, ILogger<RoslynWorkspace> logger, Func<string, bool>? projectFilter)
    {
        RootPath = rootPath;
        _workspace = workspace;
        _logger = logger;
        _projectFilter = projectFilter ?? (_ => true);
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
        // don't want those swelling the pending set.
        _watcher.Changed += (_, e) => TrackChange(e.FullPath, ChangeKind.Updated);
        _watcher.Created += (_, e) => TrackChange(e.FullPath, ChangeKind.Updated);
        _watcher.Deleted += (_, e) => TrackChange(e.FullPath, ChangeKind.Deleted);
        _watcher.Renamed += (_, e) =>
        {
            TrackChange(e.OldFullPath, ChangeKind.Deleted);
            TrackChange(e.FullPath, ChangeKind.Updated);
        };
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "FileSystemWatcher buffer overflow — scheduling full resync");
            _fullResyncNeeded = true;
        };

        // Filter "*" rather than a per-extension watcher: FileSystemWatcher takes one pattern,
        // and IsBuildFile below is cheaper than running five overlapping watchers.
        _buildFileWatcher = new FileSystemWatcher(rootPath, "*")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            InternalBufferSize = 65536,
            EnableRaisingEvents = true
        };
        _buildFileWatcher.Changed += (_, e) => TrackBuildFileChange(e.FullPath);
        _buildFileWatcher.Created += (_, e) => TrackBuildFileChange(e.FullPath);
        _buildFileWatcher.Deleted += (_, e) => TrackBuildFileChange(e.FullPath);
        _buildFileWatcher.Renamed += (_, e) =>
        {
            TrackBuildFileChange(e.OldFullPath);
            TrackBuildFileChange(e.FullPath);
        };
        _buildFileWatcher.Error += (_, e) =>
        {
            // Losing build-file events silently is what left the project model stale in the
            // first place, so an overflow here forces a reload rather than being ignored.
            _logger.LogWarning(e.GetException(), "Build-file watcher buffer overflow — scheduling project model reload");
            _projectModelDirty = true;
        };
    }

    private void TrackBuildFileChange(string path)
    {
        var full = Path.GetFullPath(path);
        if (IsExcludedBelow(RootPath, full)) return;
        if (!IsBuildFile(full)) return;

        _logger.LogInformation("Build file changed ({Path}) — project model will reload on next use", full);
        _projectModelDirty = true;
    }

    /// <summary>
    /// True for files whose contents determine what a project references or how it compiles:
    /// the project and solution files themselves, and the central build files that MSBuild
    /// imports into every project without any csproj mentioning them.
    /// </summary>
    private static bool IsBuildFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);
    }

    private void TrackChange(string path, ChangeKind kind)
    {
        var full = Path.GetFullPath(path);
        if (IsExcludedBelow(RootPath, full)) return;
        _pendingChanges[full] = kind;

        if (_pendingChanges.Count <= MaxPendingChanges) return;
        _logger.LogWarning("More than {Max} files changed without a tool call — scheduling full resync", MaxPendingChanges);
        _fullResyncNeeded = true;
        _pendingChanges.Clear();
    }

    /// <summary>
    /// Enumerates files matching <paramref name="pattern"/> under <paramref name="root"/>, pruning
    /// excluded directories as it descends rather than filtering the results, and never following
    /// reparse points. Throws if the walk outlives <see cref="DiscoveryTimeout"/>.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string root, string pattern, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DiscoveryTimeout;
        var deadline = Stopwatch.StartNew();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            if (deadline.Elapsed >= limit)
                throw new TimeoutException(
                    $"Directory walk under '{root}' exceeded {limit.TotalSeconds:0}s. " +
                    "This usually means a directory cycle: a symlink or junction pointing back at an ancestor.");

            var dir = pending.Pop();

            foreach (var file in Directory.EnumerateFiles(dir, pattern, ShallowOptions))
                yield return file;

            foreach (var sub in Directory.EnumerateDirectories(dir, "*", ShallowOptions))
            {
                // IsExcludedBelow matches "/name/", so a directory needs its trailing separator.
                if (IsExcludedBelow(root, sub + Path.DirectorySeparatorChar)) continue;
                pending.Push(sub);
            }
        }
    }

    /// <summary>
    /// Flushes any pending file changes into the workspace solution so that
    /// subsequent reads see the latest content from disk.
    /// </summary>
    private void ApplyPendingChanges()
    {
        // The project model is reloaded before source changes are applied: a reload rebuilds
        // the solution from disk, so applying document edits first would just discard them.
        if (_projectModelDirty) ReloadProjectModel();

        if (_pendingChanges.IsEmpty && !_fullResyncNeeded) return;

        lock (_lock)
        {
            if (_fullResyncNeeded)
            {
                _fullResyncNeeded = false;
                // Drain stale changes
                _pendingChanges.Clear();
                FullResync();
                return;
            }

            if (_pendingChanges.IsEmpty) return;

            // Take the set, so a change arriving mid-apply survives for the next flush.
            var changes = new List<KeyValuePair<string, ChangeKind>>(_pendingChanges.Count);
            foreach (var path in _pendingChanges.Keys)
            {
                if (_pendingChanges.TryRemove(path, out var kind))
                    changes.Add(new(path, kind));
            }

            _logger.LogDebug("Applying {Count} pending file changes", changes.Count);

            var solution = _currentSolution;

            foreach (var (path, kind) in changes)
            {
                if (kind == ChangeKind.Deleted)
                {
                    var docId = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault();
                    if (docId is not null)
                        solution = solution.RemoveDocument(docId);
                }
                else
                {
                    solution = AddOrUpdateDocument(solution, path);
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
        foreach (var file in EnumerateFiles(RootPath, "*.cs"))
            diskFiles.Add(Path.GetFullPath(file));

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
            // File may be locked; retry on the next flush
            _pendingChanges[filePath] = ChangeKind.Updated;
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
        var instance = new RoslynWorkspace(workspace, rootPath, logger, projectFilter);
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
            await LoadProjectsAsync(projectFilter).ConfigureAwait(false);

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

    /// <summary>
    /// Evaluates the build files and populates the workspace with projects. Shared by the
    /// initial load and by <see cref="ReloadProjectModel"/>, so a reload discovers projects
    /// exactly the way startup did — including ones added since.
    /// </summary>
    private async Task LoadProjectsAsync(Func<string, bool>? projectFilter)
    {
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
                var filter = projectFilter ?? (_ => true);
                var csprojFiles = EnumerateFiles(RootPath, "*.csproj")
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
        }
    }

    /// <summary>
    /// Number of times the project model has been re-evaluated since startup. Exposed so tests
    /// can prove a reload happens when a build file changes — and, just as importantly, that an
    /// ordinary source edit does not trigger one.
    /// </summary>
    public int ProjectModelReloadCount => Volatile.Read(ref _projectModelReloadCount);

    /// <summary>
    /// Re-evaluates every project's build files, picking up packages, project references and
    /// compilation options added since the last load. Without this the project model is frozen
    /// at startup: a type from a dependency added later reports as missing until the server
    /// restarts. Source edits that are still pending survive, because they are re-applied from
    /// disk after the rebuild.
    /// </summary>
    private void ReloadProjectModel()
    {
        // The MSBuild re-evaluation runs OUTSIDE _lock. It is async work that resumes on thread
        // pool threads, and the loader itself takes _lock to publish each project — so blocking
        // on it while holding _lock deadlocks the continuation against its own caller. A separate
        // gate serializes reloads instead, and _lock is taken only for the short publish below.
        if (!_reloadGate.Wait(0))
        {
            // Another reload is already in flight. Its result supersedes ours, so wait for it
            // rather than queueing a second full evaluation behind it.
            _reloadGate.Wait();
            _reloadGate.Release();
            return;
        }

        try
        {
            // Re-check under the gate: the reload we may have queued behind already cleared this.
            if (!_projectModelDirty) return;
            _projectModelDirty = false;

            _logger.LogInformation("Reloading project model (build files changed)");
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // MSBuildWorkspace won't re-open a solution over existing projects, so the old
                // model has to go first. Compilations are dropped with it: they were built
                // against the stale references that caused the reload.
                _workspace.CloseSolution();
                _compilationCache.Clear();
                lock (_lock)
                {
                    _projects.Clear();
                }
                _loadedProjects = 0;
                _totalProjects = 0;

                LoadProjectsAsync(_projectFilter).GetAwaiter().GetResult();

                foreach (var diag in _workspace.Diagnostics)
                    _logger.LogWarning("MSBuild: {Message}", diag.Message);

                lock (_lock)
                {
                    _currentSolution = _workspace.CurrentSolution;

                    // The rebuilt solution reflects the build files, but any .cs written since
                    // the last flush is only on disk. A full resync reconciles those without
                    // needing the pending set, which the reload has just invalidated anyway.
                    _pendingChanges.Clear();
                    _fullResyncNeeded = false;
                    FullResync();
                }

                _logger.LogInformation("Project model reloaded in {Elapsed}ms ({Count} projects)",
                    stopwatch.ElapsedMilliseconds, _projects.Count);
            }
            catch (Exception ex)
            {
                // A half-written csproj (mid-save, or a merge in progress) must not take the
                // server down. Keep serving the previous model and retry on the next change.
                _logger.LogError(ex, "Project model reload failed — continuing with the previously loaded model");
            }
            finally
            {
                Interlocked.Increment(ref _projectModelReloadCount);
            }
        }
        finally
        {
            _reloadGate.Release();
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
        // Reload first: a project added to the solution after startup would otherwise be
        // reported as missing, listing only the projects that existed at load time.
        ApplyPendingChanges();
        lock (_lock)
        {
            if (!_projects.TryGetValue(name, out var project))
                throw new ArgumentException($"Project '{name}' not found. Available: {string.Join(", ", _projects.Keys)}");
            return project;
        }
    }

    public IReadOnlyCollection<Project> AllProjects
    {
        get
        {
            ApplyPendingChanges();
            lock (_lock) return _projects.Values.ToArray();
        }
    }

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
    /// Updates the in-memory solution snapshot to a post-rename solution without letting
    /// MSBuildWorkspace write to disk. The caller is responsible for persisting files itself
    /// (see <see cref="Tools.RefactoringTools"/>), which avoids ApplyChanges relocating renamed
    /// documents to the project root. The FileSystemWatcher still reconciles the same edits, so
    /// this just lets reads see the new state immediately instead of waiting for the next flush.
    /// </summary>
    public void SyncRenamedSolution(Solution solution)
    {
        lock (_lock) _currentSolution = solution;
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

    /// <summary>
    /// Excludes only what lies beneath <paramref name="root"/>. The root itself may sit inside a
    /// directory the names above would exclude — a workspace opened under someone's build output,
    /// for instance — and that must not prune the whole tree.
    /// </summary>
    public static bool IsExcludedBelow(string root, string path) =>
        IsExcludedPath(Path.DirectorySeparatorChar + Path.GetRelativePath(root, path));

    public void Dispose()
    {
        _watcher.Dispose();
        _buildFileWatcher.Dispose();
        _reloadGate.Dispose();
        _workspace.Dispose();
    }
}
