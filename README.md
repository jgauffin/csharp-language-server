# csharp-language-mcp

A C# code intelligence server for AI coding agents via MCP (Model Context Protocol), powered by Roslyn.

## Why Use This?

AI coding agents working with C# treat code as text — reading entire files, grepping for patterns, guessing at types. This works passably for dynamic languages, but C# codebases fight back: deep type hierarchies, cross-project references, generics, implicit usings, partial classes. An agent armed with `grep` is flying blind.

**csharp-language-mcp** gives agents the same intelligence that IDEs have — powered by the Roslyn compiler platform:

### C# Navigation

| Raw File Access | csharp-language-mcp |
|-----------------|---------------------|
| Read entire file to find a class | Jump directly to any definition |
| Grep for method names, miss overloads | Get all references with read/write classification |
| Guess at types from context | Get exact types with full generic resolution |
| Miss interface implementations in other projects | Find all concrete implementations across the solution |
| Hope renames don't break things | Preview and execute renames across all projects |
| Read files one by one to find errors | Get project-wide or solution-wide diagnostics instantly |

### NuGet

Agents working with .NET need to find packages, understand their APIs, and read documentation. This server gives agents direct access to real assembly metadata and XML docs from the local NuGet cache — structured as JSON, sized for LLM context windows.

| Task | Without this server | With csharp-language-mcp |
|------|-------------------|----------------|
| **Find a package** | Scrape nuget.org search HTML or guess from training data | `nuget_search` — cache-first, structured JSON results |
| **List available versions** | Parse nuget.org version page or call raw API + paginate | `nuget_package_info` — versions, dependencies, and file listing in one call |
| **Read public API surface** | Download `.nupkg`, extract DLL, attempt decompilation — most agents can't do this at all | `nuget_assembly_types` — real type/member signatures via `System.Reflection.Metadata` |
| **Read API documentation** | Scrape docs site (if one exists), hope the formatting parses cleanly | `nuget_assembly_docs` — XML docs from the package, filterable by type |
| **Token cost** | 20-50k+ tokens of HTML/noise per page, or hallucinated answers at 0 tokens | ~1-5k tokens of focused, structured JSON per call |

### What makes Roslyn different from text search?

Roslyn **compiles** your code. It resolves types, binds symbols, and understands your entire solution graph. When an agent asks "who implements `IRepository<T>`?", Roslyn gives the real answer — not a regex approximation that misses implementations in other projects or includes false matches from comments and strings.

This matters most for C# because:

- **Multi-project solutions** — references, shared interfaces, and NuGet packages create a dependency graph that text search can't follow
- **Rich type system** — generics, inheritance, implicit conversions, and extension methods are invisible to grep
- **Cross-cutting patterns** — dependency injection, interface segregation, and CQRS mean the definition and usage of a symbol are rarely in the same file

## Requirements

- .NET 9 SDK (includes MSBuild)

## Quick Start

### MCP Client Configuration

**Claude Code:**

```bash
claude mcp add csharp -- dotnet run --project <server>/src/CsharpMcp -- [your-csharp-repo]
```

| Placeholder | Meaning |
|-------------|---------|
| `<server>` | Path where you cloned this repo |
| `[your-csharp-repo]` | Path to the C# project you want to analyze (optional, defaults to cwd) |
| First `--` | Separates `claude mcp add` from the server command |
| Second `--` | Separates `dotnet run` from the server's own arguments |

Examples:

```bash
# Analyze a specific repo
claude mcp add csharp -- dotnet run --project ~/tools/csharp-language-mcp/src/CsharpMcp -- ~/projects/my-api

# Analyze whichever directory Claude Code is running in (omit the repo path)
claude mcp add csharp -- dotnet run --project ~/tools/csharp-language-mcp/src/CsharpMcp

# Windows — forward slashes work and avoid quoting issues
claude mcp add csharp -- dotnet run --project C:/tools/csharp-language-mcp/src/CsharpMcp -- C:/projects/my-api

# Multiple instances with --name and --description
claude mcp add csharp-api -- dotnet run --project ~/tools/csharp-language-mcp/src/CsharpMcp -- --name csharp-api --description "API layer" ~/projects/my-api
claude mcp add csharp-core -- dotnet run --project ~/tools/csharp-language-mcp/src/CsharpMcp -- --name csharp-core --description "Core domain" ~/projects/my-core
```

**Claude Desktop, Cline, etc.:**

```json
{
  "mcpServers": {
    "csharp": {
      "command": "dotnet",
      "args": ["run", "--project", "<server>/src/CsharpMcp", "--", "[your-csharp-repo]"]
    }
  }
}
```

Or with a published binary:

```json
{
  "mcpServers": {
    "csharp": {
      "command": "<server>/CsharpMcp",
      "args": ["[your-csharp-repo]"]
    }
  }
}
```

### Options

| Option | Description | Default |
|--------|-------------|---------|
| `[directory]` | Path to C# project root | Current working directory |
| `--name <name>` | Custom server name (for multi-instance setups) | `csharp-language-mcp` |
| `--description <text>` | Extra context appended to the built-in server description | _(none)_ |

