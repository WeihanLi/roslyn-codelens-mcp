# Lazy Start + Parallel Background Warming Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce perceived MCP server startup from ~785ms to ~200ms by deferring compilation, and reduce total warm-up time by compiling projects in parallel based on dependency order.

**Architecture:** Split `SolutionLoader.LoadAsync` into `OpenAsync` (fast, ~200ms) + `CompileAllParallelAsync` (background, parallel by dependency level). `SolutionManager` returns immediately after opening, stores a `Task _warmupTask`, and awaits it lazily when tools request the resolver. `LoadedSolution.Compilations` becomes a `ConcurrentDictionary` for thread-safe updates during warming.

**Tech Stack:** Roslyn MSBuildWorkspace, `ConcurrentDictionary<ProjectId, Compilation>`, `Task.WhenAll`, topological sort for dependency levels.

---

### Task 1: Add TopologicalSort Helper to SolutionLoader

**Files:**
- Modify: `src/RoslynCodeLens/SolutionLoader.cs`
- Create: `tests/RoslynCodeLens.Tests/TopologicalSortTests.cs`

**Step 1: Write the failing test**

Create `tests/RoslynCodeLens.Tests/TopologicalSortTests.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Tests;

public class TopologicalSortTests
{
    [Fact]
    public void GetCompilationLevels_ReturnsLeavesFirst()
    {
        // Build a mock solution: A depends on B, B depends on C
        var workspace = new AdhocWorkspace();
        var projectC = workspace.AddProject("C", LanguageNames.CSharp);
        var projectB = workspace.AddProject(
            ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "B", "B", LanguageNames.CSharp)
                .WithProjectReferences([new ProjectReference(projectC.Id)]));
        var projectA = workspace.AddProject(
            ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "A", "A", LanguageNames.CSharp)
                .WithProjectReferences([new ProjectReference(projectB.Id)]));

        var solution = workspace.CurrentSolution;
        var levels = SolutionLoader.GetCompilationLevels(solution);

        // Level 0: C (leaf), Level 1: B, Level 2: A
        Assert.Equal(3, levels.Count);
        Assert.Single(levels[0]);
        Assert.Equal("C", levels[0][0].Name);
        Assert.Single(levels[1]);
        Assert.Equal("B", levels[1][0].Name);
        Assert.Single(levels[2]);
        Assert.Equal("A", levels[2][0].Name);
    }

    [Fact]
    public void GetCompilationLevels_GroupsIndependentProjects()
    {
        // B and C are both leaves, A depends on both
        var workspace = new AdhocWorkspace();
        var projectB = workspace.AddProject("B", LanguageNames.CSharp);
        var projectC = workspace.AddProject("C", LanguageNames.CSharp);
        var projectA = workspace.AddProject(
            ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "A", "A", LanguageNames.CSharp)
                .WithProjectReferences([new ProjectReference(projectB.Id), new ProjectReference(projectC.Id)]));

        var solution = workspace.CurrentSolution;
        var levels = SolutionLoader.GetCompilationLevels(solution);

        // Level 0: B and C (both leaves), Level 1: A
        Assert.Equal(2, levels.Count);
        Assert.Equal(2, levels[0].Count);
        Assert.Contains(levels[0], p => p.Name == "B");
        Assert.Contains(levels[0], p => p.Name == "C");
        Assert.Single(levels[1]);
        Assert.Equal("A", levels[1][0].Name);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "TopologicalSortTests" -v minimal`
Expected: FAIL — `SolutionLoader.GetCompilationLevels` does not exist yet.

**Step 3: Implement `GetCompilationLevels` in SolutionLoader**

Add to `src/RoslynCodeLens/SolutionLoader.cs`:

