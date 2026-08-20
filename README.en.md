# CodeAgent

[![CI](https://github.com/2228293026/CodeAgent/actions/workflows/ci.yml/badge.svg)](https://github.com/2228293026/CodeAgent/actions/workflows/ci.yml)
[![build (ubuntu)](https://github.com/2228293026/CodeAgent/actions/workflows/build.yml/badge.svg)](https://github.com/2228293026/CodeAgent/actions/workflows/build.yml)

**[中文文档](README.md)** | English

An LLM-powered coding assistant CLI written in C# (.NET 10). Like Claude Code / Aider, it understands your task in the terminal and autonomously **reads files, searches code, edits code, and runs commands** until the job is done.

## Features

- ⚡ Streaming output: replies print token by token, like a live conversation (`"streamOutput": false` to disable)
- 🚀 Parallel tool calls: multiple tools run concurrently in one turn; same-path writes automatically serialize (also serialized when `confirmCommands` is on)
- 🖥️ Tool-call visualization: actions and durations shown live; `run_command` previews output (`"showToolCalls": false` to disable)
- 🔁 Auto retry: 429 / 5xx / connection failures retry with exponential backoff (up to 2 times); no retry once streamed text has been emitted
- 🧠 Context auto-summarization: when history exceeds the limit, the oldest turns are compressed by the LLM first; dropping old messages is the fallback
- 🎭 Work modes: `/mode` or Tab to switch among 8 built-ins (code/plan/explain/review/debug/refactor/test/doc) plus custom modes in config; read-only modes hide and block write tools
- 🎮 ADOFAI mod detection: mod projects get dev context and moddev / harmony / assetbundle modes injected automatically
- 🎨 Markdown rendering: code blocks / inline code / bold / headings colored (`"renderMarkdown": false` to disable)
- ⌨️ Terminal TUI: slash-command menu (filter / arrow keys / digit-run / fill), command history (arrows, persisted, Ctrl+R reverse search), TAB completion, multi-line paste folding, Shift+Enter manual newline, line-local Home/End, Ctrl+L clear, mode prompt
- 🔧 10 built-in tools: `read_file` / `write_file` / `edit_file` / `list_directory` / `glob` / `grep` / `run_command` / `bash` / `powershell` / `stop` (command tools auto-pick Git Bash / PowerShell); `edit_file` / `write_file` show a colored diff preview before executing
- ↩️ Sessions auto-saved: `--continue` resumes the latest session, `/resume` restores by number, Esc rolls back turn by turn
- 📊 Usage visibility: status bar shows per-turn tokens, current context size ctx (with percentage; window auto-detected for common models) and thinking effort (`auto` probes supported levels and picks the highest); `/compact [focus]` compresses history manually (with progress; ESC cancels; optional focus folded into the summarization prompt)
- 🔌 Two providers: OpenAI-compatible (chat completions + function calling) and Anthropic (messages + tool use)
- 🌍 Encoding-aware: file reads auto-detect BOM / UTF-8 / GB18030 (GBK); writes keep the original encoding — legacy Chinese projects do not garble, and undo restores the original encoding
- ⚠️ Truncation alert: when output is cut off by `maxTokens` (finish_reason=length / stop_reason=max_tokens) you get an explicit warning
- 📝 Session logs: every turn lands in `.codeagent/sessions/*.jsonl`; logs beyond `maxSessionLogs` (default 30) are pruned automatically
- 💬 Two usage styles: one-shot `codeagent "task"` (piped stdin is appended to the task) or an interactive REPL
- ⚙️ Config file `codeagent.json` (project-level or global `~/.codeagent/config.json`); API keys read from environment variables
- 🛡️ Workspace sandbox: file tools stay inside the workspace by default; command execution can require per-command confirmation
- 🔐 File access levels: strict (default sandbox) / whitelist (sandbox + read-only allowlist) / full (unrestricted), switchable live via `/access` or Shift+Tab and persisted

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# 1. Build
cd CodeAgent
dotnet build -c Release

# 2. Interactive provider setup (recommended: pick a provider, fill the model,
#    generates codeagent.json, tests the connection before saving)
dotnet run --project src/CodeAgent -- --setup
#    or generate a sample config and edit it:  dotnet run --project src/CodeAgent -- --init

# 3. Set the API key the way the wizard suggested, e.g.:
export OPENAI_API_KEY=sk-xxx        # Windows PowerShell: $env:OPENAI_API_KEY = "sk-xxx"

# 4. Run
dotnet run --project src/CodeAgent -- "Review this project structure and write a README"
```

You can also publish a single-file executable:

```bash
dotnet publish src/CodeAgent -c Release -r win-x64 --self-contained false -o dist
./dist/codeagent.exe
```

## Configuration

Lookup order: path given to `-c`, then `codeagent.json` in the current directory, then `~/.codeagent/config.json`, then built-in defaults.

```jsonc
// codeagent.json sample (--init generates the full version)
{
  "provider": "deepseek",
  "providers": {
    "openai":    { "type": "openai",    "baseUrl": "https://api.openai.com/v1", "model": "gpt-4o", "apiKeyEnv": "OPENAI_API_KEY", "maxTokens": 8192, "temperature": 0.2 },
    "deepseek":  { "type": "openai",    "baseUrl": "https://api.deepseek.com/v1", "model": "deepseek-chat", "apiKeyEnv": "DEEPSEEK_API_KEY" },
    "qwen":      { "type": "openai",    "baseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1", "model": "qwen3-coder-plus", "apiKeyEnv": "DASHSCOPE_API_KEY" },
    "ollama":    { "type": "openai",    "baseUrl": "http://localhost:11434/v1", "model": "qwen2.5-coder:7b", "apiKey": "ollama" },
    "anthropic": { "type": "anthropic", "model": "claude-sonnet-4-5", "apiKeyEnv": "ANTHROPIC_API_KEY" }
  },
  "maxToolIterations": 0,        // max tool rounds per task, 0 = unlimited
  "maxHistoryChars": 160000,     // history char cap, auto-trimmed beyond
  "contextWindow": 0,            // context window in tokens for the ctx %; 0 = auto-detect
  "pricePerMillionInput": 0,     // input price ($/M tokens); > 0 enables cost estimates (per-provider keys override)
  "pricePerMillionOutput": 0,
  "allowCommands": true,         // allow run_command
  "confirmCommands": false,      // confirm each command before running
  "commandTimeoutSeconds": 60,   // default command timeout (per-call override, max 300)
  "shell": "",                   // cmd | powershell | bash (empty = cmd on Windows)
  "saveSessions": true,          // write session logs
  "maxSessionLogs": 30,          // keep at most this many session logs (0 = never prune)
  "sessionDir": ".codeagent/sessions",
  "exportDir": ".codeagent/exports",
  "streamOutput": true,          // stream replies token by token
  "showToolCalls": true,         // show tool calls live
  "renderMarkdown": true,        // render Markdown in replies
  "tuiAnsi": true,               // ANSI in-place menu rendering; false = scrolling menu for old terminals
  "thinkingEffort": "off",       // off/low/medium/high/auto (auto probes supported levels, uses the highest)
  "defaultMode": "code",         // startup mode (/mode switches)
  "fileAccess": "strict",        // strict (sandbox) | whitelist (sandbox + read-only allowlist) | full
  "readOnlyDirs": [],            // read-only dirs outside the workspace for whitelist mode
  "systemPrompt": "",            // custom system prompt (empty = built-in default)
  "modes": []                    // custom work modes (see below)
}
```

### Custom modes

Define your own modes in the `modes` list (switch with `/mode`):

```jsonc
"modes": [
  {
    "name": "fix",
    "description": "Fix mode: locate and fix bugs",
    "systemPrompt": "You are CodeAgent in FIX mode. Reproduce the bug, find the root cause, apply a minimal fix, then verify with the build/tests.",
    "tools": []          // empty = all tools; a list = only these tools
  }
]
```

Three ways to switch provider:

```bash
codeagent -p deepseek "task..."        # CLI flag
CODEAGENT_PROVIDER=anthropic codeagent  # env var (-p wins; legacy CODEGENT_* spelling still accepted)
CODEAGENT_MODEL=gpt-4o codeagent        # model only (-m wins; legacy CODEGENT_* spelling still accepted)
# In the REPL: /model gpt-4o-mini, /provider anthropic
```

> Note: `type: "openai"` means the OpenAI-compatible protocol — DeepSeek, Qwen, Ollama and friends all use it; just change `baseUrl` and `model`. Models without function calling / tool use cannot act as an agent.

### File access

File tools are sandboxed to the workspace by default (`fileAccess: "strict"`). Three levels:

| Level | Meaning |
|-------|---------|
| `strict` (default) | reads and writes confined to the workspace |
| `whitelist` | workspace plus `readOnlyDirs` read-only allowlist; write tools and commands stay in the workspace |
| `full` | sandbox off entirely — every file readable/writable (trusted environments only) |

Switch live (persisted back to the config): `/access next` cycles strict → whitelist → full; `/access whitelist` sets directly; Shift+Tab in the REPL cycles too.

## Usage

### One-shot tasks

```bash
codeagent "Implement all the TODOs in Program.cs"
codeagent --cwd ../some-project "Explain how this project builds"
codeagent --mode review "Review src/Agent.cs"   # start in a given mode (session-level defaultMode override, not persisted)
codeagent --models                    # list the current provider models
codeagent --continue                  # resume this project latest session
codeagent --continue "continue the task"
type bug.log | codeagent "analyze"    # piped stdin is appended to the task
```

Every message is logged to `.codeagent/sessions/*.jsonl` (`saveSessions`); `--continue` resumes the latest; `/resume` lists the 10 most recent (relative age + first-user-input preview) and restores by number; `/export <n>` exports one of them to Markdown (a named snapshot wins over the number if they collide).

### Interactive REPL

```bash
codeagent
codeagent> What classes are under src?
codeagent> Add a unit test for ToolRegistry
codeagent> /model claude-sonnet-4-5
codeagent> /clear
codeagent> /exit
```

REPL commands: `/help` `/clear` `/compact` `/cls` `/model` `/provider` `/config` `/session` `/setup` `/undo` `/diff` `/save` `/load` `/resume` `/export` `/copy` `/prompt` `/stats` `/retry` `/tools` `/providers` `/models` `/history [N]` `/thinking` `/shell` `/mode` `/access` `/diag` `/exit` `/quit`.

Work modes: `/mode` lists, `/mode plan` switches. 8 built-ins: `code` (default, full power), `plan` / `explain` / `review` (read-only — write tools hidden and blocked), `debug` / `refactor` / `test` / `doc`. Custom modes live in the `modes` config list (system prompt + tool scope).

`/undo` rolls back the last `write_file` / `edit_file` change: newly created files are deleted, overwritten files restored (last 50 changes remembered). `/diff` shows the diff of the most recent change. `/copy` puts the last reply on the clipboard; `/prompt` shows the live system prompt.

`Ctrl+C` or `Esc` cancels the current turn gracefully (stops model thinking / tool execution; history rolls back to a valid state so the conversation can continue). Idle `Ctrl+C` exits.

Typing `/` opens the command menu (ANSI in-place rendering: arrows move, fill, Enter runs, Esc closes, typing filters; digits 1-9 run directly). Shortcuts: `Esc` (empty input) rolls back the last turn, `Tab` cycles work modes, `Shift+Tab` cycles file-access levels, `Alt+M` mode menu, `Alt+U` undo, `Alt+D` diff, `Ctrl+arrows` word motion, `Ctrl+Backspace/Delete` word delete, `Ctrl+R` history search. The status bar above each prompt shows mode, model, directory, and token usage.

## Tools

| Tool | Description |
|------|-------------|
| `read_file` | Read with line numbers; `offset`/`limit` paging, `no_line_numbers`; binary files and directories produce clear hints |
| `write_file` | Create/overwrite a file, creating parent dirs; missing `content` errors instead of writing empty; identical content skips the write |
| `edit_file` | Exact text replace (patch-like); ambiguous matches error; `replace_all`; identical old/new errors; undo restores precisely |
| `list_directory` | Directory tree, skipping build/cache dirs |
| `glob` | Find files by pattern like `src/**/*.cs`; string or array patterns; `*`, `?`, `**`, classes `[ab]`/`[a-z]`/`[!abc]` |
| `grep` | Regex content search, smart case + context lines; `include`/`exclude` globs; `case_sensitive` override |
| `run_command` | Run shell commands (build/test/git) with timeout; `env` extra variables; Git Bash / PowerShell picked automatically on Windows |
| `bash` | Run in bash (Git Bash on Windows) — pipes, env vars, Unix toolchain |
| `powershell` | Run in PowerShell (pwsh 7 preferred, else Windows PowerShell 5.1) |
| `stop` | Model signals the task is done |

## Architecture

```
src/CodeAgent/
├── Program.cs              # CLI entry: arg parsing + interactive REPL
├── Config.cs               # Config model and loading (codeagent.json / env)
├── InputLine.cs            # Input line: slash menu / history / key dispatch
├── EditableLine.cs         # Editable text buffer (cursor, insert/delete, word nav)
├── HistoryStore.cs         # Command history persistence (.codeagent/history.txt)
├── ConsoleRenderer.cs      # Markdown terminal rendering of replies
├── Modes.cs                # Work-mode catalog (8 built-ins + custom)
├── SetupWizard.cs          # Interactive provider setup wizard
├── AdofaiContext.cs        # ADOFAI mod project auto-adaptation
├── Util.cs                 # Utilities (glob, diff, truncation, encoding)
├── Providers/
│   ├── ProviderModels.cs   # Provider-agnostic message/tool-call IR
│   ├── IAgentProvider.cs   # Provider abstraction
│   ├── OpenAiProvider.cs   # OpenAI-compatible implementation
│   ├── AnthropicProvider.cs# Anthropic messages API + tool use
│   └── ProviderFactory.cs  # Creates providers from config
├── Tools/
│   ├── ToolRegistry.cs     # Tool registration/dispatch + workspace sandbox
│   ├── FileTools.cs        # read_file / write_file / edit_file / list_directory
│   ├── SearchTools.cs      # glob / grep
│   ├── CommandTool.cs      # run_command
│   └── SessionTools.cs     # stop
└── Agent/
    ├── Agent.cs            # Main loop: call, run tools, feed back, until done; context trim/compaction
    └── Agent.Session.cs    # Session persistence: jsonl logs, named snapshots, Markdown export
```

## Security notes

- File tools are sandboxed to the workspace; paths outside are rejected.
- `run_command` executes model-provided commands (inside your workspace, with your permissions). If that is a concern, set `"confirmCommands": true` for per-command confirmation, or `"allowCommands": false` to disable entirely.
- Prefer environment variables for API keys; add `codeagent.json` to `.gitignore` if it is ever committed.
- Session logs may contain code — keep the `.codeagent/` directory out of public repos.

## FAQ

**DeepSeek returns 400?** Some models reject certain `temperature`/`max_tokens` values — adjust the config; also verify the model name matches the official listing.

**How do I use Ollama?** After `ollama serve`, set `baseUrl: http://localhost:11434/v1`, `apiKey: "ollama"` as a placeholder, and a model name like `qwen2.5-coder:7b`.

**Claude keeps retrying?** Check `ANTHROPIC_API_KEY` and your balance; Anthropic requires alternating roles — CodeAgent normalizes them.

**The model replies without using tools?** The model probably lacks function calling / tool use — switch to a tool-capable model (gpt-4o, claude-sonnet, deepseek-chat, qwen3-coder and the like).

## Further documentation (Chinese)

- [项目介绍](docs/项目介绍.md) — positioning, architecture, design trade-offs
- [配置参考](docs/配置参考.md) — every `codeagent.json` field explained
- [工作模式](docs/工作模式.md) — built-in and custom work modes
- [工具参考](docs/工具参考.md) — parameters and behavior of the 10 built-in tools
- [快捷键参考](docs/快捷键参考.md) — every input-line key
- [常见问题](docs/常见问题.md) — troubleshooting
- [开发指南](docs/开发指南.md) — building, testing, contributing

## License

MIT