**Multiple instances** — use `--name` and `--description` to distinguish servers when running one per repo:

```json
{
  "mcpServers": {
    "csharp-api": {
      "command": "<server>/CsharpMcp",
      "args": ["--name", "csharp-api", "--description", "API layer", "<your-api-repo>"]
    },
    "csharp-core": {
      "command": "<server>/CsharpMcp",
      "args": ["--name", "csharp-core", "--description", "Core domain", "<your-core-repo>"]
    }
  }
}
```

The server discovers all `.csproj` files under the root path and loads them into a single Roslyn workspace. All positions are **1-indexed** (line 1, column 1 = first character).

## Tools

| Category | Tool | Description |
|----------|------|-------------|
| **Navigation** | `get_definition` | Jump from usage to declaration |
| | `get_references` | Find all usages with read/write classification |
| | `get_implementations` | Find concrete implementations of interfaces |
| | `get_call_hierarchy` | Trace function callers / callees |
| | `get_type_hierarchy` | Navigate base types, interfaces, and derived types |
| **Type Intelligence** | `get_hover` | Type info and XML docs at a position |
| | `get_signature` | Function parameter help at a call site |
| **Code Structure** | `get_symbols` | Flat list of symbols in a file |
| | `get_outline` | Hierarchical file structure |
| | `get_imports` | List all using directives |
| **Semantic Search** | `find` | Search symbols by name pattern, kind, project |
| | `get_workspace_symbols` | Fast fuzzy symbol search |
| **Completions** | `get_completions` | Code completions at a position (members, methods, types, keywords) |
| **Project** | `get_project_files` | List all source files in the workspace |
| **Diagnostics** | `get_diagnostics` | Errors/warnings for a file |
| | `get_all_diagnostics` | Project-wide or solution-wide diagnostics |
| **Refactoring** | `rename_preview` | Preview rename impact without writing |
| | `rename_symbol` | Execute rename across all projects |
| | `format_document` | Format with Roslyn formatter |
| | `get_code_actions` | Quick fixes for diagnostics (add using, implement interface, etc.) |
| **Efficiency** | `analyze_position` | Combined hover + diagnostics + symbols in one call |
| | `batch_analyze` | Analyze multiple positions at once |
| **Quality** | `quality_snapshot` | Capture baseline metrics + diagnostics for later comparison |
| | `quality_report` | Compare current quality against snapshot, or report git-changed files |
| **Code Metrics** | `calculate_metrics` | Worst types by maintainability index |
| | `get_worst_namespaces` | Namespace-level maintainability ranking |
| | `get_namespace_types` | Drill into a specific namespace |
| | `detect_duplication` | Find exact, renamed, and semantic code clones |
| | `generate_workspace_summary` | High-level workspace health overview |
| **NuGet** | `nuget_search` | Cache-first search, falls back to remote |
| | `nuget_list_cached` | List all locally cached packages and versions |
| | `nuget_package_info` | Metadata, dependencies, file listing for a cached package |
| | `nuget_assembly_types` | Public type/member definitions from assemblies (no docs) |
| | `nuget_assembly_docs` | XML documentation, filterable by type |

## Quality Tracking

The `quality_snapshot` and `quality_report` tools let you measure code quality impact during a coding session.

**Workflow:**
1. Call `quality_snapshot` at the start of a session — captures maintainability index, cyclomatic complexity, LOC, coupling, and diagnostic counts for all types
2. Make your changes
3. Call `quality_report` — shows per-type deltas, categorized as improved/degraded/new/removed

If no snapshot exists, `quality_report` falls back to identifying git-changed `.cs` files and reporting diagnostics and workspace-wide metrics.

## Configuring CLAUDE.md

To get the most out of this server, add these instructions to your project's `CLAUDE.md` file:

```markdown
## C# Code Intelligence

Always prefer csharp MCP tools over grep/bash/find for C# code:
- Use `get_definition` instead of grepping for class/method definitions
- Use `get_references` instead of grepping for usages
- Use `find` or `get_workspace_symbols` instead of find/grep for locating symbols
- Use `get_diagnostics` / `get_all_diagnostics` instead of running `dotnet build` to check errors
- Use `get_outline` instead of reading entire files to understand structure
- Use `get_hover` to check types instead of guessing from context

## Code Quality Tracking
- At the START of each coding session, call `quality_snapshot` to capture a baseline
- At the END of each session (before committing), call `quality_report` to review quality impact
- If quality_report shows degraded types, address them before finishing
```

> **Why is this needed?** AI agents default to familiar tools like `grep`, `find`, and `cat`. Without explicit instructions, they will ignore the MCP tools even when they're available — wasting tokens reading entire files and producing less accurate results. The `CLAUDE.md` instructions override this default behavior.

## Build

```bash
dotnet build src/CsharpMcp.sln
```

## Tests

```bash
dotnet test src/CsharpMcp.sln
```

## License

MIT