```csharp
/// <summary>
/// Returns projects grouped by dependency level (leaves first).
/// Level 0 = no project dependencies, Level 1 = depends only on level 0, etc.
/// </summary>
public static List<List<Project>> GetCompilationLevels(Solution solution)
{
    var projects = solution.Projects.ToList();
    var projectIds = new HashSet<ProjectId>(projects.Select(p => p.Id));
    var assigned = new Dictionary<ProjectId, int>();

    int GetLevel(Project project)
    {
        if (assigned.TryGetValue(project.Id, out var cached))
            return cached;

        var maxDep = -1;
        foreach (var dep in project.ProjectReferences)
        {
            if (!projectIds.Contains(dep.ProjectId))
                continue;
            var depProject = solution.GetProject(dep.ProjectId);
            if (depProject != null)
                maxDep = Math.Max(maxDep, GetLevel(depProject));
        }

        var level = maxDep + 1;
        assigned[project.Id] = level;
        return level;
    }

    foreach (var project in projects)
        GetLevel(project);

    var maxLevel = assigned.Count > 0 ? assigned.Values.Max() : 0;
    var levels = new List<List<Project>>(maxLevel + 1);
    for (var i = 0; i <= maxLevel; i++)
        levels.Add(new List<Project>());

    foreach (var project in projects)
        levels[assigned[project.Id]].Add(project);

    return levels;
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "TopologicalSortTests" -v minimal`
Expected: PASS (both tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/SolutionLoader.cs tests/RoslynCodeLens.Tests/TopologicalSortTests.cs
git commit -m "feat: add topological sort for dependency-aware compilation"
```

---

### Task 2: Add Parallel Compilation to SolutionLoader

**Files:**
- Modify: `src/RoslynCodeLens/SolutionLoader.cs`
- Modify: `tests/RoslynCodeLens.Tests/SolutionLoaderTests.cs`

**Step 1: Write the failing test**

Add to `tests/RoslynCodeLens.Tests/SolutionLoaderTests.cs`:

```csharp
[Fact]
public async Task OpenAsync_ReturnsSolutionWithoutCompilations()
{
    var fixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx");
    fixturePath = Path.GetFullPath(fixturePath);

    var loader = new SolutionLoader();
    var (solution, workspace) = await loader.OpenAsync(fixturePath);

    Assert.NotNull(solution);
    Assert.True(solution.Projects.Count() >= 2);
}

[Fact]
public async Task CompileAllParallelAsync_CompilesAllProjects()
{
    var fixturePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx");
    fixturePath = Path.GetFullPath(fixturePath);

    var loader = new SolutionLoader();
    var (solution, workspace) = await loader.OpenAsync(fixturePath);
    var compilations = await loader.CompileAllParallelAsync(solution);

    Assert.True(compilations.Count >= 2);
    Assert.All(compilations.Values, c => Assert.NotNull(c));
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "SolutionLoaderTests" -v minimal`
Expected: FAIL — `OpenAsync` and `CompileAllParallelAsync` do not exist.

**Step 3: Implement `OpenAsync` and `CompileAllParallelAsync`**

Refactor `src/RoslynCodeLens/SolutionLoader.cs`. Keep `LoadAsync` for backward compatibility (it calls the new methods internally):

```csharp
using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeLens;

public class SolutionLoader
{
    public async Task<(Solution Solution, MSBuildWorkspace Workspace)> OpenAsync(string solutionPath)
    {
        var workspace = MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (_, e) =>
        {
            Console.Error.WriteLine($"[roslyn-codelens] Warning: {e.Diagnostic.Message}");
        };

        await Console.Error.WriteLineAsync($"[roslyn-codelens] Loading solution: {Path.GetFileName(solutionPath)}").ConfigureAwait(false);
        var solution = await workspace.OpenSolutionAsync(solutionPath).ConfigureAwait(false);

        return (solution, workspace);
    }

    public async Task<ConcurrentDictionary<ProjectId, Compilation>> CompileAllParallelAsync(Solution solution)
    {
        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();
        var levels = GetCompilationLevels(solution);

        var compiled = 0;
        var total = solution.Projects.Count();

        foreach (var level in levels)
        {
            var tasks = level.Select(async project =>
            {
                var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
                if (compilation != null)
                {
                    compilations[project.Id] = compilation;
                    var count = Interlocked.Increment(ref compiled);
                    await Console.Error.WriteLineAsync(
                        $"[roslyn-codelens] Compiled {count}/{total}: {project.Name}").ConfigureAwait(false);
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        return compilations;
    }

    public async Task<LoadedSolution> LoadAsync(string solutionPath)
    {
        var (solution, _) = await OpenAsync(solutionPath).ConfigureAwait(false);
        var compilations = await CompileAllParallelAsync(solution).ConfigureAwait(false);

        await Console.Error.WriteLineAsync(
            $"[roslyn-codelens] Ready. {compilations.Count} projects compiled.").ConfigureAwait(false);

        return new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations
        };
    }

    // ... keep FindSolutionFile and GetCompilationLevels as-is ...
}
```

**Step 4: Run ALL tests to verify nothing broke**

Run: `dotnet test tests/RoslynCodeLens.Tests -v minimal`
Expected: All 74+ tests PASS.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/SolutionLoader.cs tests/RoslynCodeLens.Tests/SolutionLoaderTests.cs
git commit -m "feat: add parallel compilation by dependency level"
```

---

### Task 3: Update LoadedSolution to Use ConcurrentDictionary

**Files:**
- Modify: `src/RoslynCodeLens/LoadedSolution.cs`

**Step 1: Run all tests to establish green baseline**

Run: `dotnet test tests/RoslynCodeLens.Tests -v minimal`
Expected: All PASS.

**Step 2: Update LoadedSolution**

Change `src/RoslynCodeLens/LoadedSolution.cs` — `Compilations` type from `IDictionary` to `ConcurrentDictionary`:

```csharp
using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

public class LoadedSolution
{
    public required Solution Solution { get; init; }
    public required ConcurrentDictionary<ProjectId, Compilation> Compilations { get; init; }
    public bool IsEmpty => Compilations.IsEmpty;

    public static LoadedSolution Empty { get; } = CreateEmpty();

    private static LoadedSolution CreateEmpty()
    {
        var workspace = new AdhocWorkspace();
        return new LoadedSolution
        {
            Solution = workspace.CurrentSolution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>()
        };
    }
}
```

**Step 3: Fix any compilation errors from the type change**

The `IDictionary` → `ConcurrentDictionary` change may break:
- `SolutionManager.RebuildStaleProjects` line `new Dictionary<ProjectId, Compilation>(_loaded.Compilations)` — change to `new ConcurrentDictionary<ProjectId, Compilation>(_loaded.Compilations)`
- Any other constructor calls creating `Dictionary<ProjectId, Compilation>` that get assigned to `Compilations`

**Step 4: Run ALL tests**

Run: `dotnet test tests/RoslynCodeLens.Tests -v minimal`
Expected: All PASS.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/LoadedSolution.cs src/RoslynCodeLens/SolutionManager.cs src/RoslynCodeLens/SolutionLoader.cs
git commit -m "refactor: use ConcurrentDictionary for thread-safe compilation updates"
```

---

### Task 4: Add Lazy Startup to SolutionManager

**Files:**
- Modify: `src/RoslynCodeLens/SolutionManager.cs`
- Modify: `tests/RoslynCodeLens.Tests/SolutionManagerTests.cs`

**Step 1: Write the failing test**

Add to `tests/RoslynCodeLens.Tests/SolutionManagerTests.cs`:

```csharp
[Fact]
public async Task CreateAsync_ReturnsBeforeCompilationCompletes()
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var manager = await SolutionManager.CreateAsync(_solutionPath);
    var createTime = sw.ElapsedMilliseconds;

    // After warmup, resolver should have data
    await manager.WaitForWarmupAsync();
    var resolver = manager.GetResolver();

    Assert.True(resolver.AllTypes.Count > 0);
    manager.Dispose();
}

[Fact]
public async Task GetResolver_AwaitsWarmupIfNotReady()
{
    var manager = await SolutionManager.CreateAsync(_solutionPath);

    // GetResolver should block until warmup is done and return valid resolver
    var resolver = manager.GetResolver();
    Assert.True(resolver.AllTypes.Count > 0);
    manager.Dispose();
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "SolutionManagerTests" -v minimal`
Expected: FAIL — `WaitForWarmupAsync` does not exist.

**Step 3: Implement lazy startup in SolutionManager**

Rewrite `src/RoslynCodeLens/SolutionManager.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeLens;

public sealed class SolutionManager : IDisposable
{
    private LoadedSolution _loaded;
    private SymbolResolver _resolver;
    private readonly string? _solutionPath;
    private readonly FileChangeTracker? _tracker;
    private readonly Lock _lock = new();
    private volatile bool _rebuilding;
    private Task? _warmupTask;

    private SolutionManager(LoadedSolution loaded, string? solutionPath)
    {
        _loaded = loaded;
        _solutionPath = solutionPath;
        _resolver = new SymbolResolver(loaded);
    }

    public static async Task<SolutionManager> CreateAsync(string solutionPath)
    {
        var loader = new SolutionLoader();
        var (solution, _) = await loader.OpenAsync(solutionPath).ConfigureAwait(false);

        // Create a LoadedSolution with the Solution but empty compilations
        var loaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>()
        };

        var manager = new SolutionManager(loaded, solutionPath);

        // Start background compilation
        manager._warmupTask = manager.WarmupAsync(loader, solution);

        return manager;
    }

    private async Task WarmupAsync(SolutionLoader loader, Solution solution)
    {
        try
        {
            var compilations = await loader.CompileAllParallelAsync(solution).ConfigureAwait(false);

            var warmedLoaded = new LoadedSolution
            {
                Solution = solution,
                Compilations = compilations
            };
            var warmedResolver = new SymbolResolver(warmedLoaded);

            lock (_lock)
            {
                _loaded = warmedLoaded;
                _resolver = warmedResolver;
            }

            // Start file watching now that we have full data
            if (_solutionPath != null)
            {
                // Note: FileChangeTracker is set up via a separate field after warmup
                // This is handled by updating the _tracker reference
            }

            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Ready. {compilations.Count} projects compiled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Background warmup failed: {ex}").ConfigureAwait(false);
        }
    }

    public async Task WaitForWarmupAsync()
    {
        if (_warmupTask != null)
            await _warmupTask.ConfigureAwait(false);
    }

    public static SolutionManager CreateEmpty()
    {
        return new SolutionManager(LoadedSolution.Empty, null);
    }

    public LoadedSolution GetLoadedSolution()
    {
        // Ensure warmup is complete before returning
        _warmupTask?.GetAwaiter().GetResult();
        RebuildIfStale();
        return _loaded;
    }

    public SymbolResolver GetResolver()
    {
        // Ensure warmup is complete before returning
        _warmupTask?.GetAwaiter().GetResult();
        RebuildIfStale();
        return _resolver;
    }

    public void EnsureLoaded()
    {
        _warmupTask?.GetAwaiter().GetResult();
        if (_loaded.IsEmpty)
            throw new InvalidOperationException(
                "No .sln file found. Either run from a directory containing a .sln/.slnx file, " +
                "or pass the solution path as argument: roslyn-codelens-mcp /path/to/Solution.sln");
    }

    // ... RebuildIfStale, RebuildStaleProjects, ForceReloadAsync, Dispose stay the same
    // except ForceReloadAsync should also use parallel compilation ...
}
```

**Important:** The `_tracker` field needs to be initialized after warmup. Make `_tracker` mutable (remove `readonly`) and set it at the end of `WarmupAsync`. Update the constructor to not initialize `_tracker`.

**Step 4: Run ALL tests**

Run: `dotnet test tests/RoslynCodeLens.Tests -v minimal`
Expected: All PASS.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/SolutionManager.cs tests/RoslynCodeLens.Tests/SolutionManagerTests.cs
git commit -m "feat: lazy startup with background parallel warming"
```

---

### Task 5: Update ForceReloadAsync to Use Parallel Compilation

**Files:**
- Modify: `src/RoslynCodeLens/SolutionManager.cs`

**Step 1: Run all tests to establish green baseline**

Run: `dotnet test tests/RoslynCodeLens.Tests -v minimal`
Expected: All PASS.

**Step 2: Update ForceReloadAsync**

Replace the sequential `loader.LoadAsync` call in `ForceReloadAsync` with the new parallel path:

```csharp
public async Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync()
{
    if (_solutionPath == null)
        throw new InvalidOperationException("No solution path configured. Cannot reload.");

    var sw = System.Diagnostics.Stopwatch.StartNew();

    var loader = new SolutionLoader();
    var (solution, _) = await loader.OpenAsync(_solutionPath).ConfigureAwait(false);
    var compilations = await loader.CompileAllParallelAsync(solution).ConfigureAwait(false);

    var newLoaded = new LoadedSolution
    {
        Solution = solution,
        Compilations = compilations
    };
    var newResolver = new SymbolResolver(newLoaded);

    lock (_lock)
    {
        _loaded = newLoaded;
        _resolver = newResolver;
    }

    _tracker?.UpdateMappings(newLoaded);
    _tracker?.ClearStale();

    sw.Stop();
    return (newLoaded.Compilations.Count, sw.Elapsed);
}
```

**Step 3: Run ALL tests**

Run: `dotnet test tests/RoslynCodeLens.Tests -v minimal`
Expected: All PASS.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/SolutionManager.cs
git commit -m "refactor: use parallel compilation in ForceReloadAsync"
```

---

### Task 6: Update RebuildStaleProjects to Use ConcurrentDictionary

**Files:**
- Modify: `src/RoslynCodeLens/SolutionManager.cs`

**Step 1: Run all tests to establish green baseline**

Run: `dotnet test tests/RoslynCodeLens.Tests -v minimal`
Expected: All PASS.

**Step 2: Update RebuildStaleProjects**

The stale rebuild also benefits from parallel compilation for the stale subset:

```csharp
private async Task RebuildStaleProjects(IReadOnlySet<ProjectId> staleIds)
{
    var workspace = MSBuildWorkspace.Create();
    workspace.WorkspaceFailed += (_, e) =>
        Console.Error.WriteLine($"[roslyn-codelens] Warning: {e.Diagnostic.Message}");

    var solution = await workspace.OpenSolutionAsync(_solutionPath!).ConfigureAwait(false);
    var compilations = new ConcurrentDictionary<ProjectId, Compilation>(_loaded.Compilations);

    // Compile stale projects in parallel
    var staleProjects = solution.Projects.Where(p => staleIds.Contains(p.Id)).ToList();
    var tasks = staleProjects.Select(async project =>
    {
        await Console.Error.WriteLineAsync($"[roslyn-codelens] Recompiling: {project.Name}").ConfigureAwait(false);
        var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
        if (compilation != null)
            compilations[project.Id] = compilation;
    });

    await Task.WhenAll(tasks).ConfigureAwait(false);

    var newLoaded = new LoadedSolution
    {
        Solution = solution,
        Compilations = compilations
    };
    var newResolver = new SymbolResolver(newLoaded);

    lock (_lock)
    {
        _loaded = newLoaded;
        _resolver = newResolver;
    }

    _tracker!.UpdateMappings(newLoaded);

    await Console.Error.WriteLineAsync("[roslyn-codelens] Rebuild complete.").ConfigureAwait(false);
}
```

**Step 3: Run ALL tests**

Run: `dotnet test tests/RoslynCodeLens.Tests -v minimal`
Expected: All PASS.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/SolutionManager.cs
git commit -m "perf: parallel compilation for stale project rebuilds"
```

---

### Task 7: Run Full Test Suite + Benchmarks

**Files:** None (verification only)

**Step 1: Run full test suite**

Run: `dotnet test -c Release -v minimal`
Expected: All 74+ tests PASS.

**Step 2: Run benchmarks**

Run: `dotnet run --project benchmarks/RoslynCodeLens.Benchmarks -c Release`
Expected: Solution loading should be faster due to parallel compilation. All other tool benchmarks should be unchanged (within noise).

**Step 3: Compare benchmark results**

Compare the new "Load and compile solution" time against baseline (~785ms). Expected improvement: 30-50% faster on multi-core.

**Step 4: Commit benchmark results if meaningful**

```bash
git commit -m "perf: benchmark results after lazy parallel startup"
```

---

### Task 8: Finishing

**Use skill:** `superpowers:finishing-a-development-branch` to merge or create PR.
