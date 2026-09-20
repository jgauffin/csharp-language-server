// Claude Code PreToolUse hook: denies Grep/Glob calls that are unambiguously C# symbol lookups
// so the agent uses the csharp MCP tools instead. Exit code 2 denies; stderr is shown to the agent.
// Unscoped searches, other file types and literal strings pass through, so cross-language
// text search in a mixed repo keeps working.
const fs = require('fs');
const { tool_name, tool_input } = JSON.parse(fs.readFileSync(0, 'utf8'));
const pattern = tool_input.pattern ?? '';
const scope = `${tool_input.glob ?? ''} ${tool_input.type ?? ''}`;

const scopedToCs = /\.cs\b/.test(scope) || /\bcs\b/.test(scope);
const csDeclaration = /\b(class|interface|record|struct|enum)\s+\w+|:\s*I[A-Z]\w*|\b(void|Task|async)\s+\w+\s*\(/.test(pattern);
const isLiteral = /["']/.test(pattern) || (/\s/.test(pattern) && !csDeclaration);

const deny = (msg) => { console.error(msg); process.exit(2); };

if (tool_name === 'Grep' && !isLiteral && (scopedToCs || csDeclaration))
  deny('C# symbol lookup: use mcp__csharp__find to locate a type or member by name, get_references for usages, ' +
       'get_definition to jump to a declaration, get_outline to see what a file contains. ' +
       'For a cross-language text search, scope Grep with type or glob to the other languages.');

if (tool_name === 'Glob' && /\.cs\b/.test(pattern))
  deny('C# symbol lookup: use mcp__csharp__find (optionally with kind) to locate types and members instead of globbing for files.');
