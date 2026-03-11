# Roslyn CodeLens MCP Server

[![NuGet](https://img.shields.io/nuget/v/RoslynCodeLens.Mcp?style=flat-square&logo=nuget&color=blue)](https://www.nuget.org/packages/RoslynCodeLens.Mcp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/RoslynCodeLens.Mcp?style=flat-square&color=green)](https://www.nuget.org/packages/RoslynCodeLens.Mcp)
[![Build Status](https://img.shields.io/github/actions/workflow/status/MarcelRoozekrans/roslyn-codelens-mcp/ci.yml?branch=main&style=flat-square&logo=github)](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/actions)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/roslyn-codelens-mcp?style=flat-square)](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/blob/main/LICENSE)

A Roslyn-based MCP server that gives AI agents deep semantic understanding of .NET codebases — type hierarchies, call graphs, DI registrations, diagnostics, and more.

<!-- mcp-name: io.github.marcelroozekrans/roslyn-codelens -->

---

## Features

- **find_implementations** — Find all classes/structs implementing an interface or extending a class
- **find_callers** — Find every call site for a method, property, or constructor
- **get_type_hierarchy** — Walk base classes, interfaces, and derived types
- **get_di_registrations** — Scan for DI service registrations
- **get_project_dependencies** — Get the project reference graph
- **get_symbol_context** — One-shot context dump for any type
- **find_reflection_usage** — Detect dynamic/reflection-based usage
- **find_references** — Find all references to any symbol (types, methods, properties, fields, events)
- **go_to_definition** — Find the source file and line where a symbol is defined
- **get_diagnostics** — List compiler errors, warnings, and Roslyn analyzer diagnostics
- **get_code_fixes** — Get available code fixes with structured text edits for any diagnostic
- **search_symbols** — Fuzzy workspace symbol search by name
- **get_nuget_dependencies** — List NuGet package references per project
- **find_attribute_usages** — Find types and members decorated with a specific attribute
- **find_circular_dependencies** — Detect cycles in project or namespace dependency graphs
- **get_complexity_metrics** — Cyclomatic complexity analysis per method
- **find_naming_violations** — Check .NET naming convention compliance
- **find_large_classes** — Find oversized types by member or line count
- **find_unused_symbols** — Dead code detection via reference analysis
- **get_source_generators** — List source generators and their output per project
- **get_generated_code** — Inspect generated source code from source generators
- **rebuild_solution** — Force a full reload of the analyzed solution

## Quick Start

### VS Code / Visual Studio (via dnx)

Add to your MCP settings (`.vscode/mcp.json` or VS settings):

```json
{
  "servers": {
    "roslyn-codelens": {
      "type": "stdio",
      "command": "dnx",
      "args": ["RoslynCodeLens.Mcp", "--yes"]
    }
  }
}
```

### Claude Code Plugin

```bash
claude install gh:MarcelRoozekrans/roslyn-codelens-mcp
```

### .NET Global Tool

```bash
dotnet tool install -g RoslynCodeLens.Mcp
```

Then add to your MCP client config:

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "command": "roslyn-codelens-mcp",
      "args": [],
      "transport": "stdio"
    }
  }
}
```

## Usage

The server automatically discovers `.sln` files by walking up from the current directory. You can also pass a solution path directly:

```bash
roslyn-codelens-mcp /path/to/MySolution.sln
```

## Performance

All type lookups use pre-built reverse inheritance maps, member indexes, and attribute indexes for O(1) access. Benchmarked on an i9-12900HK with .NET 10.0.3:

| Tool | Latency | Memory |
|------|--------:|-------:|
| `find_circular_dependencies` | 288 ns | 1.3 KB |
| `get_project_dependencies` | 299 ns | 1.2 KB |
| `go_to_definition` | 442 ns | 568 B |
| `get_type_hierarchy` | 720 ns | 856 B |
| `find_implementations` | 804 ns | 704 B |
| `get_symbol_context` | 1.1 µs | 1.0 KB |
| `get_source_generators` | 2.6 µs | 8.3 KB |
| `find_attribute_usages` | 6.8 µs | 312 B |
| `get_generated_code` | 13 µs | 9.8 KB |
| `get_diagnostics` | 27 µs | 23 KB |
| `get_complexity_metrics` | 50 µs | 5.8 KB |
| `find_large_classes` | 60 µs | 1.2 KB |
| `get_di_registrations` | 60 µs | 13 KB |
| `get_nuget_dependencies` | 62 µs | 16 KB |
| `find_reflection_usage` | 82 µs | 15 KB |
| `find_callers` | 182 µs | 38 KB |
| `search_symbols` | 517 µs | 2.4 KB |
| `find_references` | 927 µs | 208 KB |
| `find_unused_symbols` | 1.1 ms | 212 KB |
| `find_naming_violations` | 5.0 ms | 670 KB |
| Solution loading (one-time) | ~928 ms | 8 MB |

## Hot Reload

The server watches `.cs`, `.csproj`, `.props`, and `.targets` files for changes. When a change is detected, affected projects are lazily re-compiled on the next tool query — only stale projects and their downstream dependents are re-compiled, not the full solution.

Location-returning tools include an `IsGenerated` flag to distinguish source-generator output from hand-written code.

## Requirements

- .NET 10 SDK
- A .NET solution with compilable projects

## Development

```bash
dotnet build
dotnet test
dotnet run --project benchmarks/RoslynCodeLens.Benchmarks -c Release
```

## License

MIT
