# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows [SemVer](https://semver.org/).

## 1.1.0 - 2026-08-30

### Added

- `--root <dir>` sets the workspace root. Without it the root binds to the launch directory once at startup.

### Fixed

- Project discovery no longer follows symlinks and junctions. A package directory linked back at one of its own ancestors, the layout bun and pnpm produce, made the walk recurse until the process was killed. An idle server grew by roughly 17 GB per hour and never became ready, so every tool answered "still loading" for its whole lifetime.
- Directory walks prune excluded directories while descending instead of filtering the results, so `node_modules`, `bin` and `obj` are no longer traversed at all.
- A discovery walk that outlives its timeout now fails with an error naming the likely cause. Previously a non-terminating walk was indistinguishable from a slow one.
- Console logging goes to stderr. stdout is the MCP stdio transport, so log lines were parsed as protocol and discarded by the client, taking the workspace loading diagnostics with them.
- Pending file changes are keyed by path and capped, falling back to a full resync on overflow. The previous queue grew with the watcher event count and was drained only by a tool call, so an idle server accumulated entries indefinitely.

### Changed

- The version reported to MCP clients comes from the assembly instead of a literal.
