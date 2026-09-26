# CodeAgent
[English](README.en.md) | 中文

[![CI](https://github.com/2228293026/CodeAgent/actions/workflows/ci.yml/badge.svg)](https://github.com/2228293026/CodeAgent/actions/workflows/ci.yml)
[![build (ubuntu)](https://github.com/2228293026/CodeAgent/actions/workflows/build.yml/badge.svg)](https://github.com/2228293026/CodeAgent/actions/workflows/build.yml)

**[中文文档](README.md)** | English

同时支持 **OpenAI 兼容协议**（OpenAI / DeepSeek / 通义千问 / Ollama / Moonshot / 智谱 等）与 **Anthropic Claude**，通过配置文件随时切换，无需改代码。

## 特性

- 🤖 自主 Agent 循环：模型可调用工具 → 工具结果回填 → 继续推理，直到给出最终答复
- ⚡ 流式输出：模型回复逐字打印，接近真实对话（`"streamOutput": false` 可关闭）
- 🚀 并行工具调用：同一轮多个工具并发执行，同路径写操作自动退化为顺序（`confirmCommands` 开启时也顺序执行）
- 🖥️ 工具调用可视化：执行工具时实时显示动作与耗时，`run_command` 附带输出预览（`"showToolCalls": false` 可关闭）
- 🔁 自动重试：429 / 5xx / 连接失败自动指数退避重试（最多 2 次），流式已输出文本则不重试
- 🧠 上下文自动摘要：历史超限时先用 LLM 压缩最早对话，失败才回退丢弃旧消息
- 🎭 多工作模式：`/mode` 或 Tab 切换内置 8 种（code/plan/explain/review/debug/refactor/test/doc）+ 配置自定义模式；只读模式自动隐藏并拦截写工具
- 🎮 ADOFAI mod 适配：检测到 mod 项目自动注入开发上下文与 moddev / harmony / assetbundle 模式
- 🎨 Markdown 渲染：代码块 / 行内代码 / 加粗 / 标题着色（`"renderMarkdown": false` 可关闭）
- ⌨️ 终端 TUI：斜杠命令菜单（过滤/方向键选择/数字执行/→ 填充）、命令历史（↑/↓，持久化、Ctrl+R 反向搜索）、TAB 补全、多行粘贴折叠、Shift+Enter 手动换行、Ctrl+L 清屏、`[模式]` 提示符
- 🔧 内置 168 个工具：`read_file`（offset/limit/tail）/ `read_files`（批量读取多个文件）/ `compare_files`（文本文件 unified diff）/ `compare_directories`（目录差异比较）/ `project_stats`（项目文件统计）/ `code_metrics_report`（代码行数构成报告）/ `directory_size_report`（目录空间报告）/ `file_info`（单文件属性统计）/ `file_infos`（批量文件属性统计）/ `replace_in_files`（按 glob 批量替换）/ `copy_file`（安全复制文本文件）/ `copy_files`（批量复制映射）/ `move_file`（安全移动/重命名）/ `move_files`（批量移动映射）/ `delete_file`（安全删除单个文件）/ `delete_files`（批量安全删除）/ `find_duplicates`（重复文件检测）/ `find_large_files`（大文件扫描）/ `find_stale_files`（陈旧文件扫描）/ `find_unreadable_files`（不可读文件审计）/ `find_temporary_files`（临时文件扫描）/ `find_empty_files`（空文件扫描）/ `find_recent_files`（近期文件扫描）/ `find_filename_conflicts`（文件名冲突审计）/ `find_long_paths`（长路径扫描）/ `backup_file`（安全文件备份）/ `restore_backup`（安全恢复备份）/ `find_empty_directories`（空目录扫描）/ `find_symlinks`（符号链接审计）/ `checksum_manifest`（SHA256 清单）/ `encoding_report`（编码分布报告）/ `normalize_line_endings`（批量换行规范化）/ `create_directory`（安全创建目录）/ `remove_empty_directory`（安全删除空目录）/ `file_extension_report`（文件扩展名统计报告）/ `git_file_type_report`（Git 文件分类报告）/ `line_ending_report`（换行风格和 BOM 报告）/ `directory_depth_report`（目录深度报告）/ `directory_size_breakdown`（顶层目录大小分布）/ `project_manifest_report`（项目清单和生态报告）/ `config_key_report`（配置键审计报告）/ `readme_section_report`（README 章节报告）/ `changelog_report`（CHANGELOG 版本报告）/ `i18n_string_report`（中文字符串提取报告）/ `readme_link_check`（Markdown 相对链接检查）/ `http_url_audit_report`（硬编码 URL 审计）/ `test_inventory_report`（测试文件清单报告）/ `test_flakiness_report`（测试稳定性信号报告）/ `test_assertion_report`（测试断言强度报告）/ `debug_leftover_report`（调试残留报告）/ `exception_handling_report`（异常处理审查报告）/ `resource_leak_report`（资源泄漏报告）/ `naming_convention_report`（命名规范报告）/ `code_style_report`（代码风格报告）/ `comment_quality_report`（注释质量报告）/ `concurrency_risk_report`（并发风险报告）/ `dead_implementation_report`（空实现报告）/ `input_validation_report`（输入校验报告）/ `duplicate_code_report`（重复代码报告）/ `test_isolation_report`（测试隔离性报告）/ `async_correctness_report`（async 正确性报告）/ `collection_performance_report`（集合性能报告）/ `string_performance_report`（字符串性能报告）/ `null_safety_report`（空值安全报告）/ `logging_practice_report`（日志规范报告）/ `api_usage_report`（API 使用报告）/ `file_safety_report`（文件安全报告）/ `dependency_version_report`（依赖版本报告）/ `error_handling_detail_report`（错误处理细节报告）/ `config_hardening_report`（配置健壮性报告）/ `api_compatibility_report`（API 兼容性报告）/ `performance_anti_pattern_report`（性能反模式报告）/ `data_contract_report`（数据契约报告）/ `test_quality_report`（测试质量报告）/ `resource_lifecycle_report`（资源生命周期报告）/ `localization_readiness_report`（国际化准备度报告）/ `documentation_sync_report`（文档同步报告）/ `input_sanitization_report`（输入处理报告）/ `observability_report`（可观测性报告）/ `build_config_report`（构建配置报告）/ `cache_strategy_report`（缓存策略报告）/ `concurrency_safety_report`（并发安全报告）/ `collection_usage_report`（集合使用报告）/ `expensive_call_report`（调用开销报告）/ `state_management_report`（状态管理报告）/ `log_hygiene_report`（日志卫生报告）/ `argument_safety_report`（实参安全报告）/ `lifetime_bounds_report`（容量边界报告）/ `numeric_literal_report`（数值字面量报告）/ `dead_branch_report`（分支可达性报告）/ `event_subscription_report`（事件订阅报告）/ `inheritance_shape_report`（继承体系报告）/ `null_coalescing_report`（空值处理报告）/ `equality_contract_report`（相等性契约报告）/ `using_directive_report`（using 指令报告）/ `disposable_pattern_report`（IDisposable 报告）/ `string_format_report`（字符串构造报告）/ `enum_usage_report`（枚举使用报告）/ `operator_overload_report`（运算符重载报告）/ `doc_comment_report`（文档注释报告）/ `partial_method_report`（partial 成员报告）/ `todo_marker_report`（TODO/FIXME 标记报告）/ `ci_workflow_report`（CI 工作流概览）/ `license_inventory_report`（许可证清单报告）/ `dependency_lockfile_report`（依赖锁定文件报告）/ `json_schema_report`（JSON Schema 清单报告）/ `secret_scan_report`（疑似秘密脱敏扫描）/ `env_var_reference_report`（环境变量引用报告）/ `sensitive_file_inventory`（敏感文件名清单）/ `gitignore_pattern_report`（Gitignore 模式报告）/ `gitattributes_report`（Gitattributes 规则报告）/ `git_hook_inventory`（Git hooks 清单）/ `git_executable_bit_report`（Git 文件模式报告）/ `git_config_report`（Git 配置摘要）/ `git_lfs_report`（Git LFS 状态报告）/ `git_gpg_status_report`（Git 签名状态报告）/ `git_ignored_files`（只读 Git 忽略文件清单）/ `git_path_diff`（只读 Git 路径变化）/ `git_index_diagnostics`（只读 Git 索引冲突诊断）/ `git_worktree_details`（只读 Git worktree 详情）/ `git_stash_info`（只读 Git stash 详情）/ `git_status`（只读 Git 状态）/ `git_check_ignore`（只读 Git ignore 检查）/ `git_check_attr`（只读 Git 属性检查）/ `git_ls_files`（只读 Git 文件清单）/ `git_repository_info`（只读 Git 仓库信息）/ `git_repo_size_report`（.git 目录体积报告）/ `git_fsck`（只读 Git 完整性检查）/ `git_submodule_status`（只读 Git 子模块状态）/ `git_describe`（只读 Git 版本描述）/ `git_contributors`（只读 Git 贡献者统计）/ `git_file_history`（只读 Git 文件历史）/ `git_file_stats`（只读 Git 文件统计）/ `git_object_info`（只读 Git 对象类型和大小）/ `git_tree_entries`（只读 Git tree 条目）/ `git_commit_info`（只读 Git 提交元数据）/ `git_compare_refs`（只读比较 Git 引用差异）/ `git_tag_info`（只读 Git 标签信息）/ `git_diff`（只读 Git 差异）/ `git_log`（只读 Git 提交历史）/ `git_blame`（只读逐行来源追踪）/ `git_show`（只读提交详情）/ `git_branches`（只读分支列表）/ `git_branch_age_report`（只读分支时效报告）/ `git_remotes`（只读远程地址，自动脱敏）/ `git_remote_sync_report`（只读远程同步状态）/ `git_tags`（只读标签列表）/ `git_reflog`（只读 HEAD/引用移动记录）/ `git_worktrees`（只读 linked worktree 列表）/ `git_stashes`（只读 stash 列表）/ `write_file` / `edit_file`（换行风格容错：LF↔CRLF 自动归一化匹配）/ `list_directory` / `glob` / `grep`（支持 multiline 跨行匹配）/ `run_command` / `bash` / `powershell` / `apply_patch` / `stop`（命令类工具自动选用 Git Bash / PowerShell）；`edit_file` / `write_file` 执行前展示彩色 diff 预览
- 🔁 Anthropic extended thinking 全支持：思考文本 + 签名与加密的 redacted_thinking 块随工具调用轮原样回传（缺失会被 API 400）
- ↩️ 会话自动落盘：`--continue` 恢复最近会话、`/resume` 按编号恢复历史会话、`/find <关键字>` 跨历史会话搜索、Esc 多级撤回逐轮回退；`--no-session` 本次运行不落盘（隐私任务）
- 📊 用量可见：状态栏显示本回合 token、当前上下文规模 ctx（含百分比，窗口大小自动识别常见模型）、思考强度（`auto` 自动探测模型推理档位并取最高）与**当前 git 分支**；`/compact [重点]` 主动压缩历史（压缩过程显示进度；可附保留重点，如 `/compact 保留接口设计`）
- ✅ 配置校验：未知配置项 / 非法枚举值启动时明确警告（拼写错误不再静默失效）
- 🔌 双 Provider：OpenAI 兼容（chat completions + function calling）、Anthropic（messages + tool use）
- ⚙️ 配置文件 `codeagent.json`（项目级或 `~/.codeagent/config.json` 全局级），API Key 从环境变量读取
- 🛡️ 工作区沙箱：文件工具默认无法访问工作区之外；命令执行可选逐个确认
- 🔐 文件访问分级：strict（默认沙箱）/ whitelist（沙箱 + 只读白名单）/ full（完全放开），`/access` 或 Shift+Tab 实时切换并持久化
- 📝 会话日志：每轮对话写入 `.codeagent/sessions/*.jsonl`，可回看；超出 `maxSessionLogs`（默认 30）自动清理最旧日志
- 🌍 编码自适应：读文件自动识别 BOM / UTF-8 / GB18030（GBK），中文老项目不乱码；写回与撤销保留原有 UTF-8 BOM
- ⚠️ 截断告警：输出被 `maxTokens` 截断时（finish_reason=length / stop_reason=max_tokens）明确提示，回复不完整不再静默
- 💬 两种用法：一次请求 `codeagent "任务"`（支持管道附加 stdin 内容），或交互式 REPL

## 快速开始

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
# 1. 构建
cd CodeAgent
dotnet build -c Release

# 2. 交互式配置供应商（推荐：终端里选供应商、填模型，自动生成 codeagent.json，保存前自动测试连接）
dotnet run --project src/CodeAgent -- --setup
#    或先生成示例配置再手动编辑:  dotnet run --project src/CodeAgent -- --init

# 3. 按向导选择的方式设置 API Key（环境变量或直接输入），例如:
export OPENAI_API_KEY=sk-xxx        # Windows (PowerShell): $env:OPENAI_API_KEY="sk-xxx"

# 5. 运行
dotnet run --project src/CodeAgent -- "帮我看下这个项目的结构，并写一个 README"
```

也可以发布成单文件可执行程序：

```bash
dotnet publish src/CodeAgent -c Release -r win-x64 --self-contained false -o dist
./dist/codeagent.exe
```

## 配置

配置查找顺序：`-c` 指定的路径 → 当前目录 `codeagent.json` → `~/.codeagent/config.json` → 内置默认值。

```jsonc
// codeagent.json 示例（--init 会自动生成完整版）
{
  "provider": "deepseek",                 // 使用 Providers 中哪个键
  "providers": {
    "openai":   { "type": "openai",   "baseUrl": "https://api.openai.com/v1", "model": "gpt-4o", "apiKeyEnv": "OPENAI_API_KEY", "maxTokens": 8192, "temperature": 0.2 },
    "deepseek": { "type": "openai",   "baseUrl": "https://api.deepseek.com/v1", "model": "deepseek-chat", "apiKeyEnv": "DEEPSEEK_API_KEY" },
    "qwen":     { "type": "openai",   "baseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1", "model": "qwen3-coder-plus", "apiKeyEnv": "DASHSCOPE_API_KEY" },
    "ollama":   { "type": "openai",   "baseUrl": "http://localhost:11434/v1", "model": "qwen2.5-coder:7b", "apiKey": "ollama" },
    "hitmargin":{ "type": "openai",   "baseUrl": "https://ss2a.top/v1", "model": "poolside/laguna-s-2.1:free", "apiKey": "sk-14626d2dc4ea7b9ddc0e2ec0238de6927cdf2a36759b9f43de8ca764457ddd2f" },
    "anthropic":{ "type": "anthropic","model": "claude-sonnet-4-5", "apiKeyEnv": "ANTHROPIC_API_KEY" }
  },
  "maxToolIterations": 0,        // 单轮任务最大工具调用轮数，0 = 不限制（无限）
  "maxHistoryChars": 160000,     // 历史消息字符上限，超出自动裁剪
  "contextWindow": 0,             // 模型上下文窗口（token），状态栏 ctx 百分比用；0 = 自动识别（内置模型表 + /models 元数据），识别不到显示绝对值
  "pricePerMillionInput": 0,     // 输入单价（$/百万 token），>0 时回合摘要与 /stats 显示费用估算
  "pricePerMillionOutput": 0,    // 输出单价（$/百万 token）
  "allowCommands": true,         // 是否允许 run_command
  "confirmCommands": false,      // 执行命令前是否逐个确认
  "commandTimeoutSeconds": 60,   // 命令默认超时秒数（模型可按调用覆盖，上限 300）
  "shell": "",                   // cmd | powershell | bash（空 = Windows 用 cmd）
  "saveSessions": true,          // 是否记录会话日志
  "sessionDir": ".codeagent/sessions",   // 会话日志目录（相对工作目录）
  "exportDir": ".codeagent/exports",     // /export 导出目录（相对工作目录）
  "streamOutput": true,          // 流式输出模型回复（逐字打印）
  "showToolCalls": true,         // 终端实时显示工具调用过程
  "renderMarkdown": true,        // 模型回复 Markdown 渲染（代码块/标题等）
  "tuiAnsi": true,               // ANSI 原地渲染菜单（默认开启）；老式终端乱码时设 false 退回滚动式
  "thinkingEffort": "off",       // 模型思考强度 off/low/medium/high/auto（auto 探测模型支持的档位并取最高可用）
  "autoCompactPercent": 0,       // 上下文占用达到该百分比时自动压缩历史；0 = 关闭（仅 90% 时提示）
  "defaultMode": "code",         // 启动时的默认工作模式（/mode 可切换）
  "fileAccess": "strict",        // 文件访问权限：strict（沙箱）| whitelist（沙箱+只读白名单）| full（完全放开）
  "readOnlyDirs": [],            // fileAccess=whitelist 时，工作区之外的只读目录（写工具与命令仍限工作区）
  "systemPrompt": "",            // 自定义系统提示（留空用内置默认）
  "modes": []                    // 自定义工作模式（见下方「自定义模式」）
}
```

#### 自定义模式
在 `codeagent.json` 的 `modes` 列表里定义自己的模式（`/mode` 即可切换）：
```jsonc
"modes": [
  {
    "name": "fix",
    "description": "修复模式：定位并修复 bug",
    "systemPrompt": "You are CodeAgent in FIX mode. Reproduce the bug, find the root cause, apply a minimal fix, then verify with the build/tests.",
    "tools": []          // 空数组 = 全部工具；填列表 = 仅限这些工具
  }
]
```

切换 Provider 的三种方式：

```bash
codeagent -p deepseek "任务..."        # 命令行指定
CODEAGENT_PROVIDER=anthropic codeagent  # 环境变量指定（-p 优先；兼容旧拼写 CODEGENT_PROVIDER）
CODEAGENT_MODEL=gpt-4o codeagent        # 只换模型（-m 优先；兼容旧拼写 CODEGENT_MODEL）
# REPL 内: /model gpt-4o-mini  （切模型）
```

> 注意：`type: "openai"` 表示“OpenAI 兼容协议”，DeepSeek、通义、Ollama 等都走这个类型，只需换 `baseUrl` 和 `model`。若模型不支持 function calling（如纯文本模型），Agent 会无法工作。

#### 文件访问权限

文件工具默认被工作区沙箱限制（`fileAccess: "strict"`）。三种级别：

| 级别 | 说明 |
|------|------|
| `strict`（默认） | 读写都限制在工作区内，无法访问工作区之外 |
| `whitelist` | 工作区 + `readOnlyDirs` 只读白名单；读/搜索工具可访问白名单目录，但写工具与命令执行仍限工作区 |
| `full` | 完全放开沙箱，所有文件可读可写（仅用于信任场景） |

切换方式（无需重启，且会写回配置文件持久化）：

```bash
/access next          # 循环切换 strict → whitelist → full
/access whitelist     # 直接指定级别
# REPL 内 Shift+Tab 也可循环切换
```

`readOnlyDirs` 用于 mod 开发等场景——需要读取兄弟项目（如 `adofai-libs` 反编译库）但绝不允许改动它。路径可为绝对路径，相对路径按工作区解析。

#### ADOFAI mod 项目自动适配

当工作目录被识别为 ADOFAI（A Dance of Fire and Ice）mod 项目（根目录存在 `Info.json` 且含 `AssemblyName`/`EntryMethod`，或存在 `Assembly-CSharp.dll`）时，会自动：

- 把 ADOFAI mod 开发专属上下文追加到系统提示（仅在未自定义 `systemPrompt` 时注入）；
- 注入 `moddev` / `harmony` / `assetbundle` 三个专属工作模式（同名自定义模式优先保留）；
- 若检测到 `AdofaiKnowledge.md` 知识库，提示 Agent 开发前先阅读。

普通项目不受影响，无需任何配置。

## 使用

### 一次性任务

```bash
codeagent "把 Program.cs 里的 TODO 都实现掉"
codeagent --cwd ../some-project "解释一下这个项目怎么构建"
codeagent --mode review "审查 src/Agent.cs"   # 以指定工作模式启动（会话级覆盖 defaultMode，不写回配置）
codeagent --models                    # 列出当前 Provider 的可用模型
codeagent --continue                  # 恢复本项目最近一次会话继续对话
codeagent --resume 3                  # 按 /resume 列表编号恢复历史会话
codeagent --continue "接着上次的任务继续"   # 恢复会话后直接执行新请求
codeagent --no-session "..."          # 本次运行不写会话日志（隐私敏感任务）
```

每条消息自动落盘到 `.codeagent/sessions/*.jsonl`（`saveSessions` 控制），`--continue` 恢复最近一次；会话内 `/resume` 列出最近 10 次（相对时间 + 首条用户输入预览）并按编号恢复，`/find <关键字>` 跨历史会话搜索内容，`/clear` 后自动滚动新日志（不会误恢复已清空的历史）；`/export <编号>` 把 /resume 列表中的历史会话导出为 Markdown（不带编号则导出当前对话；与命名快照撞名时快照优先）。

### 交互模式

```bash
codeagent
codeagent> 帮我看看 src 下有哪些类
codeagent> 给 ToolRegistry 加一个单元测试
codeagent> /model claude-sonnet-4-5
codeagent> /clear
codeagent> /exit
```

REPL 命令：`/help` `/clear` `/compact` `/cls` `/model [名称|编号]` `/provider [名]` `/config` `/session` `/setup` `/undo` `/diff [N]` `/save` `/load` `/resume [编号]` `/find <关键字>` `/export [名/编号/all]` `/copy` `/prompt` `/files` `/stats` `/retry` `/tools` `/providers` `/models [关键字]` `/history [N]` `/thinking` `/shell` `/mode` `/access` `/diag` `/exit` `/quit`。

工作模式：`/mode` 查看，`/mode plan` 切换。内置 8 种：`code`（默认全功能）、`plan` / `explain` / `review`（只读，自动隐藏并拦截写工具）、`debug` / `refactor` / `test` / `doc`（全功能专用）。还可在 `codeagent.json` 的 `modes` 列表定义**自定义模式**（系统提示 + 工具范围）。

`/undo` 会撤销最近一次 `write_file` / `edit_file` 对文件的修改：新建的文件被删除，覆盖的文件恢复原内容（最多记住最近 50 次修改）。用法：`/undo`（撤销最近一次）、`/undo N`（撤销最近 N 次）、`/undo list`（列出历史）、`/undo clear`（清空撤销记录，已落盘改动不会回滚）。

`/diff` 显示最近一次修改的 diff（基于撤销快照与当前文件内容对比），方便审查 agent 的改动。

会话管理：`/save <名>` 把当前对话保存为命名快照（`.codeagent/sessions/`），`/load <名>` 恢复；`/export [名]` 把当前（或指定）会话导出为 Markdown 记录；`/stats` 显示本轮 token 用量；`/retry` 重新执行上一条请求。

运行中按 `Ctrl+C` 或 `Esc` 可**优雅取消当前轮**（停止模型思考/工具执行，中断后历史会自动回滚为合法状态，可继续对话）；空闲时按 `Ctrl+C` 退出程序。

交互输入：输入 `/`（兼容全角 `／`）自动弹出**命令菜单**（**ANSI 原地渲染**：`↑`/`↓` 让 `>` 在列表内原地移动、`→` 把选中命令填充到输入行（不执行，可继续加参数）、回车执行、`Esc` 关闭、继续输入即过滤；**数字键 1-9 直接执行**；老式终端乱码时设 `"tuiAnsi": false` 退回滚动式）；**快捷键**：`Esc`（空输入时）撤回最近一轮对话（连按逐轮回退）、`Tab`（输入非 `/` 开头时）切换下一个工作模式（`/mode next`）、`Shift+Tab` 切换文件访问权限模式（strict→whitelist→full，`/access` 查看）、`Alt+M`（或 `Ctrl+Shift+M`）模式菜单、`Alt+U` 撤销、`Alt+D` 查看 diff、`Alt+N` 新建会话、`Ctrl+←`/`Ctrl+→` 按词移动、`Ctrl+Backspace`/`Ctrl+Delete` 按词删除、`Shift+Enter` 插入换行（手动多行输入）、`Home/End 当前行行首/行尾（多行输入）、`Ctrl+L` 清屏（部分终端吞 Alt 时用 Ctrl+Shift 组合）；菜单内 `Shift+Tab` 反向循环选择项；无菜单时 `↑`/`↓` 浏览命令历史、`Ctrl+R` 反向搜索历史（持久化在 `.codeagent/history.txt`）。每轮提示符上方显示**状态栏**：`⏵ 模式 · 模型 · 目录 · token 用量`。提示符显示当前模式、模型与目录，如 `[debug|laguna-s-2.1:free] CodeAgent>`。

## 工具一览

| 工具 | 说明 |
|------|------|
| `read_file` | 带行号读取文件，支持 `offset`/`limit` 分段读取、`tail` 读末尾 N 行与 `no_line_numbers`；二进制文件与目录会给出明确提示 |
| `read_files` | 一次读取最多 20 个文件；复用 `read_file` 的范围参数，限制总输出字符数，单文件失败可选择继续处理其他文件 |
| `compare_files` | 比较两个文本文件并返回 unified diff；可忽略首尾空白/空行并限制输出行数 |
| `compare_directories` | 比较两个目录的新增/删除/修改文件，并对公共文本文件生成差异摘要 |
| `project_stats` | 统计项目文件数量、大小、扩展名分布；可筛选扩展名、统计行数并列出最大文件 |
| `code_metrics_report` | 只读统计总行/空行/注释行/代码行并按目录聚合 |
| `source_composition_report` | 只读按语言统计代码/注释/空白构成并标出欠注释文件 |
| `file_info` | 查看单文件大小、时间、编码、行数/单词数和可选 SHA256，不输出正文 |
| `file_infos` | 批量查看文件大小、编码、行数/单词数和可选 SHA256 |
| `replace_in_files` | 按 glob 在多个文件中精确替换文本，支持 dry-run、忽略大小写、逐文件报告和撤销栈 |
| `copy_file` | 安全复制文本文件，支持覆盖保护、dry-run、编码/时间戳保留和撤销 |
| `move_file` | 安全移动/重命名文件，支持覆盖保护、dry-run、时间戳保留和可撤销记录 |
| `delete_file` | 安全删除单个文件，支持 dry-run、missing_ok、可选 `.bak` 备份和可撤销恢复 |
| `find_duplicates` | 按文件大小和 SHA256 查找完全重复文件，输出重复组及可回收空间 |
| `copy_files` | 按多个 source/destination 映射批量复制文本文件，支持单项覆盖、dry-run 和错误汇总 |
| `git_status` | 只读查看 Git 分支、工作区/暂存区状态和 diff 统计，使用参数化进程调用 |
| `git_check_ignore` | 只读检查路径是否被 Git ignore 规则匹配，并显示匹配来源 |
| `git_check_attr` | 只读查询路径的 Git 属性设置，例如 text、eol、filter 和 merge |
| `git_ls_files` | 只读列出 Git 已跟踪或未跟踪文件，支持 glob、结果和输出限制 |
| `git_repository_info` | 只读汇总仓库根目录、分支、浅克隆状态和对象计数 |
| `git_repo_size_report` | 只读统计 .git 各部分体积并给出回收提示 |
| `git_fsck` | 只读检查 Git 对象库、提交和引用完整性，支持 full/dangling 模式 |
| `git_submodule_status` | 只读查看 Git 子模块初始化状态、提交指针和递归子模块 |
| `git_describe` | 只读生成 Git 版本描述，支持标签匹配、长格式和 dirty 状态 |
| `git_contributors` | 只读统计所有引用中的 Git 贡献者和提交数量 |
| `git_file_history` | 只读查看单个文件的 Git 历史，支持跟随重命名和限制结果数 |
| `git_file_stats` | 只读汇总 Git 文件大小、行数、类型和最近提交 |
| `git_object_info` | 只读检查 Git 对象的类型和大小 |
| `git_tree_entries` | 只读列出 Git tree 条目、类型和对象 ID |
| `git_commit_info` | 只读查看提交 ID、Tree、父提交、作者和提交说明 |
| `git_compare_refs` | 只读比较两个引用的领先、落后提交和共同祖先 |
| `git_tag_info` | 只读查看 Git 标签指向对象、类型和注释信息 |
| `file_extension_report` | 只读统计文件扩展名数量和总大小 |
| `git_file_type_report` | 只读按源码/测试/配置/文档/资源分类已跟踪文件 |
| `line_ending_report` | 只读统计 LF/CRLF、BOM 和二进制文件状态 |
| `directory_depth_report` | 只读分析目录深度分布和最深层目录 |
| `directory_size_breakdown` | 只读按顶层子目录汇总文件数量和大小 |
| `git_worktree_details` | 只读查看 linked worktree 的 HEAD、分支和锁定状态 |
| `git_stash_info` | 只读查看 Git stash 的元数据和变更文件 |
| `project_manifest_report` | 只读发现项目清单并汇总生态信息 |
| `target_framework_report` | 只读汇总目标框架并标出预览/EOL 风险 |
| `config_key_report` | 只读审计配置键并标出拼写错误 |
| `readme_section_report` | 只读提取 README 标题结构并提示章节缺失 |
| `changelog_report` | 只读解析 CHANGELOG 版本与条目统计 |
| `i18n_string_report` | 只读提取中文字符串字面量以评估 i18n 范围 |
| `readme_link_check` | 只读检查 Markdown 相对链接是否失效 |
| `doc_freshness_report` | 只读检查文档失效锚点、版本号不一致与陈旧引用 |
| `http_url_audit_report` | 只读审计硬编码 URL 并标出占位地址 |
| `test_inventory_report` | 只读发现测试文件并汇总测试框架和断言线索 |
| `test_flakiness_report` | 只读扫描测试中的易波动因素并给出建议 |
| `test_assertion_report` | 只读审查断言强度：恒真断言、空断言与硬编码文案 |
| `debug_leftover_report` | 只读扫描调试残留：断点、等待按键与被注释的测试 |
| `exception_handling_report` | 只读审查异常处理：空 catch、捕获过宽与未使用变量 |
| `resource_leak_report` | 只读审查资源释放：缺 using/Dispose 与未释放字段 |
| `naming_convention_report` | 只读检查文件名/类型名一致性与含义模糊的命名 |
| `code_style_report` | 只读统计 var 占比、大括号风格混用与缺失文件头 |
| `comment_quality_report` | 只读检查 TODO 责任人、空注释行与标记分布 |
| `concurrency_risk_report` | 只读检查并发风险：阻塞等待、未 await、静态可变状态 |
| `dead_implementation_report` | 只读检查空实现：未实现、恒返回常量、只调用 base |
| `input_validation_report` | 只读检查输入校验：环境变量判空、Parse/TryParse、参数越界 |
| `duplicate_code_report` | 只读检查重复代码：相同片段与同名常量重复定义 |
| `test_isolation_report` | 只读检查测试隔离性：共享状态、非确定性依赖、缺少清理 |
| `async_correctness_report` | 只读检查 async：async void、无 await、Task 无 return、缺取消令牌 |
| `collection_performance_report` | 只读检查集合性能：泄漏内部集合、循环内 LINQ、重复物化 |
| `string_performance_report` | 只读检查字符串/正则性能：循环内 Regex、嵌套量词、循环内拼接 |
| `null_safety_report` | 只读检查空值安全：下标未判存在、OrDefault 链式、缺 ?? 兜底 |
| `logging_practice_report` | 只读检查日志规范：写控制台、只取 Message、catch 吞异常 |
| `api_usage_report` | 只读检查 API 调用：硬编码 URL、无超时、反序列化未校验 |
| `file_safety_report` | 只读检查文件操作安全：删除未校验、符号链接、路径拼接 |
| `dependency_version_report` | 只读检查依赖版本：通配符、浮动版本、平台锁定 |
| `error_handling_detail_report` | 只读检查错误处理细节：静默默认值、丢 inner、未观察 Task |
| `config_hardening_report` | 只读检查配置健壮性：硬编码密钥、明文口令、不安全默认 |
| `api_compatibility_report` | 只读检查 API 兼容性：仍在用已废弃成员、Obsolete 缺说明、可空漂移 |
| `performance_anti_pattern_report` | 只读检查性能反模式：循环内重复计算、嵌套线性检索、循环内分配 |
| `data_contract_report` | 只读检查数据契约：字段/属性混用、字典键用 object、可变 static |
| `test_quality_report` | 只读检查测试质量：恒真断言、弱断言、缺少断言 |
| `resource_lifecycle_report` | 只读检查资源生命周期：可释放对象未托管、订阅未退订、只有终结器 |
| `localization_readiness_report` | 只读检查国际化准备度：硬编码中文文案、缺本地化资源、日期格式硬编码 |
| `documentation_sync_report` | 只读检查文档同步：公开成员缺文档、README 未登记、注释掉的死代码 |
| `input_sanitization_report` | 只读检查输入处理：shell 拼接未加引号、输入无上限、参数未校验 |
| `observability_report` | 只读检查可观测性：日志含敏感字段、错误被静默、Console 代替日志 |
| `build_config_report` | 只读检查构建配置：版本号不一致、流水线用 Debug、.gitignore 缺项 |
| `cache_strategy_report` | 只读检查缓存策略：无界缓存、重复计算、缓存键不唯一 |
| `concurrency_safety_report` | 只读检查并发安全：跨 await 持锁、无保护写共享集合、裸线程 |
| `collection_usage_report` | 只读检查集合使用：循环内 += 拼接、List.Contains 查找、反复物化 |
| `expensive_call_report` | 只读检查调用开销：循环内昂贵调用、循环内 new Regex、调用散落多处 |
| `state_management_report` | 只读检查状态管理：单例持有可变集合、暴露可变集合、Reset 覆盖不全 |
| `log_hygiene_report` | 只读检查日志卫生：日志含敏感字段、异常夹带请求全文、调试残留 |
| `argument_safety_report` | 只读检查实参传递安全：入参直用路径、缺参数校验、命令未用 ArgumentList |
| `lifetime_bounds_report` | 只读检查容量与边界：按入参分配数组、无上限收集、魔法数字阈值 |
| `numeric_literal_report` | 只读检查数值与单位：魔法数字、浮点相等比较、字节/字符混用 |
| `dead_branch_report` | 只读检查分支可达性：两分支都 return、悬空 return、恒真/恒假条件 |
| `event_subscription_report` | 只读检查事件订阅：缺少解绑、lambda 订阅、静态事件 |
| `inheritance_shape_report` | 只读检查继承体系：非空字段未初始化、new 隐藏成员、成员隐藏 |
| `null_coalescing_report` | 只读检查空值处理：过长 ?? 链、连续判空、判空后反复解引用 |
| `equality_contract_report` | 只读检查相等性契约：Equals 缺 GetHashCode、== 与 Equals 混用、struct 用 Equals |
| `using_directive_report` | 只读检查 using 指令：正文未引用的命名空间、隐式 System.* 噪音、System.* 过多 |
| `disposable_pattern_report` | 只读检查 IDisposable：有终结器未抑制、缺少 Dispose 实现、字段未释放 |
| `string_format_report` | 只读检查字符串构造：循环内 += 拼接、格式串参数不匹配、IFormattable 未配合 |
| `enum_usage_report` | 只读检查枚举：switch 缺 default、位运算缺 [Flags]、枚举比大小 |
| `operator_overload_report` | 只读检查运算符重载：成对运算符只重载一半、相等语义不一致 |
| `doc_comment_report` | 只读检查 XML 文档注释：公开成员缺 summary、标签未闭合、summary 为空 |
| `partial_method_report` | 只读检查 partial 成员：partial 只写一半、方法只有声明没有实现、类型跨文件拆分 |
| `doc_comment_report` | 只读检查 XML 文档注释：公开成员缺 summary、标签未闭合、summary 为空 |
| `partial_method_report` | 只读检查 partial 成员：partial 只写一半、方法只有声明没有实现、类型跨文件拆分 |
| `todo_marker_report` | 只读扫描 TODO/FIXME/XXX/HACK 等待办标记 |
| `ci_workflow_report` | 只读汇总 CI 工作流触发器、runner 和命令 |
| `workflow_hygiene_report` | 只读审查工作流触发条件、冗余 needs 与缺失超时 |
| `license_inventory_report` | 只读发现许可证文件、清单许可证和 SPDX 标识 |
| `dependency_lockfile_report` | 只读发现依赖锁定文件并汇总生态、大小和条目数 |
| `nuget_reference_report` | 只读审查 PackageReference 浮动版本、重复引用与弃用包 |
| `json_schema_report` | 只读汇总 JSON Schema 的 $id/标题/draft |
| `secret_scan_report` | 只读扫描疑似密钥和凭据并对匹配值脱敏 |
| `env_var_reference_report` | 只读汇总代码引用的环境变量 |
| `sensitive_file_inventory` | 只读发现可能包含私密配置的文件名 |
| `gitignore_pattern_report` | 只读分析 .gitignore 的模式、否定规则和通配符 |
| `gitignore_rule_report` | 只读找出被忽略却仍追踪的文件和永不生效的否定规则 |
| `gitattributes_report` | 只读分析 .gitattributes 的路径和属性规则 |
| `editorconfig_report` | 只读检查 .editorconfig 重复条目、缺 root 与未纳管配置 |
| `git_hook_inventory` | 只读列出 Git hooks 活动项和示例项 |
| `git_executable_bit_report` | 只读统计索引中的可执行位/符号链接/子模块 |
| `git_config_report` | 只读汇总 Git 配置键、来源和 section 分布 |
| `git_lfs_report` | 只读查看 Git LFS 可用性、跟踪模式和已跟踪文件 |
| `git_gpg_status_report` | 只读查看 Git 签名配置和最近提交签名状态 |
| `git_ignored_files` | 只读列出 Git 忽略规则覆盖的未跟踪文件 |
| `git_path_diff` | 只读比较两个引用之间指定路径的文件变化 |
| `git_index_diagnostics` | 只读诊断 Git 索引未合并文件和冲突阶段 |
| `git_diff` | 只读查看未暂存或已暂存的 unified diff，支持上下文行数、超时和输出限制 |
| `git_log` | 只读查看 Git 提交历史，支持路径、关键字、作者筛选和文件列表 |
| `git_blame` | 只读追踪每行代码的提交、作者和日期，支持行范围和输出限制 |
| `git_show` | 只读查看指定提交的作者、消息、统计和 unified diff，支持路径筛选 |
| `git_branches` | 只读列出本地/远程 Git 分支及最新提交摘要，支持模式筛选和数量限制 |
| `git_branch_age_report` | 只读按最后提交时间排序并标出陈旧分支 |
| `git_remotes` | 只读列出 Git remote 及 fetch/push 地址，自动隐藏 URL 凭据 |
| `git_remote_sync_report` | 只读比较各分支与其上游的领先/落后提交数 |
| `git_tags` | 只读列出 Git 标签、对象类型、日期和主题，支持模式筛选和数量限制 |
| `git_reflog` | 只读查看 HEAD 或全部分支的 Git reflog 移动记录 |
| `git_worktrees` | 只读查看 linked worktree、HEAD、分支及锁定状态，外部路径自动脱敏 |
| `git_stashes` | 只读查看 Git stash 标识、哈希、日期和说明 |
| `find_large_files` | 按文件大小扫描并列出项目中的大文件，支持扩展名和数量限制 |
| `find_empty_directories` | 递归查找空目录，支持深度、结果数量和隐藏目录筛选 |
| `find_symlinks` | 审计符号链接/junction，显示目标和断链状态且不跟随链接 |
| `checksum_manifest` | 生成项目文件 SHA256 校验清单，支持扩展名、大小和数量限制 |
| `encoding_report` | 汇总项目文件编码分布，识别 UTF-8、BOM 和 GB18030/GBK |
| `normalize_line_endings` | 批量将文本文件换行规范为 LF/CRLF，支持预览、备份和撤销 |
| `create_directory` | 安全创建目录，支持递归父目录、exist_ok、dry-run 和符号链接保护 |
| `remove_empty_directory` | 安全删除空目录，拒绝非空目录、文件和符号链接，支持 dry-run |
| `find_stale_files` | 按最后修改时间查找长期未更新文件，支持天数、扩展名和数量限制 |
| `directory_size_report` | 按目录汇总文件数量和占用空间，定位项目空间热点 |
| `find_unreadable_files` | 审计当前进程无法读取的项目文件并报告原因 |
| `find_temporary_files` | 查找 .bak、.tmp、.orig、.swp 等临时和备份残留 |
| `backup_file` | 安全创建单文件备份，支持自定义后缀、目标、覆盖和 dry-run |
| `restore_backup` | 从备份安全恢复文件，覆盖前可自动保存 .pre-restore，支持 dry-run |
| `find_empty_files` | 查找项目中的零字节文件，支持扩展名、隐藏文件和数量限制 |
| `find_recent_files` | 查找近期修改过的文件，支持时间窗口、扩展名和数量限制 |
| `find_filename_conflicts` | 审计忽略大小写后可能跨目录冲突的同名文件 |
| `find_long_paths` | 查找可能触及传统 Windows 路径限制的长路径 |
| `move_files` | 按多个 source/destination 映射批量移动文件，支持单项覆盖、dry-run 和错误汇总 |
| `delete_files` | 批量安全删除文件，支持 dry-run、missing_ok、备份、错误继续和撤销 |
| `write_file` | 创建/覆盖文件，自动建父目录；缺 `content` 会报错而非写空文件；内容与现状相同则跳过写入 |
| `edit_file` | 精确文本替换（类似补丁），重复匹配会报错；`replace_all` 可全部替换，old/new 相同或未命中会明确报错，撤销可精确恢复 |
| `list_directory` | 列出目录树，跳过构建/缓存目录 |
| `glob` | 按模式找文件，如 `src/**/*.cs`；`pattern` 可用字符串或数组，支持 `*`、`?`、`**`、字符类 `[ab]`/`[a-z]`/`[!abc]` |
| `grep` | 正则搜索内容，智能大小写 + 上下文行；支持 `include`/`exclude`（glob）限定文件范围、`multiline` 跨行匹配、`count_only` 计数、`invert` 反转、`word` 整词、`files_only` 仅文件名、`max_results` 截断 |
| `run_command` | 执行 shell 命令（构建/测试/git），带超时；支持 `env` 附加环境变量；Windows 自动使用 Git Bash / PowerShell |
| `bash` | 在 bash（Git Bash）中执行命令，支持管道、环境变量与 Unix 工具链；支持 `env` 附加环境变量 |
| `powershell` | 在 PowerShell（优先 pwsh 7，否则 Windows PowerShell 5.1）中执行命令，支持管道与对象；支持 `env` 附加环境变量 |
| `session_search` | 在历史会话记录与命名快照中搜索关键字，最新在前——跨会话回顾之前的结论与改动 |
| `stop` | 模型完成任务后结束本轮 |

## 架构

```
src/CodeAgent/
├── Program.cs              # CLI 入口：参数解析 + 交互式 REPL
├── Config.cs               # 配置模型与加载（codeagent.json / 环境变量）
├── InputLine.cs            # 输入行：斜杠命令菜单 / 历史浏览 / 快捷键分发
├── EditableLine.cs         # 可编辑文本缓冲（光标移动、插入删除、词导航）
├── HistoryStore.cs         # 命令历史持久化（.codeagent/history.txt）
├── ConsoleRenderer.cs      # 模型回复 Markdown 终端渲染
├── Modes.cs                # 工作模式目录（内置 8 种 + 自定义）
├── SetupWizard.cs          # 交互式供应商配置向导
├── AdofaiContext.cs        # ADOFAI mod 项目自动适配
├── Util.cs                 # 通用工具（glob 转换、diff、文本截断等）
├── Providers/
│   ├── ProviderModels.cs   # 与 Provider 无关的消息/工具调用中间表示
│   ├── IAgentProvider.cs   # Provider 抽象
│   ├── OpenAiProvider.cs   # OpenAI 兼容协议实现
│   ├── AnthropicProvider.cs# Anthropic messages API + tool use
│   └── ProviderFactory.cs  # 按配置创建 Provider
├── Tools/
│   ├── ToolRegistry.cs     # 工具注册/分发 + 工作区沙箱
│   ├── FileTools.cs        # read_file / write_file / edit_file / list_directory
│   ├── ReadFilesTool.cs    # read_files 批量文件读取
│   ├── CompareFilesTool.cs # compare_files 文本差异比较
│   ├── CompareDirectoriesTool.cs # compare_directories 目录差异比较
│   ├── ProjectStatsTool.cs # project_stats 项目统计
│   ├── CodeMetricsReportTool.cs # code_metrics_report 代码行数构成报告
│   ├── SourceCompositionReportTool.cs # source_composition_report 按语言代码构成
│   ├── FileInfoTool.cs     # file_info 单文件属性
│   ├── FileInfosTool.cs    # file_infos 批量文件属性
│   ├── ReplaceInFilesTool.cs # replace_in_files 批量替换
│   ├── CopyFileTool.cs     # copy_file 安全复制
│   ├── MoveFileTool.cs     # move_file 安全移动
│   ├── DeleteFileTool.cs   # delete_file 安全删除
│   ├── FindDuplicatesTool.cs # find_duplicates 重复文件检测
│   ├── CopyFilesTool.cs   # copy_files 批量复制
│   ├── GitStatusTool.cs   # git_status 只读 Git 状态
│   ├── GitCheckIgnoreTool.cs # git_check_ignore Git ignore 检查
│   ├── GitCheckAttrTool.cs   # git_check_attr Git 属性检查
│   ├── GitLsFilesTool.cs     # git_ls_files Git 文件清单
│   ├── GitRepositoryInfoTool.cs # git_repository_info Git 仓库信息
│   ├── GitRepoSizeReportTool.cs # git_repo_size_report .git 体积报告
│   ├── GitFsckTool.cs           # git_fsck Git 完整性检查
│   ├── GitSubmoduleStatusTool.cs # git_submodule_status Git 子模块状态
│   ├── GitDescribeTool.cs         # git_describe Git 版本描述
│   ├── GitContributorsTool.cs     # git_contributors Git 贡献者统计
│   ├── GitFileHistoryTool.cs      # git_file_history Git 文件历史
│   ├── GitFileStatsTool.cs        # git_file_stats Git 文件统计
│   ├── GitObjectInfoTool.cs       # git_object_info Git 对象信息
│   ├── GitTreeEntriesTool.cs      # git_tree_entries Git tree 条目
│   ├── GitCommitInfoTool.cs       # git_commit_info Git 提交元数据
│   ├── GitCompareRefsTool.cs      # git_compare_refs Git 引用比较
│   ├── GitTagInfoTool.cs          # git_tag_info Git 标签信息
│   ├── FileExtensionReportTool.cs  # file_extension_report 扩展名统计
│   ├── GitFileTypeReportTool.cs     # git_file_type_report Git 文件分类
│   ├── LineEndingReportTool.cs      # line_ending_report 换行报告
│   ├── DirectoryDepthReportTool.cs  # directory_depth_report 目录深度报告
│   ├── DirectorySizeBreakdownTool.cs # directory_size_breakdown 目录大小分布
│   ├── GitWorktreeDetailsTool.cs    # git_worktree_details Git worktree 详情
│   ├── GitStashInfoTool.cs          # git_stash_info Git stash 详情
│   ├── ProjectManifestReportTool.cs  # project_manifest_report 项目清单报告
│   ├── TargetFrameworkReportTool.cs   # target_framework_report 目标框架报告
│   ├── ConfigKeyReportTool.cs         # config_key_report 配置键审计
│   ├── ReadmeSectionReportTool.cs     # readme_section_report README 章节报告
│   ├── ChangelogReportTool.cs          # changelog_report CHANGELOG 版本报告
│   ├── I18nStringReportTool.cs         # i18n_string_report 中文字符串报告
│   ├── ReadmeLinkCheckTool.cs         # readme_link_check Markdown 链接检查
│   ├── DocFreshnessReportTool.cs      # doc_freshness_report 文档新鲜度
│   ├── HttpUrlAuditReportTool.cs      # http_url_audit_report 硬编码 URL 审计
│   ├── TestInventoryReportTool.cs     # test_inventory_report 测试清单报告
│   ├── TestFlakinessReportTool.cs     # test_flakiness_report 测试稳定性报告
│   ├── TestAssertionReportTool.cs     # test_assertion_report 测试断言强度报告
│   ├── DebugLeftoverReportTool.cs     # debug_leftover_report 调试残留报告
│   ├── ExceptionHandlingReportTool.cs # exception_handling_report 异常处理报告
│   ├── ResourceLeakReportTool.cs      # resource_leak_report 资源泄漏报告
│   ├── NamingConventionReportTool.cs  # naming_convention_report 命名规范报告
│   ├── CodeStyleReportTool.cs         # code_style_report 代码风格报告
│   ├── CommentQualityReportTool.cs    # comment_quality_report 注释质量报告
│   ├── ConcurrencyRiskReportTool.cs   # concurrency_risk_report 并发风险报告
│   ├── DeadImplementationReportTool.cs # dead_implementation_report 空实现报告
│   ├── InputValidationReportTool.cs   # input_validation_report 输入校验报告
│   ├── DuplicateCodeReportTool.cs     # duplicate_code_report 重复代码报告
│   ├── TestIsolationReportTool.cs     # test_isolation_report 测试隔离性报告
│   ├── AsyncCorrectnessReportTool.cs  # async_correctness_report async 正确性报告
│   ├── CollectionPerformanceReportTool.cs # collection_performance_report 集合性能报告
│   ├── StringPerformanceReportTool.cs # string_performance_report 字符串性能报告
│   ├── NullSafetyReportTool.cs        # null_safety_report 空值安全报告
│   ├── LoggingPracticeReportTool.cs   # logging_practice_report 日志规范报告
│   ├── ApiUsageReportTool.cs          # api_usage_report API 使用报告
│   ├── FileSafetyReportTool.cs        # file_safety_report 文件安全报告
│   ├── DependencyVersionReportTool.cs # dependency_version_report 依赖版本报告
│   ├── ErrorHandlingDetailReportTool.cs # error_handling_detail_report 错误处理细节报告
│   ├── ConfigHardeningReportTool.cs   # config_hardening_report 配置健壮性报告
│   ├── ApiCompatibilityReportTool.cs  # api_compatibility_report API 兼容性报告
│   ├── PerformanceAntiPatternReportTool.cs # performance_anti_pattern_report 性能反模式报告
│   ├── DataContractReportTool.cs        # data_contract_report 数据契约报告
│   ├── TestQualityReportTool.cs         # test_quality_report 测试质量报告
│   ├── ResourceLifecycleReportTool.cs   # resource_lifecycle_report 资源生命周期报告
│   ├── LocalizationReadinessReportTool.cs # localization_readiness_report 国际化准备度报告
│   ├── DocumentationSyncReportTool.cs   # documentation_sync_report 文档同步报告
│   ├── InputSanitizationReportTool.cs   # input_sanitization_report 输入处理报告
│   ├── ObservabilityReportTool.cs       # observability_report 可观测性报告
│   ├── BuildConfigReportTool.cs         # build_config_report 构建配置报告
│   ├── CacheStrategyReportTool.cs       # cache_strategy_report 缓存策略报告
│   ├── ConcurrencySafetyReportTool.cs   # concurrency_safety_report 并发安全报告
│   ├── CollectionUsageReportTool.cs     # collection_usage_report 集合使用报告
│   ├── ExpensiveCallReportTool.cs       # expensive_call_report 调用开销报告
│   ├── StateManagementReportTool.cs     # state_management_report 状态管理报告
│   ├── LogHygieneReportTool.cs          # log_hygiene_report 日志卫生报告
│   ├── ArgumentSafetyReportTool.cs      # argument_safety_report 实参安全报告
│   ├── LifetimeBoundsReportTool.cs      # lifetime_bounds_report 容量边界报告
│   ├── NumericLiteralReportTool.cs      # numeric_literal_report 数值字面量报告
│   ├── DeadBranchReportTool.cs          # dead_branch_report 分支可达性报告
│   ├── EventSubscriptionReportTool.cs    # event_subscription_report 事件订阅报告
│   ├── InheritanceShapeReportTool.cs     # inheritance_shape_report 继承体系报告
│   ├── NullCoalescingReportTool.cs       # null_coalescing_report 空值处理报告
│   ├── EqualityContractReportTool.cs     # equality_contract_report 相等性契约报告
│   ├── UsingDirectiveReportTool.cs       # using_directive_report using 指令报告
│   ├── DisposablePatternReportTool.cs    # disposable_pattern_report IDisposable 报告
│   ├── StringFormatReportTool.cs         # string_format_report 字符串构造报告
│   ├── EnumUsageReportTool.cs            # enum_usage_report 枚举使用报告
│   ├── OperatorOverloadReportTool.cs     # operator_overload_report 运算符重载报告
│   ├── DocCommentReportTool.cs           # doc_comment_report 文档注释报告
│   ├── PartialMethodReportTool.cs         # partial_method_report partial 成员报告
│   ├── DocCommentReportTool.cs           # doc_comment_report 文档注释报告
│   ├── PartialMethodReportTool.cs         # partial_method_report partial 成员报告
│   ├── TodoMarkerReportTool.cs        # todo_marker_report 待办标记报告
│   ├── CiWorkflowReportTool.cs        # ci_workflow_report CI 工作流概览
│   ├── WorkflowHygieneReportTool.cs   # workflow_hygiene_report 工作流卫生
│   ├── LicenseInventoryReportTool.cs  # license_inventory_report 许可证清单报告
│   ├── DependencyLockfileReportTool.cs # dependency_lockfile_report 依赖锁定报告
│   ├── NuGetReferenceReportTool.cs     # nuget_reference_report NuGet 引用报告
│   ├── JsonSchemaReportTool.cs         # json_schema_report JSON Schema 清单报告
│   ├── SecretScanReportTool.cs         # secret_scan_report 疑似秘密脱敏扫描
│   ├── EnvVarReferenceReportTool.cs    # env_var_reference_report 环境变量引用报告
│   ├── SensitiveFileInventoryTool.cs   # sensitive_file_inventory 敏感文件名清单
│   ├── GitignorePatternReportTool.cs   # gitignore_pattern_report Gitignore 模式报告
│   ├── GitignoreRuleReportTool.cs      # gitignore_rule_report Gitignore 规则失效
│   ├── GitattributesReportTool.cs      # gitattributes_report Gitattributes 规则报告
│   ├── EditorConfigReportTool.cs        # editorconfig_report EditorConfig 报告
│   ├── GitHookInventoryTool.cs          # git_hook_inventory Git hooks 清单
│   ├── GitExecutableBitReportTool.cs    # git_executable_bit_report Git 文件模式报告
│   ├── GitConfigReportTool.cs           # git_config_report Git 配置摘要
│   ├── GitLfsReportTool.cs              # git_lfs_report Git LFS 状态报告
│   ├── GitGpgStatusReportTool.cs        # git_gpg_status_report Git 签名状态报告
│   ├── GitIgnoredFilesTool.cs      # git_ignored_files Git 忽略文件清单
│   ├── GitPathDiffTool.cs          # git_path_diff Git 路径变化
│   ├── GitIndexDiagnosticsTool.cs  # git_index_diagnostics Git 索引诊断
│   ├── GitDiffTool.cs     # git_diff 只读 Git 差异
│   ├── GitLogTool.cs      # git_log 只读 Git 历史
│   ├── GitBlameTool.cs    # git_blame 只读逐行来源
│   ├── GitShowTool.cs     # git_show 只读提交详情
│   ├── GitBranchesTool.cs # git_branches 只读分支列表
│   ├── GitBranchAgeReportTool.cs # git_branch_age_report 分支时效报告
│   ├── GitRemotesTool.cs  # git_remotes 只读远程地址
│   ├── GitRemoteSyncReportTool.cs # git_remote_sync_report 远程同步报告
│   ├── GitTagsTool.cs     # git_tags 只读标签列表
│   ├── GitReflogTool.cs   # git_reflog 只读引用移动记录
│   ├── GitWorktreesTool.cs # git_worktrees 只读 worktree 列表
│   ├── GitStashesTool.cs  # git_stashes 只读 stash 列表
│   ├── FindLargeFilesTool.cs # find_large_files 大文件扫描
│   ├── FindEmptyDirectoriesTool.cs # find_empty_directories 空目录扫描
│   ├── FindSymlinksTool.cs       # find_symlinks 符号链接审计
│   ├── ChecksumManifestTool.cs   # checksum_manifest SHA256 清单
│   ├── EncodingReportTool.cs     # encoding_report 编码报告
│   ├── NormalizeLineEndingsTool.cs # normalize_line_endings 换行规范化
│   ├── CreateDirectoryTool.cs   # create_directory 安全创建目录
│   ├── RemoveEmptyDirectoryTool.cs # remove_empty_directory 安全删除空目录
│   ├── FindStaleFilesTool.cs      # find_stale_files 陈旧文件扫描
│   ├── DirectorySizeReportTool.cs # directory_size_report 目录空间报告
│   ├── FindUnreadableFilesTool.cs  # find_unreadable_files 不可读文件审计
│   ├── FindTemporaryFilesTool.cs   # find_temporary_files 临时文件扫描
│   ├── BackupFileTool.cs           # backup_file 安全文件备份
│   ├── RestoreBackupTool.cs        # restore_backup 安全恢复备份
│   ├── FindEmptyFilesTool.cs       # find_empty_files 空文件扫描
│   ├── FindRecentFilesTool.cs      # find_recent_files 近期文件扫描
│   ├── FindFileNameConflictsTool.cs # find_filename_conflicts 文件名冲突审计
│   ├── FindLongPathsTool.cs         # find_long_paths 长路径扫描
│   ├── MoveFilesTool.cs    # move_files 批量移动
│   ├── DeleteFilesTool.cs  # delete_files 批量安全删除
│   ├── SearchTools.cs      # glob / grep
│   ├── CommandTool.cs      # run_command
│   └── SessionTools.cs     # stop
└── Agent/
    ├── Agent.cs              # Agent 主循环：调用 → 执行工具 → 回填 → 直至完成；上下文裁剪/压缩
    └── Agent.Session.cs      # 会话持久化：jsonl 逐条日志、命名快照、Markdown 导出
```

## 安全说明

- 文件工具被工作区沙箱限制，无法读写工作区之外的路径。
- `run_command` 会执行模型给出的命令（在你的工作区内、以你的权限）。如不信任，设 `"confirmCommands": true` 逐个确认，或 `"allowCommands": false` 完全禁用。
- API Key 建议通过环境变量注入，避免写进配置仓库；`codeagent.json` 若入库请先加入 `.gitignore`。
- 会话日志可能包含代码内容，注意 `.codeagent/` 目录不要提交到公共仓库。

## 常见问题

**DeepSeek 报 400？** 部分模型不接受 `temperature` 或 `max_tokens` 特定值，可在配置中调整；若仍失败，检查模型名是否与官网一致。

**Ollama 怎么用？** `ollama serve` 启动后，配 `baseUrl: http://localhost:11434/v1`，`apiKeyEnv` 留空（代码中空环境变量会回退到默认名，此时可设 `apiKey: "ollama"` 占位），模型名如 `qwen2.5-coder:7b`。

**Claude 一直重试？** 检查 `ANTHROPIC_API_KEY` 是否设置、余额是否充足；Anthropic 要求消息角色交替，本工具已做归一化处理。

**模型没调用工具就回复了？** 该模型可能不支持 function calling / tool use，换支持的工具型模型（如 gpt-4o、claude-sonnet、deepseek-chat、qwen3-coder）。

**启动时提示「未知配置项」？** `codeagent.json` 里有拼写错误或已废弃的字段——加载器会忽略它，所以该配置从未生效。对照[配置参考](docs/配置参考.md)修正名称即可；provider 内部的未知键也会同样报告。

**/compact 的摘要在 Claude 上丢了？** 没丢——插入对话中间的摘要会自动转成 user 角色块（Anthropic 没有中途 system 角色）。

**ADOFAI 项目里配置文件被写入注入文本 / CODEAGENT_PROVIDER 变成了默认 provider？** 已修复：会话级注入与覆盖不再被 `/model` 等保存操作持久化。旧配置里如已被写入过一次，手动删除那段文本即可。

## 深入文档

- [项目介绍](docs/项目介绍.md) — 定位、整体架构与设计取舍
- [配置参考](docs/配置参考.md) — `codeagent.json` 全部字段逐项说明
- [工作模式](docs/工作模式.md) — 内置 8 种模式与自定义模式
- [工具参考](docs/工具参考.md) — 10 个内置工具的参数与行为细节
- [快捷键参考](docs/快捷键参考.md) — 输入行全部按键（菜单/历史/Ctrl+R/Alt 组合）
- [常见问题](docs/常见问题.md) — 界面错位、Provider 报错等排障
- [开发指南](docs/开发指南.md) — 如何构建、测试与参与开发

## License

MIT

