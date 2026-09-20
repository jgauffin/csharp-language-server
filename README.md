# csharp-language-mcp

A C# code intelligence server for AI coding agents via MCP (Model Context Protocol), powered by Roslyn.

## What it does

Text tools (grep, glob, reading files) see C# as characters. Roslyn compiles the solution, so this server answers questions about symbols rather than strings, across every project in the solution:

| Text tools | csharp-language-mcp |
|------------|---------------------|
| Read a whole file to find a member | Outline of a file with line numbers, then read only what is needed |
| Grep a name, get comments, strings, overloads and unrelated symbols too | References resolved by the compiler, classified read/write |
| Infer a type from surrounding code | Resolved type and signature, generics included |
| Grep for `: IFoo`, miss implementations in other projects | All implementations across the solution |
| Rename with search-and-replace | Rename with preview, all projects |
| Run `dotnet build` and parse the output | Diagnostics per file or per solution, no build |

Beyond navigation the server provides quality metrics (hotspots, ISO 5055 report) and NuGet exploration: search, cached-package metadata and the public API surface plus XML docs of any cached package, read from assembly metadata rather than a docs site.

## Requirements

- .NET 10 SDK (includes MSBuild)

## Quick Start

Add the server to `.mcp.json` in your C# repository:

```json
{
  "mcpServers": {
    "csharp": {
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "<server>/src/CsharpMcp", "--", "[your-csharp-repo]"]
    }
  }
}
```

Or with a published binary:

```json
{
  "mcpServers": {
    "csharp": {
      "command": "<server>/CsharpMcp.exe",
      "args": ["[your-csharp-repo]"]
    }
  }
}
```

Replace `<server>` with the path to your clone of this repo. `--no-build` is required to run several instances at once (one per editor window).

### Options

| Option | Description | Default |
|--------|-------------|---------|
| `[directory]` | Path to C# project root | Current working directory |
| `--name <name>` | Custom server name (for multi-instance setups) | `csharp-language-mcp` |
| `--description <text>` | Extra context appended to the built-in server instructions | _(none)_ |
| `--no-quality` | Disable quality/metrics tools | _(enabled)_ |
| `--no-nuget` | Disable NuGet tools | _(enabled)_ |

**Multiple instances:** use `--name` and `--description` to distinguish servers when running one per repo:

```json
{
  "mcpServers": {
    "csharp-api": {
      "command": "<server>/CsharpMcp.exe",
      "args": ["--name", "csharp-api", "--description", "API layer", "<your-api-repo>"]
    },
    "csharp-core": {
      "command": "<server>/CsharpMcp.exe",
      "args": ["--name", "csharp-core", "--description", "Core domain", "<your-core-repo>"]
    }
  }
}
```

The server discovers all `.csproj` files under the root path and loads them into a single Roslyn workspace. All positions are **1-indexed** (line 1, column 1 = first character).

## Getting agents to use the server

Agents fall back to grep, glob and whole-file reads even when the server is available: those tools are always loaded, need no extra step, and match the model's habits. Instructions in `CLAUDE.md` are advisory and get skipped for the same reason. Two measures help, in order of effect.

### Block text search on C# files (Claude Code hook)

A `PreToolUse` hook is enforced by the harness, not by the model. When it denies a `Grep` or `Glob` call, the reason is shown to the agent, which then reaches for the MCP tool. The hook ships with the server as [`hooks/prefer-csharp-mcp.js`](hooks/prefer-csharp-mcp.js) (requires Node). In `~/.claude/settings.json`:

```json
{
  "permissions": {
    "allow": ["mcp__csharp__*"]
  },
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Grep|Glob",
        "hooks": [{ "type": "command", "command": "node <server>/hooks/prefer-csharp-mcp.js" }]
      }
    ]
  }
}
```

The `allow` entry removes permission prompts on the MCP tools; every prompt on an MCP call and none on grep is a nudge the wrong way.

