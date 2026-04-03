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
| **Find a package** | Scrape nuget.org search HTML or guess from training data | `nuget_search` — cache-first, structured results |
| **List cached packages** | Browse filesystem manually | `nuget_packages` — list all cached packages or get metadata for a specific one |
| **Read public API surface** | Download `.nupkg`, extract DLL, attempt decompilation — most agents can't do this at all | `nuget_explore` — real type/member signatures via `System.Reflection.Metadata` |
| **Read API documentation** | Scrape docs site (if one exists), hope the formatting parses cleanly | `nuget_explore` with `includeDocs` — XML docs from the package, filterable by type |
| **Token cost** | 20-50k+ tokens of HTML/noise per page, or hallucinated answers at 0 tokens | ~1-5k tokens of focused, structured output per call |

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
| `--no-quality` | Disable quality/metrics tools (saves 2 tools) | _(enabled)_ |
| `--no-nuget` | Disable NuGet tools (saves 3 tools) | _(enabled)_ |

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

## Tools (21 total)

| Category | Tool | Description |
|----------|------|-------------|
| **Navigation** | `get_definition` | Jump from usage to declaration |
| | `get_references` | Find all usages with read/write classification |
| | `get_implementations` | Find concrete implementations of interfaces |
| | `get_call_hierarchy` | Trace callers/callees; use `maxDepth` for deep usage tracing |
| | `get_type_hierarchy` | Navigate base types, interfaces, and derived types |
| **Type Intelligence** | `get_hover` | Type info, XML docs, and parameter signatures at a position |
| **Code Structure** | `get_outline` | Hierarchical file structure (use `flat=true` for flat symbol list) |
| | `get_imports` | List all using directives |
| **Search** | `find` | Search symbols by name pattern, kind, project |
| **Completions** | `get_completions` | Code completions at a position (members, methods, types, keywords) |
| **Diagnostics** | `get_diagnostics` | Errors/warnings for a file or entire workspace |
| **Refactoring** | `rename` | Preview or execute rename across all projects (`preview=true` by default) |
| | `format_document` | Format with Roslyn formatter |
| | `get_code_actions` | Quick fixes for diagnostics (add using, implement interface, etc.) |
| **Efficiency** | `analyze_position` | Combined hover + diagnostics + symbols in one call |
| | `batch_analyze` | Analyze multiple positions at once |
| **Quality** | `quality_hotspots` | Composite quality scoring — finds worst code by weighting MI, duplication, opacity, indirection |
| | `generate_iso5055_report` | ISO 5055 quality report (security, reliability, performance, maintainability) |
| **NuGet** | `nuget_search` | Cache-first search, falls back to remote |
| | `nuget_packages` | List cached packages, or get metadata/deps for a specific package |
| | `nuget_explore` | Explore assemblies, types, and XML docs in a cached package |

## Quality Hotspots

The `quality_hotspots` tool identifies code that needs refactoring by weighting four quality dimensions:

- **Maintainability** — MI, cyclomatic complexity, LOC, coupling
- **Duplication** — exact, renamed, and semantic code clones
- **Opacity** — hard-to-understand methods (embedding similarity, nesting, magic literals)
- **Indirection** — hidden coupling through deep call chains

Default weights are equal (0.25 each). Override weights to focus on specific concerns.

**Before/after tracking:**
1. Call `quality_hotspots(snapshotLabel: "before")` at the start of a session
2. Make your changes
3. Call `quality_hotspots(compareToSnapshot: "before")` — shows per-type deltas

## ISO 5055 Support (Partial)

The `generate_iso5055_report` tool provides partial coverage of the [ISO/IEC 5055](https://www.iso.org/standard/80623.html) automated source code quality standard. It analyzes your solution against the four ISO 5055 quality characteristics:

- **Security** — detects CWE-mapped vulnerabilities (e.g. SQL injection, path traversal)
- **Reliability** — detects defect-prone patterns (e.g. null dereference, empty catch blocks)
- **Performance Efficiency** — detects resource waste patterns
- **Maintainability** — detects overly complex or opaque code (e.g. deep nesting, magic numbers)

The report includes violation counts, violations per KLOC, pass/fail per category, covered CWE IDs, and per-violation file paths with fix suggestions.

**Categories and current coverage:**

| Category | Focus | Coverage |
|----------|-------|----------|
| **Security** | Exploitable vulnerabilities | Low — pattern-match rules only (unsafe code, CWE-242). No taint analysis. |
| **Reliability** | Crash/corruption risks | Moderate — dispose pattern, stack trace destruction, event leaks, weak identity locks, virtual calls in constructors |
| **Performance Efficiency** | Resource waste | Low — sync-over-async detection (CWE-1049) |
| **Maintainability** | Structural decay | Good — cyclomatic complexity, deep nesting, large classes/methods, too many parameters, lack of cohesion, class instability, goto statements |

**Limitations:** This is not a SAST replacement. Taint-analysis-dependent CWEs (SQL injection, XSS, command injection) require dedicated tools like CodeQL or Semgrep. The report includes `CoveredCweIds` so consumers know exactly which CWEs are in scope.

## Configuring CLAUDE.md

To get the most out of this server, add these instructions to your project's `CLAUDE.md` file:

```markdown
## C# Code Intelligence

Always prefer csharp MCP tools over grep/bash/find for C# code:
- Use `get_definition` instead of grepping for class/method definitions
- Use `get_references` instead of grepping for usages
- Use `find` instead of find/grep for locating symbols
- Use `get_diagnostics` instead of running `dotnet build` to check errors
- Use `get_outline` instead of reading entire files to understand structure
- Use `get_hover` to check types instead of guessing from context

## Code Quality Tracking
- At the START of each coding session, call `quality_hotspots(snapshotLabel: "before")` to capture a baseline
- At the END of each session (before committing), call `quality_hotspots(compareToSnapshot: "before")` to review quality impact
- If quality_hotspots shows degraded types, address them before finishing
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
