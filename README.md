# CodeAgent

一个用 C#（.NET 10）编写的 LLM 驱动编码助手 CLI。它像 Claude Code / Aider 一样，在终端里理解你的任务，自主地**读取文件、搜索代码、修改代码、执行命令**，直到完成任务。

同时支持 **OpenAI 兼容协议**（OpenAI / DeepSeek / 通义千问 / Ollama / Moonshot / 智谱 等）与 **Anthropic Claude**，通过配置文件随时切换，无需改代码。

## 特性

- 🤖 自主 Agent 循环：模型可调用工具 → 工具结果回填 → 继续推理，直到给出最终答复
- ⚡ 流式输出：模型回复逐字打印，接近真实对话（`"streamOutput": false` 可关闭）
- 🚀 并行工具调用：同一轮多个工具并发执行，同路径写操作自动退化为顺序（`confirmCommands` 开启时也顺序执行）
- 🖥️ 工具调用可视化：执行工具时实时显示动作与耗时，`run_command` 附带输出预览（`"showToolCalls": false` 可关闭）
- 🔁 自动重试：429 / 5xx / 连接失败自动指数退避重试（最多 2 次），流式已输出文本则不重试
- 🧠 上下文自动摘要：历史超限时先用 LLM 压缩最早对话，失败才回退丢弃旧消息
- 🎭 多工作模式：`/mode` 切换 code / plan / explain / review，只读模式自动隐藏并拦截写工具
- 🎨 Markdown 渲染：代码块 / 行内代码 / 加粗 / 标题着色（`"renderMarkdown": false` 可关闭）
- 🔧 内置 8 个工具：`read_file` / `write_file` / `edit_file` / `list_directory` / `glob` / `grep` / `run_command` / `stop`
- 🔌 双 Provider：OpenAI 兼容（chat completions + function calling）、Anthropic（messages + tool use）
- ⚙️ 配置文件 `codeagent.json`（项目级或 `~/.codeagent/config.json` 全局级），API Key 从环境变量读取
- 🛡️ 工作区沙箱：文件工具无法访问工作区之外；命令执行可选逐个确认
- 📝 会话日志：每轮对话写入 `.codeagent/sessions/*.jsonl`，可回看
- 💬 两种用法：一次请求 `codeagent "任务"`，或交互式 REPL

## 快速开始

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
# 1. 构建
cd CodeAgent
dotnet build -c Release

# 2. 交互式配置供应商（推荐：终端里选供应商、填模型，自动生成 codeagent.json）
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
    "openai":   { "type": "openai",   "baseUrl": "https://api.openai.com/v1", "model": "gpt-4o", "apiKeyEnv": "OPENAI_API_KEY" },
    "deepseek": { "type": "openai",   "baseUrl": "https://api.deepseek.com/v1", "model": "deepseek-chat", "apiKeyEnv": "DEEPSEEK_API_KEY" },
    "qwen":     { "type": "openai",   "baseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1", "model": "qwen3-coder-plus", "apiKeyEnv": "DASHSCOPE_API_KEY" },
    "ollama":   { "type": "openai",   "baseUrl": "http://localhost:11434/v1", "model": "qwen2.5-coder:7b", "apiKey": "ollama" },
    "hitmargin":{ "type": "openai",   "baseUrl": "https://api.hitmargin.workers.dev/v1", "model": "poolside/laguna-s-2.1:free", "apiKey": "dummy" },
    "anthropic":{ "type": "anthropic","model": "claude-sonnet-4-5", "apiKeyEnv": "ANTHROPIC_API_KEY" }
  },
  "maxToolIterations": 20,       // 单轮任务最大工具调用轮数
  "maxHistoryChars": 160000,     // 历史消息字符上限，超出自动裁剪
  "allowCommands": true,         // 是否允许 run_command
  "confirmCommands": false,      // 执行命令前是否逐个确认
  "shell": "",                   // cmd | powershell | bash（空 = Windows 用 cmd）
  "saveSessions": true           // 是否记录会话日志
  "streamOutput": true           // 流式输出模型回复（逐字打印）
  "showToolCalls": true          // 终端实时显示工具调用过程
  "renderMarkdown": true         // 模型回复 Markdown 渲染（代码块/标题等）
}
```

切换 Provider 的三种方式：

```bash
codeagent -p deepseek "任务..."
CODEGENT_PROVIDER=anthropic codeagent
# REPL 内: /model gpt-4o-mini  （切模型）
```

> 注意：`type: "openai"` 表示“OpenAI 兼容协议”，DeepSeek、通义、Ollama 等都走这个类型，只需换 `baseUrl` 和 `model`。若模型不支持 function calling（如纯文本模型），Agent 会无法工作。

## 使用

### 一次性任务

```bash
codeagent "把 Program.cs 里的 TODO 都实现掉"
codeagent --cwd ../some-project "解释一下这个项目怎么构建"
```

### 交互模式

```bash
codeagent
codeagent> 帮我看看 src 下有哪些类
codeagent> 给 ToolRegistry 加一个单元测试
codeagent> /model claude-sonnet-4-5
codeagent> /clear
codeagent> /exit
```

REPL 命令：`/help` `/clear` `/model [名称]` `/config` `/session` `/setup` `/undo` `/diff` `/save` `/load` `/export` `/stats` `/retry` `/tools` `/providers` `/mode` `/exit`。

工作模式：`/mode` 查看，`/mode plan` 切换。`plan` / `explain` / `review` 为只读模式（只能读取/搜索，自动隐藏并拦截写工具与命令），`code` 为默认全功能模式。

`/undo` 会撤销最近一次 `write_file` / `edit_file` 对文件的修改：新建的文件被删除，覆盖的文件恢复原内容（最多记住最近 50 次修改）。

`/diff` 显示最近一次修改的 diff（基于撤销快照与当前文件内容对比），方便审查 agent 的改动。

会话管理：`/save <名>` 把当前对话保存为命名快照（`.codeagent/sessions/`），`/load <名>` 恢复；`/export [名]` 把当前（或指定）会话导出为 Markdown 记录；`/stats` 显示本轮 token 用量；`/retry` 重新执行上一条请求。

运行中按 `Ctrl+C` 或 `Esc` 可**优雅取消当前轮**（中断后历史会自动回滚为合法状态，可继续对话）；空闲时按 `Ctrl+C` 退出程序。

## 工具一览

| 工具 | 说明 |
|------|------|
| `read_file` | 带行号读取文件，支持 `offset`/`limit` 分段读取 |
| `write_file` | 创建/覆盖文件，自动建父目录 |
| `edit_file` | 精确文本替换（类似补丁），重复匹配会报错 |
| `list_directory` | 列出目录树，跳过构建/缓存目录 |
| `glob` | 按模式找文件，如 `src/**/*.cs` |
| `grep` | 正则搜索内容，智能大小写 + 上下文行 |
| `run_command` | 执行 shell 命令（构建/测试/git），带超时 |
| `stop` | 模型完成任务后结束本轮 |

## 架构

```
src/CodeAgent/
├── Program.cs              # CLI 入口：参数解析 + 交互式 REPL
├── Config.cs               # 配置模型与加载（codeagent.json / 环境变量）
├── Util.cs                 # 通用工具（glob 转换、二进制检测等）
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
    └── Agent.cs            # Agent 主循环：调用 → 执行工具 → 回填 → 直至完成
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

## License

MIT
