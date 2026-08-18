# CodeAgent

[![CI](https://github.com/2228293026/CodeAgent/actions/workflows/ci.yml/badge.svg)](https://github.com/2228293026/CodeAgent/actions/workflows/ci.yml)
[![build (ubuntu)](https://github.com/2228293026/CodeAgent/actions/workflows/build.yml/badge.svg)](https://github.com/2228293026/CodeAgent/actions/workflows/build.yml)

一个用 C#（.NET 10）编写的 LLM 驱动编码助手 CLI。它像 Claude Code / Aider 一样，在终端里理解你的任务，自主地**读取文件、搜索代码、修改代码、执行命令**，直到完成任务。

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
- 🔧 内置 10 个工具：`read_file` / `write_file` / `edit_file` / `list_directory` / `glob` / `grep` / `run_command` / `bash` / `powershell` / `stop`（命令类工具自动选用 Git Bash / PowerShell）；`edit_file` / `write_file` 执行前展示彩色 diff 预览
- ↩️ 会话自动落盘：`--continue` 恢复最近会话、`/resume` 按编号恢复历史会话、Esc 多级撤回逐轮回退
- 📊 用量可见：状态栏显示本回合 token、当前上下文规模 ctx（含百分比，窗口大小自动识别常见模型）与思考强度（`auto` 自动探测模型推理档位并取最高）；`/compact` 主动压缩历史（压缩过程显示进度）
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
    "hitmargin":{ "type": "openai",   "baseUrl": "https://api.hitmargin.workers.dev/v1", "model": "poolside/laguna-s-2.1:free", "apiKey": "dummy" },
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
CODEGENT_PROVIDER=anthropic codeagent  # 环境变量指定（-p 优先）
CODEGENT_MODEL=gpt-4o codeagent        # 只换模型（-m 优先）
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
codeagent --models                    # 列出当前 Provider 的可用模型
codeagent --continue                  # 恢复本项目最近一次会话继续对话
codeagent --continue "接着上次的任务继续"   # 恢复会话后直接执行新请求
```

每条消息自动落盘到 `.codeagent/sessions/*.jsonl`（`saveSessions` 控制），`--continue` 恢复最近一次；会话内 `/resume` 列出最近 10 次并按编号恢复，`/clear` 后自动滚动新日志（不会误恢复已清空的历史）；`/export <编号>` 把 /resume 列表中的历史会话导出为 Markdown（不带编号则导出当前对话）。

### 交互模式

```bash
codeagent
codeagent> 帮我看看 src 下有哪些类
codeagent> 给 ToolRegistry 加一个单元测试
codeagent> /model claude-sonnet-4-5
codeagent> /clear
codeagent> /exit
```

REPL 命令：`/help` `/clear` `/compact` `/cls` `/model [名称|编号]` `/provider [名]` `/config` `/session` `/setup` `/undo` `/diff` `/save` `/load` `/resume [编号]` `/export` `/copy` `/stats` `/retry` `/tools` `/providers` `/models [关键字]` `/history` `/thinking` `/mode` `/access` `/diag` `/exit` `/quit`。

工作模式：`/mode` 查看，`/mode plan` 切换。内置 8 种：`code`（默认全功能）、`plan` / `explain` / `review`（只读，自动隐藏并拦截写工具）、`debug` / `refactor` / `test` / `doc`（全功能专用）。还可在 `codeagent.json` 的 `modes` 列表定义**自定义模式**（系统提示 + 工具范围）。

`/undo` 会撤销最近一次 `write_file` / `edit_file` 对文件的修改：新建的文件被删除，覆盖的文件恢复原内容（最多记住最近 50 次修改）。

`/diff` 显示最近一次修改的 diff（基于撤销快照与当前文件内容对比），方便审查 agent 的改动。

会话管理：`/save <名>` 把当前对话保存为命名快照（`.codeagent/sessions/`），`/load <名>` 恢复；`/export [名]` 把当前（或指定）会话导出为 Markdown 记录；`/stats` 显示本轮 token 用量；`/retry` 重新执行上一条请求。

运行中按 `Ctrl+C` 或 `Esc` 可**优雅取消当前轮**（停止模型思考/工具执行，中断后历史会自动回滚为合法状态，可继续对话）；空闲时按 `Ctrl+C` 退出程序。

交互输入：输入 `/`（兼容全角 `／`）自动弹出**命令菜单**（**ANSI 原地渲染**：`↑`/`↓` 让 `>` 在列表内原地移动、`→` 把选中命令填充到输入行（不执行，可继续加参数）、回车执行、`Esc` 关闭、继续输入即过滤；**数字键 1-9 直接执行**；老式终端乱码时设 `"tuiAnsi": false` 退回滚动式）；**快捷键**：`Esc`（空输入时）撤回最近一轮对话（连按逐轮回退）、`Tab`（输入非 `/` 开头时）切换下一个工作模式（`/mode next`）、`Shift+Tab` 切换文件访问权限模式（strict→whitelist→full，`/access` 查看）、`Alt+M`（或 `Ctrl+Shift+M`）模式菜单、`Alt+U` 撤销、`Alt+D` 查看 diff、`Alt+N` 新建会话、`Ctrl+←`/`Ctrl+→` 按词移动、`Ctrl+Backspace`/`Ctrl+Delete` 按词删除、`Shift+Enter` 插入换行（手动多行输入）、`Ctrl+L` 清屏（部分终端吞 Alt 时用 Ctrl+Shift 组合）；菜单内 `Shift+Tab` 反向循环选择项；无菜单时 `↑`/`↓` 浏览命令历史、`Ctrl+R` 反向搜索历史（持久化在 `.codeagent/history.txt`）。每轮提示符上方显示**状态栏**：`⏵ 模式 · 模型 · 目录 · token 用量`。提示符显示当前模式、模型与目录，如 `[debug|laguna-s-2.1:free] CodeAgent>`。

## 工具一览

| 工具 | 说明 |
|------|------|
| `read_file` | 带行号读取文件，支持 `offset`/`limit` 分段读取与 `no_line_numbers`；二进制文件与目录会给出明确提示 |
| `write_file` | 创建/覆盖文件，自动建父目录；缺 `content` 会报错而非写空文件；内容与现状相同则跳过写入 |
| `edit_file` | 精确文本替换（类似补丁），重复匹配会报错；`replace_all` 可全部替换，old/new 相同或未命中会明确报错，撤销可精确恢复 |
| `list_directory` | 列出目录树，跳过构建/缓存目录 |
| `glob` | 按模式找文件，如 `src/**/*.cs`；`pattern` 可用字符串或数组，支持 `*`、`?`、`**`、字符类 `[ab]`/`[a-z]`/`[!abc]` |
| `grep` | 正则搜索内容，智能大小写 + 上下文行；支持 `include`/`exclude`（glob）限定文件范围 |
| `run_command` | 执行 shell 命令（构建/测试/git），带超时；支持 `env` 附加环境变量；Windows 自动使用 Git Bash / PowerShell |
| `bash` | 在 bash（Git Bash）中执行命令，支持管道、环境变量与 Unix 工具链；支持 `env` 附加环境变量 |
| `powershell` | 在 PowerShell（优先 pwsh 7，否则 Windows PowerShell 5.1）中执行命令，支持管道与对象；支持 `env` 附加环境变量 |
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