The hook denies only calls that are unambiguously C# symbol lookups: Grep scoped to `.cs` files, Grep for C# declaration syntax (`class Foo`, `: IFoo`, `void Foo(`), or Glob for `.cs` files. Unscoped searches, other file types and literal strings pass through, so cross-language searches in a mixed repo still work.

### Server instructions and tool descriptions (built in)

The server's MCP instructions land in the agent's system prompt and map each habit to its replacement (locate by name: `find`; file contents: `get_outline`; usages: `get_references`; errors: `get_diagnostics`, not `dotnet build`). Tool descriptions say the same where a tool competes with grep. Nothing to configure; `--description` appends repo-specific context.

### CLAUDE.md

Still worth having as a reminder, and the place for workflow rules the server cannot express, such as quality tracking:

```markdown
## C# Code Intelligence
Prefer the csharp MCP tools over grep/glob/reading whole files for C# code.
Use get_diagnostics instead of dotnet build to check for errors.

## Code Quality Tracking
- At the start of a session, call quality_hotspots(snapshotLabel: "before")
- Before committing, call quality_hotspots(compareToSnapshot: "before") and address degraded types
```

## Quality Hotspots

The `quality_hotspots` tool identifies code that needs refactoring by weighting three quality dimensions:

- **Maintainability**: MI, cyclomatic complexity, LOC, statements, coupling
- **Duplication**: exact, renamed, and semantic code clones
- **Indirection**: hidden coupling through deep call chains

Default weights are roughly equal (≈0.33 each). Override weights to focus on specific concerns.

**Reading the numbers:** every metric value carries a health rating in brackets, from `[1 - Healthy]`
to `[5 - Fix ASAP]`, where lower is better, the opposite direction to the maintainability index next
to it. Call `metric_scales` for the full legend; reports leave it out to keep responses small.

Size is reported as two separate figures. **LOC** counts the source lines an element occupies,
excluding blanks, comments and documentation. **Stmts** counts
executable statements, which formatting cannot change, and is the size term the maintainability
index is actually built on.

**Before/after tracking:**
1. Call `quality_hotspots(snapshotLabel: "before")` at the start of a session
2. Make your changes
3. Call `quality_hotspots(compareToSnapshot: "before")` to see per-type deltas

## ISO 5055 Support (Partial)

The `generate_iso5055_report` tool provides partial coverage of the [ISO/IEC 5055](https://www.iso.org/standard/80623.html) automated source code quality standard. It analyzes your solution against the four ISO 5055 quality characteristics:

- **Security**: CWE-mapped vulnerabilities (e.g. SQL injection, path traversal)
- **Reliability**: defect-prone patterns (e.g. null dereference, empty catch blocks)
- **Performance Efficiency**: resource waste patterns
- **Maintainability**: overly complex or opaque code (e.g. deep nesting, magic numbers)

The report includes violation counts, violations per KLOC, pass/fail per category, covered CWE IDs, and per-violation file paths with fix suggestions.

> **Note:** the KLOC denominator is physical source lines, so densities are not comparable with
> reports produced before the size metrics were separated.

**Categories and current coverage:**

| Category | Focus | Coverage |
|----------|-------|----------|
| **Security** | Exploitable vulnerabilities | Low: pattern-match rules only (unsafe code, CWE-242). No taint analysis. |
| **Reliability** | Crash/corruption risks | Moderate: dispose pattern, stack trace destruction, event leaks, weak identity locks, virtual calls in constructors |
| **Performance Efficiency** | Resource waste | Low: sync-over-async detection (CWE-1049) |
| **Maintainability** | Structural decay | Good: cyclomatic complexity, deep nesting, large classes/methods, too many parameters, lack of cohesion, class instability, goto statements |

**Limitations:** This is not a SAST replacement. Taint-analysis-dependent CWEs (SQL injection, XSS, command injection) require dedicated tools like CodeQL or Semgrep. The report includes `CoveredCweIds` so consumers know exactly which CWEs are in scope.

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
