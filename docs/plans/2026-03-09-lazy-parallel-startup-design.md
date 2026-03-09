# Lazy Start + Parallel Background Warming

## Problem

The MCP server takes ~785ms to start because it sequentially compiles all projects before serving requests. On large solutions this could be multiple seconds.

## Goal

Reduce perceived startup to ~200ms by deferring compilation, and reduce total warm-up time by compiling projects in parallel.

## Architecture

### Phase 1 — Quick Start (~200ms)

`OpenSolutionAsync()` only. Return a `LoadedSolution` with the `Solution` object populated but compilations empty. SymbolResolver starts with no indexed data.

### Phase 2 — Background Warming

Kick off parallel compilation of all projects respecting dependency order (topological sort). As each dependency level completes, update the shared compilations dictionary. After all projects are compiled, rebuild SymbolResolver atomically.

### Phase 3 — On-Demand Fallback

If a tool needs data before warming finishes:
- Tools that need a specific `Compilation` (e.g., get_diagnostics) wait for that project's compilation task.
- Tools that need `SymbolResolver` (e.g., find_implementations) wait for full warm-up to complete.

## Dependency-Aware Parallel Compilation

```
Level 0 (leaves):    [TestLib1]  [TestLib2]     ← compile in parallel
Level 1:             [TestLib3 depends on 1,2]  ← compile after level 0
Level 2 (root):      [App depends on 3]         ← compile after level 1
```

Build a topological sort of projects by their `ProjectReference` edges. Projects at the same level compile concurrently via `Task.WhenAll`.

## Key Changes

### SolutionLoader

Split `LoadAsync` into two methods:
- `OpenAsync(solutionPath)` — calls `OpenSolutionAsync()`, returns `Solution` immediately
- `CompileProjectsAsync(solution)` — parallel compilation with dependency ordering, returns compilations dict

### SolutionManager

- `CreateAsync` returns immediately after `OpenAsync`, stores a `Task _warmupTask` for background compilation
- `GetResolver()` / `GetLoadedSolution()` check if warmup is complete; if not, await it (or return partial data)
- Add `WaitForWarmupAsync()` for tools that need full data
- Add per-project `Task<Compilation>` tracking via `ConcurrentDictionary<ProjectId, Task<Compilation>>`

### LoadedSolution

- `Compilations` becomes `ConcurrentDictionary<ProjectId, Compilation>` to support concurrent updates during warming
- Add `IsWarmedUp` flag

### SymbolResolver

- No changes to SymbolResolver itself — it remains immutable once constructed
- It gets rebuilt atomically after all compilations complete

### Tool Access Pattern

Tools call `GetResolver()` which either:
1. Returns the current resolver if warmup is done
2. Awaits warmup completion, then returns resolver
3. For stale rebuilds: same behavior as today

## Progress Reporting

```
[roslyn-codelens] Solution opened (5 projects). Warming up...
[roslyn-codelens] Compiled 2/5 projects (TestLib1, TestLib2)
[roslyn-codelens] Compiled 4/5 projects (TestLib3, Tests)
[roslyn-codelens] All 5 projects compiled. Indexes ready. (450ms)
```

## What Stays the Same

- **FileChangeTracker** — unchanged, still watches files
- **RebuildStaleProjects** — unchanged, still incremental
- **All tool logic files** — unchanged, they just call `GetResolver()`/`GetLoadedSolution()`
- **ForceReloadAsync** — reuses the same parallel compilation path

## Expected Performance

| Metric | Before | After |
|--------|--------|-------|
| Perceived startup | ~785ms | ~200ms |
| Full warm-up | ~785ms | ~400-500ms (parallel on multi-core) |
| First tool call (if warm) | instant | instant |
| First tool call (if not warm) | N/A | blocks for required project(s) |

## Risks

- **Roslyn thread safety**: `GetCompilationAsync()` is safe to call concurrently on different projects within the same `Solution`. Roslyn's internal caching handles this.
- **SymbolResolver race**: Tools might get a stale resolver during warmup. Mitigated by awaiting warmup in `GetResolver()`.
- **Memory spike**: Parallel compilation uses more peak memory. Acceptable on modern machines.

## Out of Scope

- Per-tool result caching (can be added later independently)
- Compilation serialization to disk (Roslyn doesn't support this)
- Lazy per-project SymbolResolver (too complex, marginal benefit)
