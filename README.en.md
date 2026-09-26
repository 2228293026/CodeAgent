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
- 🔧 228 built-in tools: colour honours `NO_COLOR` (https://no-color.org) and `TERM=dumb`, and is disabled automatically when output is redirected (inline code then keeps its backticks, bold its asterisks and fenced code its fences, so no meaning is lost); set `CODEAGENT_THEME=light` for the light-background palette, `contrast` (dark background) or `contrast-light` (light background) for the high-contrast palette that lifts secondary text from DarkGray to Gray; `CODEAGENT_ASCII=1` falls box drawing back to pure ASCII (`▌ │ ─` → `| | -`); `read_file` (offset/limit/tail) / `read_files` (batch reads) / `compare_files` (text diff) / `compare_directories` (directory diff) / `project_stats` (project file metrics) / `code_metrics_report` (code line composition report) / `directory_size_report` (directory size report) / `file_info` (single-file metadata) / `file_infos` (batch file metadata) / `replace_in_files` (glob-based batch replacement) / `copy_file` (safe text-file copy) / `copy_files` (batch copy mappings) / `move_file` (safe move/rename) / `move_files` (batch move mappings) / `delete_file` (safe single-file deletion) / `delete_files` (batch safe deletion) / `find_duplicates` (duplicate-file detection) / `find_large_files` (large-file scan) / `find_stale_files` (stale-file scan) / `find_unreadable_files` (unreadable-file audit) / `find_temporary_files` (temporary-file scan) / `find_empty_files` (empty-file scan) / `find_recent_files` (recent-file scan) / `find_filename_conflicts` (case-insensitive filename conflict audit) / `find_long_paths` (long-path scan) / `backup_file` (safe file backup) / `restore_backup` (safe backup restoration) / `find_empty_directories` (empty-directory scan) / `find_symlinks` (symlink audit) / `checksum_manifest` (SHA256 manifest) / `encoding_report` (encoding distribution report) / `normalize_line_endings` (batch line-ending normalization) / `create_directory` (safe directory creation) / `remove_empty_directory` (safe empty-directory removal) / `file_extension_report` (file extension statistics) / `git_file_type_report` (Git tracked-file category report) / `line_ending_report` (line ending and BOM report) / `directory_depth_report` (directory depth report) / `directory_size_breakdown` (top-level directory size breakdown) / `project_manifest_report` (project manifest ecosystem report) / `config_key_report` (config key audit report) / `readme_section_report` (README heading structure report) / `changelog_report` (CHANGELOG release report) / `i18n_string_report` (Chinese string literal report) / `readme_link_check` (Markdown relative link check) / `http_url_audit_report` (hardcoded URL audit) / `test_inventory_report` (test file inventory report) / `test_flakiness_report` (test flakiness signal report) / `test_assertion_report` (test assertion strength report) / `debug_leftover_report` (debug leftover report) / `exception_handling_report` (exception handling report) / `resource_leak_report` (resource leak report) / `naming_convention_report` (naming convention report) / `code_style_report` (code style report) / `comment_quality_report` (comment quality report) / `concurrency_risk_report` (concurrency risk report) / `dead_implementation_report` (dead implementation report) / `input_validation_report` (input validation report) / `duplicate_code_report` (duplicate code report) / `test_isolation_report` (test isolation report) / `async_correctness_report` (async correctness report) / `collection_performance_report` (collection performance report) / `string_performance_report` (string performance report) / `null_safety_report` (null safety report) / `logging_practice_report` (logging practice report) / `api_usage_report` (api usage report) / `file_safety_report` (file safety report) / `dependency_version_report` (dependency version report) / `error_handling_detail_report` (error handling detail report) / `config_hardening_report` (config hardening report) / `api_compatibility_report` (api compatibility report) / `performance_anti_pattern_report` (performance anti-pattern report) / `data_contract_report` (data contract report) / `test_quality_report` (test quality report) / `resource_lifecycle_report` (resource lifecycle report) / `localization_readiness_report` (localization readiness report) / `documentation_sync_report` (documentation sync report) / `input_sanitization_report` (input sanitization report) / `observability_report` (observability report) / `build_config_report` (build config report) / `cache_strategy_report` (cache strategy report) / `concurrency_safety_report` (concurrency safety report) / `collection_usage_report` (collection usage report) / `expensive_call_report` (expensive call report) / `state_management_report` (state management report) / `log_hygiene_report` (log hygiene report) / `argument_safety_report` (argument safety report) / `lifetime_bounds_report` (lifetime bounds report) / `numeric_literal_report` (numeric literal report) / `dead_branch_report` (dead branch report) / `event_subscription_report` (event subscription report) / `inheritance_shape_report` (inheritance shape report) / `null_coalescing_report` (null handling report) / `equality_contract_report` (equality contract report) / `using_directive_report` (using directive report) / `disposable_pattern_report` (IDisposable report) / `string_format_report` (string construction report) / `enum_usage_report` (enum usage report) / `operator_overload_report` (operator overload report) / `doc_comment_report` (doc comment report) / `partial_method_report` (partial member report) / `obsolete_usage_report` (Obsolete usage report) / `extension_method_report` (extension method report) / `record_mutability_report` (record mutability report) / `nullable_annotation_report` (nullable annotation report) / `gitignore_quality_report` (.gitignore quality report) / `todo_comment_report` (TODO comment report) / `csharp_keyword_report` (C# keyword report) / `large_method_report` (large method report) / `duplicate_method_report` (duplicate implementation report) / `config_key_usage_report` (config key usage report) / `magic_number_report` (magic number report) / `gitattributes_quality_report` (.gitattributes quality report) / `line_ending_mixed_report` (mixed line ending report) / `todo_marker_report` (TODO/FIXME marker report) / `ci_workflow_report` (CI workflow overview) / `license_inventory_report` (license inventory report) / `dependency_lockfile_report` (dependency lockfile report) / `nuget_reference_report` (NuGet reference report) / `json_schema_report` (JSON Schema inventory report) / `secret_scan_report` (redacted secret scan) / `env_var_reference_report` (environment variable reference report) / `sensitive_file_inventory` (sensitive filename inventory) / `gitignore_pattern_report` (gitignore pattern report) / `gitignore_rule_report` (gitignore dead-rule report) / `gitattributes_report` (gitattributes rule report) / `git_hook_inventory` (Git hooks inventory) / `git_executable_bit_report` (Git file mode report) / `git_config_report` (Git config report) / `git_lfs_report` (Git LFS status report) / `git_gpg_status_report` (Git signing status report) / `git_ignored_files` (read-only Git ignored-file listing) / `git_path_diff` (read-only Git path changes) / `git_index_diagnostics` (read-only Git index conflict diagnostics) / `git_worktree_details` (read-only Git worktree details) / `git_stash_info` (read-only Git stash details) / `git_status` (read-only Git status) / `git_check_ignore` (read-only Git ignore check) / `git_check_attr` (read-only Git attribute check) / `git_ls_files` (read-only Git file listing) / `git_repository_info` (read-only Git repository info) / `git_repo_size_report` (.git size breakdown) / `git_fsck` (read-only Git integrity check) / `git_submodule_status` (read-only Git submodule status) / `git_describe` (read-only Git version description) / `git_contributors` (read-only Git contributor statistics) / `git_file_history` (read-only Git file history) / `git_file_stats` (read-only Git file stats) / `git_object_info` (read-only Git object type and size) / `git_tree_entries` (read-only Git tree entries) / `git_commit_info` (read-only Git commit metadata) / `git_compare_refs` (read-only compare Git refs) / `git_tag_info` (read-only Git tag info) / `git_diff` (read-only Git diff) / `git_log` (read-only Git history) / `git_blame` (read-only line provenance) / `git_show` (read-only commit details) / `git_branches` (read-only branch list) / `git_branch_age_report` (read-only branch staleness report) / `git_remotes` (read-only remote URLs with credential redaction) / `git_remote_sync_report` (read-only upstream sync status) / `git_tags` (read-only tag list) / `git_reflog` (read-only HEAD/reference movement history) / `git_worktrees` (read-only linked-worktree list) / `git_stashes` (read-only stash list) / `write_file` / `edit_file` (newline-style tolerant: LF↔CRLF normalized matching) / `list_directory` / `glob` / `grep` (multiline cross-line matching) / `run_command` / `bash` / `powershell` / `stop` (command tools auto-pick Git Bash / PowerShell); `edit_file` / `write_file` show a colored diff preview before executing
- 💭 Anthropic extended thinking fully supported: thinking text + signature + encrypted redacted_thinking blocks are round-tripped on tool-use turns (missing blocks make the API return 400)
- ↩️ Sessions auto-saved: `--continue` resumes the latest session, `/resume` restores by number, `/find <keyword>` searches past sessions, Esc rolls back turn by turn; `--no-session` skips logging for one run (privacy)
- 📊 Usage visibility: status bar shows per-turn tokens, current context size ctx (with percentage; window auto-detected for common models), thinking effort (`auto` probes supported levels and picks the highest) and the **current git branch**; `/compact [focus]` compresses history manually (with progress; ESC cancels; optional focus folded into the summarization prompt)
- ✅ Config validation: unknown keys / invalid enum values warn at startup (typos no longer fail silently)
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
  "autoCompactPercent": 0,       // auto-compress history when context usage reaches this percent; 0 = off (hint at 90%)
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
codeagent --no-session "..."          # do not write a session log for this run (privacy)
type bug.log | codeagent "analyze"    # piped stdin is appended to the task
```

Every message is logged to `.codeagent/sessions/*.jsonl` (`saveSessions`); `--continue` resumes the latest; `/resume` lists the 10 most recent (relative age + first-user-input preview) and restores by number; `/find <keyword>` searches across past sessions; `/export <n|all>` exports one of them to Markdown (a named snapshot wins over the number if they collide).

### Interactive REPL

```bash
codeagent
codeagent> What classes are under src?
codeagent> Add a unit test for ToolRegistry
codeagent> /model claude-sonnet-4-5
codeagent> /clear
codeagent> /exit
```

REPL commands: `/help` `/clear` `/compact` `/cls` `/model` `/provider` `/config` `/session` `/setup` `/undo` `/diff [N]` `/save` `/load` `/resume` `/find <keyword>` `/export [名/编号/all]` `/copy` `/prompt` `/stats` `/retry` `/tools` `/providers` `/models` `/history [N]` `/thinking` `/shell` `/mode` `/access` `/diag` `/exit` `/quit`.

Work modes: `/mode` lists, `/mode plan` switches. 8 built-ins: `code` (default, full power), `plan` / `explain` / `review` (read-only — write tools hidden and blocked), `debug` / `refactor` / `test` / `doc`. Custom modes live in the `modes` config list (system prompt + tool scope).

`/undo` rolls back the last `write_file` / `edit_file` change: newly created files are deleted, overwritten files restored (last 50 changes remembered). `/diff` shows the diff of the most recent change. `/copy` puts the last reply on the clipboard; `/prompt` shows the live system prompt.

`Ctrl+C` or `Esc` cancels the current turn gracefully (stops model thinking / tool execution; history rolls back to a valid state so the conversation can continue). Idle `Ctrl+C` exits.

Typing `/` opens the command menu (ANSI in-place rendering: arrows move, fill, Enter runs, Esc closes, typing filters; digits 1-9 run directly). Shortcuts: `Esc` (empty input) rolls back the last turn, `Tab` cycles work modes, `Shift+Tab` cycles file-access levels, `Alt+M` mode menu, `Alt+U` undo, `Alt+D` diff, `Ctrl+arrows` word motion, `Ctrl+Backspace/Delete` word delete, `Ctrl+R` history search. The status bar above each prompt shows mode, model, directory, and token usage.

## Tools

| Tool | Description |
|------|-------------|
| `read_file` | Read with line numbers; `offset`/`limit` paging, `no_line_numbers`; binary files and directories produce clear hints |
| `read_files` | Read up to 20 files in one call; reuses `read_file` range options, caps total output, and can continue after a per-file error |
| `compare_files` | Compare two text files with a unified diff; optionally ignores whitespace/blank lines and caps output |
| `compare_directories` | Compare added/removed/modified files across two directories with text diff summaries |
| `project_stats` | Summarize project file count, size, and extension distribution; filter extensions, count lines, and list largest files |
| `code_metrics_report` | Read-only count total/blank/comment/code lines and aggregate by directory |
| `source_composition_report` | Read-only per-language code/comment/blank composition, flags under-commented files |
| `file_info` | Inspect one file's size, timestamps, encoding, line/word counts, and optional SHA256 without printing its contents |
| `file_infos` | Batch inspect file sizes, encodings, line/word counts, and optional SHA256 |
| `replace_in_files` | Exact-replace text across glob-matched files with dry-run, case options, per-file reporting, and undo support |
| `copy_file` | Safely copy a text file with overwrite protection, dry-run, encoding/timestamp preservation, and undo |
| `move_file` | Safely move or rename a file with overwrite protection, dry-run, timestamp preservation, and undo records |
| `delete_file` | Safely delete one file with dry-run, missing_ok, optional `.bak` backup, and undo restoration |
| `find_duplicates` | Find byte-identical files by size and SHA256, with duplicate groups and reclaimable space |
| `copy_files` | Copy text files using multiple source/destination mappings, with per-item overwrite, dry-run, and error summaries |
| `git_status` | Read-only Git branch/worktree/staged status and diff stats via parameterized process invocation |
| `git_check_ignore` | Read-only check whether paths match Git ignore rules and show the matching source |
| `git_check_attr` | Read-only inspect Git attributes such as text, eol, filter, and merge |
| `git_ls_files` | Read-only list tracked or untracked Git files with glob, result, and output limits |
| `git_repository_info` | Read-only summarize repository root, branch, shallow state, and object counts |
| `git_repo_size_report` | Read-only measure .git subdirectory sizes with reclaim hints |
| `git_fsck` | Read-only check Git object, commit, and reference integrity with full/dangling modes |
| `git_submodule_status` | Read-only inspect submodule initialization, commit pointers, and nested submodules |
| `git_describe` | Read-only generate Git version descriptions with tag matching, long format, and dirty state |
| `git_contributors` | Read-only count Git contributors and commits across all refs |
| `git_file_history` | Read-only inspect one file's Git history with rename following and result limits |
| `git_file_stats` | Read-only summarize Git file size, line count, type, and latest commit |
| `git_object_info` | Read-only inspect a Git object's type and size |
| `git_tree_entries` | Read-only list Git tree entries, types, and object IDs |
| `git_commit_info` | Read-only inspect commit IDs, tree, parents, authors, and message |
| `git_compare_refs` | Read-only compare ref divergence and common ancestor |
| `git_tag_info` | Read-only inspect Git tag targets, types, and annotation details |
| `file_extension_report` | Read-only report file extension counts and total bytes |
| `git_file_type_report` | Read-only categorize tracked files by source/test/config/doc/asset |
| `line_ending_report` | Read-only report LF/CRLF, BOM, and binary status |
| `directory_depth_report` | Read-only analyze directory depth distribution and deepest directories |
| `directory_size_breakdown` | Read-only summarize file counts and bytes by top-level directory |
| `git_worktree_details` | Read-only inspect linked worktree HEAD, branch, and lock state |
| `git_stash_info` | Read-only inspect Git stash metadata and changed files |
| `project_manifest_report` | Read-only discover project manifests and summarize ecosystems |
| `target_framework_report` | Read-only summarize target frameworks, flag preview/EOL |
| `config_key_report` | Read-only audit config keys and flag likely misspellings |
| `readme_section_report` | Read-only extract README heading structure and flag missing sections |
| `changelog_report` | Read-only parse CHANGELOG releases and entry counts |
| `i18n_string_report` | Read-only extract Chinese string literals to size i18n work |
| `readme_link_check` | Read-only check Markdown relative links for broken targets |
| `doc_freshness_report` | Read-only check broken anchors, version drift, stale references |
| `http_url_audit_report` | Read-only audit hardcoded URLs and flag placeholders |
| `test_inventory_report` | Read-only discover test files and summarize frameworks/assertion clues |
| `test_flakiness_report` | Read-only scan tests for flakiness signals with advice |
| `test_assertion_report` | Read-only audit assertion strength: tautologies, empty and brittle asserts |
| `debug_leftover_report` | Read-only scan debug leftovers: breakpoints, ReadKey, commented-out tests |
| `exception_handling_report` | Read-only audit exception handling: empty catch, broad catch, unused variables |
| `resource_leak_report` | Read-only audit resource disposal: missing using/Dispose, undisposed fields |
| `naming_convention_report` | Read-only check filename/type agreement and vague type names |
| `code_style_report` | Read-only stats: var ratio, brace style mix, missing file headers |
| `comment_quality_report` | Read-only check TODO ownership, empty comment lines, marker distribution |
| `concurrency_risk_report` | Read-only check concurrency risks: blocking waits, un-awaited calls, shared state |
| `dead_implementation_report` | Read-only check dead code: unimplemented, constant returns, base-only overrides |
| `input_validation_report` | Read-only check input validation: env null checks, Parse vs TryParse, arg bounds |
| `duplicate_code_report` | Read-only check duplicate code: identical fragments and repeated constants |
| `test_isolation_report` | Read-only check test isolation: shared state, nondeterminism, missing cleanup |
| `async_correctness_report` | Read-only check async: async void, no await, Task without return, missing token |
| `collection_performance_report` | Read-only check collections: leaked internals, LINQ in loops, repeated materialization |
| `logging_practice_report` | Read-only check logging: console writes, Message-only, swallowing catch |
| `null_safety_report` | Read-only check null safety: unchecked index, OrDefault chaining, missing ?? |
| `string_performance_report` | Read-only check string/regex performance: Regex in loops, nested quantifiers, concat in loops |
| `api_usage_report` | Read-only check API usage: hardcoded URLs, no timeout, unvalidated deserialize |
| `file_safety_report` | Read-only check file safety: unguarded delete, symlinks, path concatenation |
| `dependency_version_report` | Read-only check dependency versions: wildcards, floating versions, machine-locked |
| `error_handling_detail_report` | Read-only check error handling detail: silent defaults, lost inner exceptions, unobserved tasks |
| `config_hardening_report` | Read-only check config hardening: hardcoded secrets, plaintext credentials, insecure defaults |
| `api_compatibility_report` | Read-only check API compatibility: deprecated members still used, undocumented Obsolete, nullable drift |
| `performance_anti_pattern_report` | Read-only check performance anti-patterns: repeated work in loops, nested linear scans, allocations in loops |
| `data_contract_report` | Read-only check data contracts: field/property mix, object dict keys, mutable statics |
| `test_quality_report` | Read-only check test quality: tautological assertions, weak assertions, missing assertions |
| `resource_lifecycle_report` | Read-only check resource lifecycle: undisposed objects, missing unsubscribes, finalizer without Dispose |
| `localization_readiness_report` | Read-only check localization readiness: hardcoded Chinese UI text, missing resources, hardcoded date formats |
| `documentation_sync_report` | Read-only check documentation sync: undocumented public members, README gaps, commented-out code |
| `input_sanitization_report` | Read-only check input handling: unquoted shell concatenation, unbounded input, unvalidated tool args |
| `observability_report` | Read-only check observability: secrets in logs, silently swallowed errors, Console instead of logging |
| `build_config_report` | Read-only check build config: version drift, Debug in pipelines, missing .gitignore entries |
| `cache_strategy_report` | Read-only check cache strategy: unbounded caches, repeated computation, non-unique cache keys |
| `concurrency_safety_report` | Read-only check concurrency safety: locks held across await, unguarded shared writes, raw threads |
| `collection_usage_report` | Read-only check collection usage: += in loops, List.Contains lookups, repeated materialization |
| `expensive_call_report` | Read-only check call cost: expensive calls in loops, new Regex in loops, calls scattered across files |
| `state_management_report` | Read-only check state management: singletons holding mutable collections, exposed mutable collections, incomplete Reset |
| `log_hygiene_report` | Read-only check log hygiene: secrets in logs, full payloads in exceptions, debug residue |
| `argument_safety_report` | Read-only check argument safety: arguments used as paths, missing guards, commands without ArgumentList |
| `lifetime_bounds_report` | Read-only check capacity and bounds: arrays sized by arguments, unbounded collection, magic-number thresholds |
| `numeric_literal_report` | Read-only check numbers and units: magic numbers, float equality comparisons, byte/char unit mixups |
| `dead_branch_report` | Read-only check branch reachability: both branches return, dangling return, constant conditions |
| `event_subscription_report` | Read-only check event subscriptions: missing unsubscribe, lambda subscriptions, static events |
| `inheritance_shape_report` | Read-only check inheritance shape: uninitialized non-null fields, new-hiding members, member hiding |
| `null_coalescing_report` | Read-only check null handling: long ?? chains, consecutive null checks, repeated dereference |
| `equality_contract_report` | Read-only check equality contract: Equals without GetHashCode, == mixed with Equals, struct using Equals |
| `using_directive_report` | Read-only check using directives: namespaces unused in the body, implicit System.* noise, too many System.* |
| `disposable_pattern_report` | Read-only check IDisposable: finalizer without SuppressFinalize, missing Dispose, undisposed fields |
| `string_format_report` | Read-only check string construction: += in loops, format arity mismatch, IFormattable without FormattableString |
| `enum_usage_report` | Read-only check enum usage: switch without default, bitwise without [Flags], ordered comparison of enums |
| `operator_overload_report` | Read-only check operator overloads: half-overloaded operator pairs, inconsistent equality semantics |
| `doc_comment_report` | Read-only check XML doc comments: public member without summary, unclosed tags, empty summary |
| `partial_method_report` | Read-only check partial members: half-written modifier, declaration without implementation, type split across files |
| `obsolete_usage_report` | Read-only check [Obsolete]: marked but still used, error:true still used, no replacement stated |
| `extension_method_report` | Read-only check extension methods: in a non-static class, first parameter without this, defined in a nested class |
| `record_mutability_report` | Read-only check record immutability: mutable collection fields, state-changing members, record struct |
| `nullable_annotation_report` | Read-only check nullable annotations: #nullable disable, bare reference types, collection element without ? |
| `gitignore_quality_report` | Read-only check .gitignore: backslashes that break cross-platform, absolute paths, over-broad wildcards, missing common artifacts |
| `todo_comment_report` | Read-only check TODO/FIXME markers: no owner, no linked ticket, marker inside commented-out code |
| `csharp_keyword_report` | Read-only check C# keywords: declared name shaped like or differing only in case from a keyword, redundant @ prefix |
| `large_method_report` | Read-only check oversized methods: body too long, nesting too deep, too many parameters, too many branches |
| `duplicate_method_report` | Read-only check duplicate implementations: identical method bodies, repeated method names, repeated comment blocks |
| `config_key_usage_report` | Read-only check config keys: declared but never read, read without declaration, inconsistent naming style |
| `magic_number_report` | Read-only check magic numbers: repeated literals, bare literals in comparisons, port/timeout/buffer magnitudes |
| `gitattributes_quality_report` | Read-only check .gitattributes: missing text=auto, missing eol, contradictory/duplicate rules, binaries treated as text |
| `line_ending_mixed_report` | Read-only check line endings: CRLF/LF mixed across the repo or inside one file, missing trailing newline |
| `stale_comment_report` | Read-only check stale comments: comments that merely restate the identifier, commented-out dead code, stale markers |
| `string_concat_perf_report` | Read-only check string concatenation cost: repeated += or = a + b inside a loop, invariant literal prefixes, over-long multi-part concatenation |
| `test_placeholder_report` | Read-only check test placeholders: tests without assertions, tautological assertions, empty assertions, commented-out tests |
| `public_api_surface_report` | Read-only check public API stability: mutable defaults, public fields, position-only bool parameters |
| `optional_param_dropped_report` | Read-only check interface/implementation signature drift: lost optional-parameter defaults, missing ref/out/in, mismatched return type |
| `switch_exhaustiveness_report` | Read-only check switch exhaustiveness: missing default, enum switch missing members, duplicated case labels |
| `linq_query_report` | Read-only check LINQ traps: repeated queries inside a loop, the same sequence enumerated twice, single-predicate LINQ in a loop |
| `swallowed_exception_report` | Read-only check swallowed exceptions: empty catch blocks, neither rethrown nor logged, catch type not matching the thrown type |
| `equals_gethashcode_report` | Read-only check the Equals/GetHashCode contract: missing GetHashCode, missing Equals, the two methods keyed on different fields |
| `deferred_query_report` | Read-only check LINQ deferred-execution traps: unmaterialised queries, mutating a collection inside foreach, the same variable enumerated repeatedly |
| `boxing_allocation_report` | Read-only check implicit allocations on hot paths: boxing, string interpolation inside a loop, ToList() immediately re-enumerated |
| `time_unit_confusion_report` | Read-only check time-unit traps: named ms but valued in seconds, named seconds but valued in milliseconds, unitless timeout values |
| `culture_format_report` | Read-only check culture traps: ToString/Parse without a culture, ToUpper/ToLower without the invariant culture |
| `concurrent_mutation_report` | Read-only check concurrent writes to shared state: parallel writes to a shared collection, unlocked counter increments, captured variables mutated in parallel |
| `undisposed_stream_report` | Read-only check stream and handle leaks: disposable objects not wrapped in using, manual Dispose, returning a disposable directly |
| `null_assignment_report` | Read-only check nullability traps: non-null variables assigned null, dereferenced after a null check without returning, non-null methods returning null |
| `boolean_parameter_report` | Read-only check boolean parameter traps: optional parameters after required ones, semantically vaguely named flags, duplicate parameter names |
| `hardcoded_secret_report` | Read-only check hardcoded credentials: API key/token/password literals, connection-string passwords, keys reaching logs or exceptions |
| `path_escape_report` | Read-only check path escape: Path.Combine without a root check, `..` traversal, string-prefix path containment tests |
| `duplicate_logic_report` | Read-only check copy-paste: one literal under several constant names, identical method bodies, scattered magic numbers |
| `silent_catch_report` | Read-only check silent failure: empty catch blocks, unused catch variables, returning a default after catching |
| `interface_contract_report` | Read-only check interface contract drift: unimplemented members, parameter types that quietly differ, a possibly missing override |
| `testability_report` | Read-only check testability: over-long methods, long private logic, unpaired if/else |
| `lock_misuse_report` | Read-only check lock misuse: await inside a lock block, locking a value type or literal, the same lock nested |
| `dependency_inversion_report` | Read-only check dependency-inversion gaps: fields/constructor parameters typed as concrete classes, constructing infrastructure directly |
| `ownership_report` | Read-only check resource ownership: IDisposable without Dispose, disposable parameters with unclear ownership, injected fields with no stated owner |
| `weak_assertion_report` | Read-only check weak assertions: tests with no assertion, always-true assertions, exceptions swallowed inside a test, shared mutable static state |
| `untrusted_input_report` | Read-only check untrusted input: shell/SQL concatenation, unvalidated external paths, dynamic assembly loading |
| `retry_safety_report` | Read-only check retry correctness: fixed interval with no backoff, no attempt limit, non-idempotent writes inside a retry |
| `state_machine_report` | Read-only check state-machine holes: state fields assigned values outside the enum, switch without default, unguarded state transitions |
| `observability_gap_report` | Read-only check observability gaps: catches with no trace, key writes with no log, logs with no correlation id |
| `time_zone_report` | Read-only check time-zone traps: Now mixed with UtcNow, conversion after losing Kind, storing instants in DateTime |
| `collection_edge_report` | Read-only check collection edges: First/aggregate without an empty check, indexers instead of TryGetValue, List.Contains inside a loop |
| `contract_evolution_report` | Read-only check contract-evolution risk: required parameters without defaults, returning internal collection references, interface members with no default implementation |
| `config_robustness_report` | Read-only check config fragility: environment variables read without a guard, silent parse fallbacks, numeric config with no range validation |
| `float_precision_report` | Read-only check floating-point/amount precision: float literals in arithmetic, float == comparison, Math.Round without a rounding mode, Parse without a fixed culture |
| `enum_flag_report` | Read-only check enum/flag misuse: enums compared to numeric literals, [Flags] enums compared with ==, booleans compared to true/false |
| `async_misuse_report` | Read-only check async/await misuse: .Result/.Wait() blocking (deadlock), async methods with no await, fire-and-forget calls that swallow exceptions |
| `api_version_pin_report` | Read-only check project-file dependency versions: ^ ~ * ranges instead of exact pins, pre-release versions, the same package pinned to several versions |
| `nested_try_report` | Read-only check try/finally control-flow traps: an inner catch swallowing exceptions, return/throw in finally overriding the original exception, return in a loop finally |
| `iterator_yield_report` | Read-only check iterator traps: return truncating the sequence, yield inside try-catch, blocking I/O inside an iterator |
| `switch_fallthrough_report` | Read-only check switch fallthrough: a case branch with no break/return falling into the next case, default not last |
| `linq_deferred_report` | Read-only check LINQ deferred execution: a deferred query used repeatedly, the same query re-enumerated in a loop, mutating the source collection after building a query |
| `string_interpolation_report` | Read-only check string concatenation style: + inside a loop (O(n^2)), many-part concatenation instead of interpolation, log messages concatenated in a loop |
| `array_bounds_report` | Read-only check array bounds: arr[arr.Length] off-by-one, subtraction indices with no lower-bound guard, indexing a possibly-empty result |
| `readonly_struct_report` | Read-only check struct semantics: setters in a readonly struct, a mutable struct used as a collection key then mutated, defensive copies |
| `integer_overflow_report` | Read-only check silent integer errors: unchecked wraparound, integer division truncating toward zero, narrowing casts losing precision |
| `doc_comment_report` | Read-only check XML doc comments: public member without summary, unclosed tags, empty summary |
| `partial_method_report` | Read-only check partial members: half-written modifier, declaration without implementation, type split across files |
| `obsolete_usage_report` | Read-only check [Obsolete]: marked but still used, error:true still used, no replacement stated |
| `extension_method_report` | Read-only check extension methods: in a non-static class, first parameter without this, defined in a nested class |
| `record_mutability_report` | Read-only check record immutability: mutable collection fields, state-changing members, record struct |
| `nullable_annotation_report` | Read-only check nullable annotations: #nullable disable, bare reference types, collection element without ? |
| `gitignore_quality_report` | Read-only check .gitignore: backslashes that break cross-platform, absolute paths, over-broad wildcards, missing common artifacts |
| `todo_comment_report` | Read-only check TODO/FIXME markers: no owner, no linked ticket, marker inside commented-out code |
| `csharp_keyword_report` | Read-only check C# keywords: declared name shaped like or differing only in case from a keyword, redundant @ prefix |
| `large_method_report` | Read-only check oversized methods: body too long, nesting too deep, too many parameters, too many branches |
| `duplicate_method_report` | Read-only check duplicate implementations: identical method bodies, repeated method names, repeated comment blocks |
| `config_key_usage_report` | Read-only check config keys: declared but never read, read without declaration, inconsistent naming style |
| `magic_number_report` | Read-only check magic numbers: repeated literals, bare literals in comparisons, port/timeout/buffer magnitudes |
| `gitattributes_quality_report` | Read-only check .gitattributes: missing text=auto, missing eol, contradictory/duplicate rules, binaries treated as text |
| `line_ending_mixed_report` | Read-only check line endings: CRLF/LF mixed across the repo or inside one file, missing trailing newline |
| `stale_comment_report` | Read-only check stale comments: comments that merely restate the identifier, commented-out dead code, stale markers |
| `string_concat_perf_report` | Read-only check string concatenation cost: repeated += or = a + b inside a loop, invariant literal prefixes, over-long multi-part concatenation |
| `test_placeholder_report` | Read-only check test placeholders: tests without assertions, tautological assertions, empty assertions, commented-out tests |
| `public_api_surface_report` | Read-only check public API stability: mutable defaults, public fields, position-only bool parameters |
| `optional_param_dropped_report` | Read-only check interface/implementation signature drift: lost optional-parameter defaults, missing ref/out/in, mismatched return type |
| `switch_exhaustiveness_report` | Read-only check switch exhaustiveness: missing default, enum switch missing members, duplicated case labels |
| `linq_query_report` | Read-only check LINQ traps: repeated queries inside a loop, the same sequence enumerated twice, single-predicate LINQ in a loop |
| `swallowed_exception_report` | Read-only check swallowed exceptions: empty catch blocks, neither rethrown nor logged, catch type not matching the thrown type |
| `equals_gethashcode_report` | Read-only check the Equals/GetHashCode contract: missing GetHashCode, missing Equals, the two methods keyed on different fields |
| `deferred_query_report` | Read-only check LINQ deferred-execution traps: unmaterialised queries, mutating a collection inside foreach, the same variable enumerated repeatedly |
| `boxing_allocation_report` | Read-only check implicit allocations on hot paths: boxing, string interpolation inside a loop, ToList() immediately re-enumerated |
| `time_unit_confusion_report` | Read-only check time-unit traps: named ms but valued in seconds, named seconds but valued in milliseconds, unitless timeout values |
| `culture_format_report` | Read-only check culture traps: ToString/Parse without a culture, ToUpper/ToLower without the invariant culture |
| `concurrent_mutation_report` | Read-only check concurrent writes to shared state: parallel writes to a shared collection, unlocked counter increments, captured variables mutated in parallel |
| `undisposed_stream_report` | Read-only check stream and handle leaks: disposable objects not wrapped in using, manual Dispose, returning a disposable directly |
| `null_assignment_report` | Read-only check nullability traps: non-null variables assigned null, dereferenced after a null check without returning, non-null methods returning null |
| `boolean_parameter_report` | Read-only check boolean parameter traps: optional parameters after required ones, semantically vaguely named flags, duplicate parameter names |
| `hardcoded_secret_report` | Read-only check hardcoded credentials: API key/token/password literals, connection-string passwords, keys reaching logs or exceptions |
| `path_escape_report` | Read-only check path escape: Path.Combine without a root check, `..` traversal, string-prefix path containment tests |
| `duplicate_logic_report` | Read-only check copy-paste: one literal under several constant names, identical method bodies, scattered magic numbers |
| `silent_catch_report` | Read-only check silent failure: empty catch blocks, unused catch variables, returning a default after catching |
| `interface_contract_report` | Read-only check interface contract drift: unimplemented members, parameter types that quietly differ, a possibly missing override |
| `testability_report` | Read-only check testability: over-long methods, long private logic, unpaired if/else |
| `lock_misuse_report` | Read-only check lock misuse: await inside a lock block, locking a value type or literal, the same lock nested |
| `dependency_inversion_report` | Read-only check dependency-inversion gaps: fields/constructor parameters typed as concrete classes, constructing infrastructure directly |
| `ownership_report` | Read-only check resource ownership: IDisposable without Dispose, disposable parameters with unclear ownership, injected fields with no stated owner |
| `weak_assertion_report` | Read-only check weak assertions: tests with no assertion, always-true assertions, exceptions swallowed inside a test, shared mutable static state |
| `untrusted_input_report` | Read-only check untrusted input: shell/SQL concatenation, unvalidated external paths, dynamic assembly loading |
| `retry_safety_report` | Read-only check retry correctness: fixed interval with no backoff, no attempt limit, non-idempotent writes inside a retry |
| `state_machine_report` | Read-only check state-machine holes: state fields assigned values outside the enum, switch without default, unguarded state transitions |
| `observability_gap_report` | Read-only check observability gaps: catches with no trace, key writes with no log, logs with no correlation id |
| `time_zone_report` | Read-only check time-zone traps: Now mixed with UtcNow, conversion after losing Kind, storing instants in DateTime |
| `collection_edge_report` | Read-only check collection edges: First/aggregate without an empty check, indexers instead of TryGetValue, List.Contains inside a loop |
| `contract_evolution_report` | Read-only check contract-evolution risk: required parameters without defaults, returning internal collection references, interface members with no default implementation |
| `config_robustness_report` | Read-only check config fragility: environment variables read without a guard, silent parse fallbacks, numeric config with no range validation |
| `float_precision_report` | Read-only check floating-point/amount precision: float literals in arithmetic, float == comparison, Math.Round without a rounding mode, Parse without a fixed culture |
| `enum_flag_report` | Read-only check enum/flag misuse: enums compared to numeric literals, [Flags] enums compared with ==, booleans compared to true/false |
| `async_misuse_report` | Read-only check async/await misuse: .Result/.Wait() blocking (deadlock), async methods with no await, fire-and-forget calls that swallow exceptions |
| `api_version_pin_report` | Read-only check project-file dependency versions: ^ ~ * ranges instead of exact pins, pre-release versions, the same package pinned to several versions |
| `nested_try_report` | Read-only check try/finally control-flow traps: an inner catch swallowing exceptions, return/throw in finally overriding the original exception, return in a loop finally |
| `iterator_yield_report` | Read-only check iterator traps: return truncating the sequence, yield inside try-catch, blocking I/O inside an iterator |
| `switch_fallthrough_report` | Read-only check switch fallthrough: a case branch with no break/return falling into the next case, default not last |
| `linq_deferred_report` | Read-only check LINQ deferred execution: a deferred query used repeatedly, the same query re-enumerated in a loop, mutating the source collection after building a query |
| `string_interpolation_report` | Read-only check string concatenation style: + inside a loop (O(n^2)), many-part concatenation instead of interpolation, log messages concatenated in a loop |
| `array_bounds_report` | Read-only check array bounds: arr[arr.Length] off-by-one, subtraction indices with no lower-bound guard, indexing a possibly-empty result |
| `readonly_struct_report` | Read-only check struct semantics: setters in a readonly struct, a mutable struct used as a collection key then mutated, defensive copies |
| `integer_overflow_report` | Read-only check silent integer errors: unchecked wraparound, integer division truncating toward zero, narrowing casts losing precision |
 `todo_marker_report` | Read-only scan TODO/FIXME/XXX/HACK markers |
| `ci_workflow_report` | Read-only summarize CI workflow triggers, runners, and commands |
| `workflow_hygiene_report` | Read-only audit workflow triggers, redundant needs, missing timeouts |
| `license_inventory_report` | Read-only discover license files, manifest declarations, and SPDX markers |
| `dependency_lockfile_report` | Read-only discover dependency lockfiles and summarize ecosystem/size/entries |
| `nuget_reference_report` | Read-only audit PackageReference floating versions, duplicates, deprecated |
| `json_schema_report` | Read-only summarize JSON Schema `$id`/title/draft |
| `secret_scan_report` | Read-only scan suspected secrets and redact matched values |
| `env_var_reference_report` | Read-only summarize referenced environment variables |
| `sensitive_file_inventory` | Read-only discover filenames that may contain private configuration |
| `gitignore_pattern_report` | Read-only analyze .gitignore patterns, negations, and wildcards |
| `gitignore_rule_report` | Read-only find ignored-but-tracked files and dead negations |
| `gitattributes_report` | Read-only analyze .gitattributes path and attribute rules |
| `editorconfig_report` | Read-only check .editorconfig duplicates, missing root, untracked configs |
| `git_hook_inventory` | Read-only list active and sample Git hooks |
| `git_executable_bit_report` | Read-only summarize index modes, symlinks, and submodules |
| `git_config_report` | Read-only summarize Git config keys, origins, and sections |
| `git_lfs_report` | Read-only inspect Git LFS availability, patterns, and tracked files |
| `git_gpg_status_report` | Read-only inspect Git signing config and latest commit signature |
| `git_ignored_files` | Read-only list untracked files covered by Git ignore rules |
| `git_path_diff` | Read-only compare file changes for a path between two refs |
| `git_index_diagnostics` | Read-only diagnose unmerged index files and conflict stages |
| `git_diff` | Read-only unified diff for unstaged or staged changes, with context, timeout, and output limits |
| `git_log` | Read-only Git history with path, keyword, author filters, and optional file lists |
| `git_blame` | Read-only line provenance with commit, author, date, line ranges, and output limits |
| `git_show` | Read-only commit author, message, stats, and unified diff with path filtering |
| `git_branches` | Read-only local/remote branch list with latest commit summaries, patterns, and limits |
| `git_branch_age_report` | Read-only rank branches by last commit and flag stale ones |
| `git_remotes` | Read-only Git remote fetch/push URLs with credential redaction |
| `git_remote_sync_report` | Read-only ahead/behind counts vs each branch upstream |
| `git_tags` | Read-only Git tag list with object type, date, subject, pattern, and limits |
| `git_reflog` | Read-only Git HEAD/reference movement history |
| `git_worktrees` | Read-only linked worktree, HEAD, branch, and lock state with external paths redacted |
| `git_stashes` | Read-only Git stash identifiers, hashes, dates, and messages |
| `find_large_files` | Scan and rank large project files with extension and count limits |
| `find_empty_directories` | Recursively find empty directories with depth, result, and hidden filters |
| `find_symlinks` | Audit symlinks/junctions with targets and broken-link state without following them |
| `checksum_manifest` | Generate SHA256 manifests with extension, size, and count limits |
| `encoding_report` | Summarize UTF-8, BOM, and GB18030/GBK encoding distribution across project files |
| `normalize_line_endings` | Normalize text files to LF/CRLF with dry-run, backup, and undo support |
| `create_directory` | Safely create directories with parents, exist_ok, dry-run, and symlink protection |
| `remove_empty_directory` | Safely remove empty directories while rejecting files, symlinks, and non-empty paths |
| `find_stale_files` | Find files not modified for a configurable number of days with extension and count limits |
| `directory_size_report` | Aggregate file counts and bytes by directory to locate storage hotspots |
| `find_unreadable_files` | Audit project files that the current process cannot read and report the reason |
| `find_temporary_files` | Find .bak, .tmp, .orig, .swp, and other temporary or backup leftovers |
| `backup_file` | Safely back up one file with custom suffix, destination, overwrite, and dry-run options |
| `restore_backup` | Restore a file from backup, optionally preserving the current file as .pre-restore, with dry-run |
| `find_empty_files` | Find zero-byte files with extension, hidden-file, and count limits |
| `find_recent_files` | Find files changed within a configurable time window with extension and count limits |
| `find_filename_conflicts` | Audit same-name files across directories using case-insensitive matching |
| `find_long_paths` | Find paths that may hit traditional Windows path-length limits |
| `move_files` | Move files using multiple source/destination mappings with per-item overwrite and dry-run |
| `delete_files` | Safely delete multiple files with dry-run, missing_ok, backups, error continuation, and undo |
| `write_file` | Create/overwrite a file, creating parent dirs; missing `content` errors instead of writing empty; identical content skips the write |
| `edit_file` | Exact text replace (patch-like); ambiguous matches error; `replace_all`; identical old/new errors; undo restores precisely |
| `list_directory` | Directory tree, skipping build/cache dirs |
| `glob` | Find files by pattern like `src/**/*.cs`; string or array patterns; `*`, `?`, `**`, classes `[ab]`/`[a-z]`/`[!abc]` |
| `grep` | Regex content search, smart case + context lines; `include`/`exclude` globs; `case_sensitive` override; `multiline`; `count_only` |
| `run_command` | Run shell commands (build/test/git) with timeout; `env` extra variables; Git Bash / PowerShell picked automatically on Windows |
| `bash` | Run in bash (Git Bash on Windows) — pipes, env vars, Unix toolchain |
| `powershell` | Run in PowerShell (pwsh 7 preferred, else Windows PowerShell 5.1) |
| `session_search` | Search past session logs and named snapshots for a keyword, newest first — recall earlier conclusions across sessions |
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
│   ├── ReadFilesTool.cs    # read_files batch reader
│   ├── CompareFilesTool.cs # compare_files text diff
│   ├── CompareDirectoriesTool.cs # compare_directories directory diff
│   ├── ProjectStatsTool.cs # project_stats project metrics
│   ├── CodeMetricsReportTool.cs # code_metrics_report code line composition report
│   ├── SourceCompositionReportTool.cs # source_composition_report per-language composition
│   ├── FileInfoTool.cs     # file_info single-file metadata
│   ├── FileInfosTool.cs    # file_infos batch file metadata
│   ├── ReplaceInFilesTool.cs # replace_in_files batch replacement
│   ├── CopyFileTool.cs     # copy_file safe text copy
│   ├── MoveFileTool.cs     # move_file safe move
│   ├── DeleteFileTool.cs   # delete_file safe delete
│   ├── FindDuplicatesTool.cs # find_duplicates duplicate detection
│   ├── CopyFilesTool.cs   # copy_files batch copy
│   ├── GitStatusTool.cs   # git_status read-only Git status
│   ├── GitCheckIgnoreTool.cs # git_check_ignore Git ignore check
│   ├── GitCheckAttrTool.cs   # git_check_attr Git attribute check
│   ├── GitLsFilesTool.cs     # git_ls_files Git file listing
│   ├── GitRepositoryInfoTool.cs # git_repository_info Git repository info
│   ├── GitRepoSizeReportTool.cs # git_repo_size_report .git size report
│   ├── GitFsckTool.cs           # git_fsck Git integrity check
│   ├── GitSubmoduleStatusTool.cs # git_submodule_status Git submodule status
│   ├── GitDescribeTool.cs         # git_describe Git version description
│   ├── GitContributorsTool.cs     # git_contributors Git contributor statistics
│   ├── GitFileHistoryTool.cs      # git_file_history Git file history
│   ├── GitFileStatsTool.cs        # git_file_stats Git file stats
│   ├── GitObjectInfoTool.cs       # git_object_info Git object info
│   ├── GitTreeEntriesTool.cs      # git_tree_entries Git tree entries
│   ├── GitCommitInfoTool.cs       # git_commit_info Git commit metadata
│   ├── GitCompareRefsTool.cs      # git_compare_refs Git ref comparison
│   ├── GitTagInfoTool.cs          # git_tag_info Git tag info
│   ├── FileExtensionReportTool.cs  # file_extension_report extension report
│   ├── GitFileTypeReportTool.cs     # git_file_type_report Git file category report
│   ├── LineEndingReportTool.cs      # line_ending_report line ending report
│   ├── DirectoryDepthReportTool.cs  # directory_depth_report depth report
│   ├── DirectorySizeBreakdownTool.cs # directory_size_breakdown size breakdown
│   ├── GitWorktreeDetailsTool.cs    # git_worktree_details Git worktree details
│   ├── GitStashInfoTool.cs          # git_stash_info Git stash details
│   ├── ProjectManifestReportTool.cs  # project_manifest_report project manifest report
│   ├── TargetFrameworkReportTool.cs   # target_framework_report target framework report
│   ├── ConfigKeyReportTool.cs         # config_key_report config key audit report
│   ├── ReadmeSectionReportTool.cs     # readme_section_report README section report
│   ├── ChangelogReportTool.cs          # changelog_report CHANGELOG release report
│   ├── I18nStringReportTool.cs         # i18n_string_report Chinese string literal report
│   ├── ReadmeLinkCheckTool.cs         # readme_link_check Markdown link check
│   ├── DocFreshnessReportTool.cs      # doc_freshness_report doc freshness report
│   ├── HttpUrlAuditReportTool.cs      # http_url_audit_report hardcoded URL audit
│   ├── TestInventoryReportTool.cs     # test_inventory_report test inventory report
│   ├── TestFlakinessReportTool.cs     # test_flakiness_report test flakiness report
│   ├── TestAssertionReportTool.cs     # test_assertion_report test assertion strength report
│   ├── DebugLeftoverReportTool.cs     # debug_leftover_report debug leftover report
│   ├── ExceptionHandlingReportTool.cs # exception_handling_report exception handling report
│   ├── ResourceLeakReportTool.cs      # resource_leak_report resource leak report
│   ├── NamingConventionReportTool.cs  # naming_convention_report naming convention report
│   ├── CodeStyleReportTool.cs         # code_style_report code style report
│   ├── CommentQualityReportTool.cs    # comment_quality_report comment quality report
│   ├── ConcurrencyRiskReportTool.cs   # concurrency_risk_report concurrency risk report
│   ├── DeadImplementationReportTool.cs # dead_implementation_report dead implementation report
│   ├── InputValidationReportTool.cs   # input_validation_report input validation report
│   ├── DuplicateCodeReportTool.cs     # duplicate_code_report duplicate code report
│   ├── TestIsolationReportTool.cs     # test_isolation_report test isolation report
│   ├── AsyncCorrectnessReportTool.cs  # async_correctness_report async correctness report
│   ├── CollectionPerformanceReportTool.cs # collection_performance_report collection performance report
│   ├── StringPerformanceReportTool.cs # string_performance_report string performance report
│   ├── NullSafetyReportTool.cs        # null_safety_report null safety report
│   ├── LoggingPracticeReportTool.cs   # logging_practice_report logging practice report
│   ├── ApiUsageReportTool.cs          # api_usage_report api usage report
│   ├── FileSafetyReportTool.cs        # file_safety_report file safety report
│   ├── DependencyVersionReportTool.cs # dependency_version_report dependency version report
│   ├── ErrorHandlingDetailReportTool.cs # error_handling_detail_report error handling detail report
│   ├── ConfigHardeningReportTool.cs   # config_hardening_report config hardening report
│   ├── ApiCompatibilityReportTool.cs  # api_compatibility_report api compatibility report
│   ├── PerformanceAntiPatternReportTool.cs # performance_anti_pattern_report performance anti-pattern report
│   ├── DataContractReportTool.cs        # data_contract_report data contract report
│   ├── TestQualityReportTool.cs         # test_quality_report test quality report
│   ├── ResourceLifecycleReportTool.cs   # resource_lifecycle_report resource lifecycle report
│   ├── LocalizationReadinessReportTool.cs # localization_readiness_report localization readiness report
│   ├── DocumentationSyncReportTool.cs   # documentation_sync_report documentation sync report
│   ├── InputSanitizationReportTool.cs   # input_sanitization_report input sanitization report
│   ├── ObservabilityReportTool.cs       # observability_report observability report
│   ├── BuildConfigReportTool.cs         # build_config_report build config report
│   ├── CacheStrategyReportTool.cs       # cache_strategy_report cache strategy report
│   ├── ConcurrencySafetyReportTool.cs   # concurrency_safety_report concurrency safety report
│   ├── CollectionUsageReportTool.cs     # collection_usage_report collection usage report
│   ├── ExpensiveCallReportTool.cs       # expensive_call_report expensive call report
│   ├── StateManagementReportTool.cs     # state_management_report state management report
│   ├── LogHygieneReportTool.cs          # log_hygiene_report log hygiene report
│   ├── ArgumentSafetyReportTool.cs      # argument_safety_report argument safety report
│   ├── LifetimeBoundsReportTool.cs      # lifetime_bounds_report lifetime bounds report
│   ├── NumericLiteralReportTool.cs      # numeric_literal_report numeric literal report
│   ├── DeadBranchReportTool.cs          # dead_branch_report dead branch report
│   ├── EventSubscriptionReportTool.cs    # event_subscription_report event subscription report
│   ├── InheritanceShapeReportTool.cs     # inheritance_shape_report inheritance shape report
│   ├── NullCoalescingReportTool.cs       # null_coalescing_report null handling report
│   ├── EqualityContractReportTool.cs     # equality_contract_report equality contract report
│   ├── UsingDirectiveReportTool.cs       # using_directive_report using directive report
│   ├── DisposablePatternReportTool.cs    # disposable_pattern_report IDisposable report
│   ├── StringFormatReportTool.cs         # string_format_report string construction report
│   ├── EnumUsageReportTool.cs            # enum_usage_report enum usage report
│   ├── OperatorOverloadReportTool.cs     # operator_overload_report operator overload report
│   ├── DocCommentReportTool.cs           # doc_comment_report doc comment report
│   ├── PartialMethodReportTool.cs         # partial_method_report partial member report
│   ├── ObsoleteUsageReportTool.cs         # obsolete_usage_report Obsolete usage report
│   ├── ExtensionMethodReportTool.cs       # extension_method_report extension method report
│   ├── RecordMutabilityReportTool.cs      # record_mutability_report record mutability report
│   ├── NullableAnnotationReportTool.cs    # nullable_annotation_report nullable annotation report
│   ├── GitignoreQualityReportTool.cs      # gitignore_quality_report .gitignore quality report
│   ├── TodoCommentReportTool.cs           # todo_comment_report TODO comment report
│   ├── CsharpKeywordReportTool.cs         # csharp_keyword_report C# keyword report
│   ├── LargeMethodReportTool.cs           # large_method_report large method report
│   ├── DuplicateMethodReportTool.cs       # duplicate_method_report duplicate implementation report
│   ├── ConfigKeyUsageReportTool.cs        # config_key_usage_report config key usage report
│   ├── MagicNumberReportTool.cs           # magic_number_report magic number report
│   ├── GitattributesQualityReportTool.cs  # gitattributes_quality_report .gitattributes quality report
│   ├── LineEndingMixedReportTool.cs        # line_ending_mixed_report mixed line ending report
│   ├── StaleCommentReportTool.cs           # stale_comment_report stale comment report
│   ├── StringConcatPerfReportTool.cs       # string_concat_perf_report string concatenation performance report
│   ├── TestPlaceholderReportTool.cs        # test_placeholder_report test placeholder report
│   ├── PublicApiSurfaceReportTool.cs       # public_api_surface_report public API stability report
│   ├── OptionalParamDroppedReportTool.cs   # optional_param_dropped_report interface signature drift report
│   ├── SwitchExhaustivenessReportTool.cs   # switch_exhaustiveness_report switch exhaustiveness report
│   ├── LinqQueryReportTool.cs               # linq_query_report LINQ query trap report
│   ├── SwallowedExceptionReportTool.cs     # swallowed_exception_report swallowed exception report
│   ├── EqualsGetHashCodeReportTool.cs      # equals_gethashcode_report Equals/GetHashCode contract report
│   ├── DeferredQueryReportTool.cs         # deferred_query_report LINQ deferred execution report
│   ├── BoxingAllocationReportTool.cs      # boxing_allocation_report implicit allocation report
│   ├── TimeUnitConfusionReportTool.cs     # time_unit_confusion_report time unit confusion report
│   ├── CultureFormatReportTool.cs         # culture_format_report culture format report
│   ├── ConcurrentMutationReportTool.cs    # concurrent_mutation_report concurrent mutation report
│   ├── UndisposedStreamReportTool.cs      # undisposed_stream_report undisposed stream report
│   ├── NullAssignmentReportTool.cs        # null_assignment_report null assignment report
│   ├── BooleanParameterReportTool.cs      # boolean_parameter_report boolean parameter report
│   ├── HardcodedSecretReportTool.cs        # hardcoded_secret_report hardcoded secret report
│   ├── PathEscapeReportTool.cs             # path_escape_report path escape report
│   ├── DuplicateLogicReportTool.cs         # duplicate_logic_report duplicate logic report
│   ├── SilentCatchReportTool.cs            # silent_catch_report silent catch report
│   ├── InterfaceContractReportTool.cs      # interface_contract_report interface contract report
│   ├── TestabilityReportTool.cs            # testability_report testability report
│   ├── LockMisuseReportTool.cs             # lock_misuse_report lock misuse report
│   ├── DependencyInversionReportTool.cs    # dependency_inversion_report dependency inversion report
│   ├── OwnershipReportTool.cs              # ownership_report resource ownership report
│   ├── WeakAssertionReportTool.cs          # weak_assertion_report weak assertion report
│   ├── UntrustedInputReportTool.cs        # untrusted_input_report untrusted input report
│   ├── RetrySafetyReportTool.cs           # retry_safety_report retry safety report
│   ├── StateMachineReportTool.cs          # state_machine_report state machine report
│   ├── ObservabilityGapReportTool.cs      # observability_gap_report observability gap report
│   ├── TimeZoneReportTool.cs              # time_zone_report time zone report
│   ├── CollectionEdgeReportTool.cs        # collection_edge_report collection edge report
│   ├── ContractEvolutionReportTool.cs     # contract_evolution_report contract evolution report
│   ├── ConfigRobustnessReportTool.cs      # config_robustness_report config robustness report
│   ├── FloatPrecisionReportTool.cs         # float_precision_report float precision report
│   ├── EnumFlagReportTool.cs              # enum_flag_report enum/flag report
│   ├── AsyncMisuseReportTool.cs           # async_misuse_report async misuse report
│   ├── ApiVersionPinReportTool.cs         # api_version_pin_report dependency version report
│   ├── NestedTryReportTool.cs            # nested_try_report nested try report
│   ├── IteratorYieldReportTool.cs         # iterator_yield_report iterator yield report
│   ├── SwitchFallthroughReportTool.cs     # switch_fallthrough_report switch fallthrough report
│   ├── LinqDeferredReportTool.cs          # linq_deferred_report LINQ deferred execution report
│   ├── StringInterpolationReportTool.cs   # string_interpolation_report string concatenation report
│   ├── ArrayBoundsReportTool.cs           # array_bounds_report array bounds report
│   ├── ReadonlyStructReportTool.cs        # readonly_struct_report readonly struct report
│   ├── IntegerOverflowReportTool.cs       # integer_overflow_report integer overflow report
│   ├── DocCommentReportTool.cs           # doc_comment_report doc comment report
│   ├── PartialMethodReportTool.cs         # partial_method_report partial member report
│   ├── ObsoleteUsageReportTool.cs         # obsolete_usage_report Obsolete usage report
│   ├── ExtensionMethodReportTool.cs       # extension_method_report extension method report
│   ├── RecordMutabilityReportTool.cs      # record_mutability_report record mutability report
│   ├── NullableAnnotationReportTool.cs    # nullable_annotation_report nullable annotation report
│   ├── GitignoreQualityReportTool.cs      # gitignore_quality_report .gitignore quality report
│   ├── TodoCommentReportTool.cs           # todo_comment_report TODO comment report
│   ├── CsharpKeywordReportTool.cs         # csharp_keyword_report C# keyword report
│   ├── LargeMethodReportTool.cs           # large_method_report large method report
│   ├── DuplicateMethodReportTool.cs       # duplicate_method_report duplicate implementation report
│   ├── ConfigKeyUsageReportTool.cs        # config_key_usage_report config key usage report
│   ├── MagicNumberReportTool.cs           # magic_number_report magic number report
│   ├── GitattributesQualityReportTool.cs  # gitattributes_quality_report .gitattributes quality report
│   ├── LineEndingMixedReportTool.cs        # line_ending_mixed_report mixed line ending report
│   ├── StaleCommentReportTool.cs           # stale_comment_report stale comment report
│   ├── StringConcatPerfReportTool.cs       # string_concat_perf_report string concatenation performance report
│   ├── TestPlaceholderReportTool.cs        # test_placeholder_report test placeholder report
│   ├── PublicApiSurfaceReportTool.cs       # public_api_surface_report public API stability report
│   ├── OptionalParamDroppedReportTool.cs   # optional_param_dropped_report interface signature drift report
│   ├── SwitchExhaustivenessReportTool.cs   # switch_exhaustiveness_report switch exhaustiveness report
│   ├── LinqQueryReportTool.cs               # linq_query_report LINQ query trap report
│   ├── SwallowedExceptionReportTool.cs     # swallowed_exception_report swallowed exception report
│   ├── EqualsGetHashCodeReportTool.cs      # equals_gethashcode_report Equals/GetHashCode contract report
│   ├── DeferredQueryReportTool.cs         # deferred_query_report LINQ deferred execution report
│   ├── BoxingAllocationReportTool.cs      # boxing_allocation_report implicit allocation report
│   ├── TimeUnitConfusionReportTool.cs     # time_unit_confusion_report time unit confusion report
│   ├── CultureFormatReportTool.cs         # culture_format_report culture format report
│   ├── ConcurrentMutationReportTool.cs    # concurrent_mutation_report concurrent mutation report
│   ├── UndisposedStreamReportTool.cs      # undisposed_stream_report undisposed stream report
│   ├── NullAssignmentReportTool.cs        # null_assignment_report null assignment report
│   ├── BooleanParameterReportTool.cs      # boolean_parameter_report boolean parameter report
│   ├── HardcodedSecretReportTool.cs        # hardcoded_secret_report hardcoded secret report
│   ├── PathEscapeReportTool.cs             # path_escape_report path escape report
│   ├── DuplicateLogicReportTool.cs         # duplicate_logic_report duplicate logic report
│   ├── SilentCatchReportTool.cs            # silent_catch_report silent catch report
│   ├── InterfaceContractReportTool.cs      # interface_contract_report interface contract report
│   ├── TestabilityReportTool.cs            # testability_report testability report
│   ├── LockMisuseReportTool.cs             # lock_misuse_report lock misuse report
│   ├── DependencyInversionReportTool.cs    # dependency_inversion_report dependency inversion report
│   ├── OwnershipReportTool.cs              # ownership_report resource ownership report
│   ├── WeakAssertionReportTool.cs          # weak_assertion_report weak assertion report
│   ├── UntrustedInputReportTool.cs        # untrusted_input_report untrusted input report
│   ├── RetrySafetyReportTool.cs           # retry_safety_report retry safety report
│   ├── StateMachineReportTool.cs          # state_machine_report state machine report
│   ├── ObservabilityGapReportTool.cs      # observability_gap_report observability gap report
│   ├── TimeZoneReportTool.cs              # time_zone_report time zone report
│   ├── CollectionEdgeReportTool.cs        # collection_edge_report collection edge report
│   ├── ContractEvolutionReportTool.cs     # contract_evolution_report contract evolution report
│   ├── ConfigRobustnessReportTool.cs      # config_robustness_report config robustness report
│   ├── FloatPrecisionReportTool.cs         # float_precision_report float precision report
│   ├── EnumFlagReportTool.cs              # enum_flag_report enum/flag report
│   ├── AsyncMisuseReportTool.cs           # async_misuse_report async misuse report
│   ├── ApiVersionPinReportTool.cs         # api_version_pin_report dependency version report
│   ├── NestedTryReportTool.cs            # nested_try_report nested try report
│   ├── IteratorYieldReportTool.cs         # iterator_yield_report iterator yield report
│   ├── SwitchFallthroughReportTool.cs     # switch_fallthrough_report switch fallthrough report
│   ├── LinqDeferredReportTool.cs          # linq_deferred_report LINQ deferred execution report
│   ├── StringInterpolationReportTool.cs   # string_interpolation_report string concatenation report
│   ├── ArrayBoundsReportTool.cs           # array_bounds_report array bounds report
│   ├── ReadonlyStructReportTool.cs        # readonly_struct_report readonly struct report
│   ├── IntegerOverflowReportTool.cs       # integer_overflow_report integer overflow report
│   ├── TodoMarkerReportTool.cs        # todo_marker_report TODO marker report
│   ├── CiWorkflowReportTool.cs        # ci_workflow_report CI workflow report
│   ├── WorkflowHygieneReportTool.cs   # workflow_hygiene_report workflow hygiene report
│   ├── LicenseInventoryReportTool.cs  # license_inventory_report license inventory report
│   ├── DependencyLockfileReportTool.cs # dependency_lockfile_report dependency lockfile report
│   ├── NuGetReferenceReportTool.cs     # nuget_reference_report NuGet reference report
│   ├── JsonSchemaReportTool.cs         # json_schema_report JSON Schema inventory report
│   ├── SecretScanReportTool.cs         # secret_scan_report redacted secret scan
│   ├── EnvVarReferenceReportTool.cs    # env_var_reference_report environment variable report
│   ├── SensitiveFileInventoryTool.cs   # sensitive_file_inventory sensitive filename inventory
│   ├── GitignorePatternReportTool.cs   # gitignore_pattern_report gitignore pattern report
│   ├── GitignoreRuleReportTool.cs      # gitignore_rule_report gitignore dead-rule report
│   ├── GitattributesReportTool.cs      # gitattributes_report gitattributes rule report
│   ├── EditorConfigReportTool.cs        # editorconfig_report EditorConfig report
│   ├── GitHookInventoryTool.cs          # git_hook_inventory Git hooks inventory
│   ├── GitExecutableBitReportTool.cs    # git_executable_bit_report Git file mode report
│   ├── GitConfigReportTool.cs           # git_config_report Git config report
│   ├── GitLfsReportTool.cs              # git_lfs_report Git LFS status report
│   ├── GitGpgStatusReportTool.cs        # git_gpg_status_report Git signing status report
│   ├── GitIgnoredFilesTool.cs      # git_ignored_files Git ignored files
│   ├── GitPathDiffTool.cs          # git_path_diff Git path changes
│   ├── GitIndexDiagnosticsTool.cs  # git_index_diagnostics Git index diagnostics
│   ├── GitDiffTool.cs     # git_diff read-only Git diff
│   ├── GitLogTool.cs      # git_log read-only Git history
│   ├── GitBlameTool.cs    # git_blame read-only line provenance
│   ├── GitShowTool.cs     # git_show read-only commit details
│   ├── GitBranchesTool.cs # git_branches read-only branch list
│   ├── GitBranchAgeReportTool.cs # git_branch_age_report branch staleness report
│   ├── GitRemotesTool.cs  # git_remotes read-only remote URLs
│   ├── GitRemoteSyncReportTool.cs # git_remote_sync_report remote sync report
│   ├── GitTagsTool.cs     # git_tags read-only tag list
│   ├── GitReflogTool.cs   # git_reflog read-only movement history
│   ├── GitWorktreesTool.cs # git_worktrees read-only worktree list
│   ├── GitStashesTool.cs  # git_stashes read-only stash list
│   ├── FindLargeFilesTool.cs # find_large_files large-file scan
│   ├── FindEmptyDirectoriesTool.cs # find_empty_directories empty-directory scan
│   ├── FindSymlinksTool.cs       # find_symlinks symlink audit
│   ├── ChecksumManifestTool.cs   # checksum_manifest SHA256 manifest
│   ├── EncodingReportTool.cs     # encoding_report encoding report
│   ├── NormalizeLineEndingsTool.cs # normalize_line_endings line-ending normalization
│   ├── CreateDirectoryTool.cs   # create_directory safe directory creation
│   ├── RemoveEmptyDirectoryTool.cs # remove_empty_directory safe empty-directory removal
│   ├── FindStaleFilesTool.cs      # find_stale_files stale-file scan
│   ├── DirectorySizeReportTool.cs # directory_size_report directory size report
│   ├── FindUnreadableFilesTool.cs  # find_unreadable_files unreadable-file audit
│   ├── FindTemporaryFilesTool.cs   # find_temporary_files temporary-file scan
│   ├── BackupFileTool.cs           # backup_file safe file backup
│   ├── RestoreBackupTool.cs        # restore_backup safe backup restoration
│   ├── FindEmptyFilesTool.cs       # find_empty_files empty-file scan
│   ├── FindRecentFilesTool.cs      # find_recent_files recent-file scan
│   ├── FindFileNameConflictsTool.cs # find_filename_conflicts filename conflict audit
│   ├── FindLongPathsTool.cs         # find_long_paths long-path scan
│   ├── MoveFilesTool.cs    # move_files batch move
│   ├── DeleteFilesTool.cs  # delete_files batch safe delete
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

**Startup shows "unknown config key" warnings?** A key in `codeagent.json` is misspelled or no longer exists — the loader ignores it, so the setting never took effect. Fix the name (see [配置参考](docs/配置参考.md)); unknown keys inside a provider block are reported the same way.

**Where did my `/compact` summary go on Claude?** Nowhere — summaries inserted mid-conversation are converted to user-role blocks automatically (Anthropic has no mid-thread system role).

**A config field I set in an ADOFAI project got overwritten with injected text / my `CODEAGENT_PROVIDER` became the default?** Fixed: runtime injections and session-level overrides are no longer persisted by in-session saves (`/model` etc.). Older configs may still contain the baked-in text — remove it manually once.

## Further documentation (Chinese)

- [项目介绍](docs/项目介绍.md) — positioning, architecture, design trade-offs
- [配置参考](docs/配置参考.md) — every `codeagent.json` field explained
- [工作模式](docs/工作模式.md) — built-in and custom work modes
- [工具参考](docs/工具参考.md) — parameters and behavior of the 11 built-in tools
- [快捷键参考](docs/快捷键参考.md) — every input-line key
- [常见问题](docs/常见问题.md) — troubleshooting
- [开发指南](docs/开发指南.md) — building, testing, contributing

## License

MIT
