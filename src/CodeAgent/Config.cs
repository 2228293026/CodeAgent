using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodeAgent;

/// <summary>单个 Provider 的连接与模型选项。</summary>
public sealed class ProviderOptions
{
    /// <summary>Provider 类型：openai（兼容协议）| anthropic。</summary>
    public string Type { get; set; } = "openai";

    /// <summary>API 基础地址（留空则用内置默认）。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>模型名（留空则用内置默认）。</summary>
    public string Model { get; set; } = "";

    /// <summary>存放 API Key 的环境变量名。</summary>
    public string? ApiKeyEnv { get; set; }

    /// <summary>直接写 API Key（不推荐，仅测试用；优先于 ApiKeyEnv）。</summary>
    public string? ApiKey { get; set; }

    /// <summary>单次回复最大 token 数。</summary>
    public int MaxTokens { get; set; } = 8192;

    /// <summary>采样温度。</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>该供应商的输入单价（美元 / 百万 token）；0 = 未配置（费用估算回退全局 pricePerMillionInput）。</summary>
    public double PricePerMillionInput { get; set; } = 0;
    /// <summary>该供应商的输出单价（美元 / 百万 token）；0 = 未配置（回退全局 pricePerMillionOutput）。</summary>
    public double PricePerMillionOutput { get; set; } = 0;
}

/// <summary>自定义模式定义（codeagent.json 的 modes 列表项）。</summary>
public sealed class AgentModeConfig
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = AgentConfig.DefaultSystemPrompt;

    /// <summary>可用工具名列表；空/省略 = 全部工具。</summary>
    public List<string>? Tools { get; set; }
}

/// <summary>全局配置，对应 codeagent.json。</summary>
public sealed class AgentConfig
{
    /// <summary>当前使用的 Provider 名称（对应 Providers 字典的键）。</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>已配置的 Provider 集合，键为名称。</summary>
    public Dictionary<string, ProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>单轮任务中最大工具调用轮数；0 或负 = 不限制（无限）。</summary>
    public int MaxToolIterations { get; set; } = 0;

    /// <summary>历史消息总字符上限，超过后从最旧处截断/裁剪。</summary>
    public int MaxHistoryChars { get; set; } = 160_000;

    /// <summary>模型上下文窗口大小（token），用于状态栏 ctx 百分比显示；0 = 未知，只显示绝对值。
    /// 各模型不同（如 32k/128k/200k/1M），按所用模型填写。</summary>
    public int ContextWindow { get; set; } = 0;

    /// <summary>输入单价（美元/百万 token），用于成本估算；0 = 不显示费用。</summary>
    public double PricePerMillionInput { get; set; } = 0;

    /// <summary>输出单价（美元/百万 token）；0 = 不显示费用。</summary>
    public double PricePerMillionOutput { get; set; } = 0;

    /// <summary>是否允许 run_command 执行命令。</summary>
    public bool AllowCommands { get; set; } = true;

    /// <summary>执行命令前是否逐个询问确认。</summary>
    public bool ConfirmCommands { get; set; } = false;

    /// <summary>命令默认超时秒数（run_command / bash / powershell 未显式传 timeout_seconds 时用）。
    /// 模型可按调用覆盖（1-300）；全局上限 300。</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>命令使用的 shell：cmd | powershell | bash；留空自动（Windows 用 cmd）。</summary>
    public string Shell { get; set; } = "";

    /// <summary>是否把每轮对话写入会话日志（.codeagent/sessions/*.jsonl）。</summary>
    public bool SaveSessions { get; set; } = true;

    /// <summary>会话日志保留数量：.codeagent/sessions/*.jsonl 滚动新日志时删除最旧的超出部分（磁盘卫生）。
    /// 0 = 不清理（保留全部）。</summary>
    public int MaxSessionLogs { get; set; } = 30;

    /// <summary>会话日志目录（相对工作目录）。</summary>
    public string SessionDir { get; set; } = ".codeagent/sessions";

    /// <summary>Markdown 导出目录（相对工作目录，/export 用）。</summary>
    public string ExportDir { get; set; } = ".codeagent/exports";

    /// <summary>是否流式输出模型回复（逐字打印，默认开启）。</summary>
    public bool StreamOutput { get; set; } = true;

    /// <summary>是否在终端实时显示工具调用过程（动作、耗时，默认开启）。</summary>
    public bool ShowToolCalls { get; set; } = true;

    /// <summary>是否对模型回复做 Markdown 渲染（代码块/行内代码/加粗/标题，默认开启）。</summary>
    public bool RenderMarkdown { get; set; } = true;

    /// <summary>是否用 ANSI 转义做菜单原地渲染（过滤在基础列表上原地更新、方向键高亮移动）；Windows Terminal 等支持 ANSI 的终端默认开启，老式终端设 false 用滚动式。</summary>
    public bool TuiAnsi { get; set; } = true;

    /// <summary>模型思考强度（reasoning effort）：off / low / medium / high / auto，默认 off（不启用）。
    /// auto 不指定强度，由模型/供应商按自身默认行为思考。</summary>
    public string ThinkingEffort { get; set; } = "off";

    /// <summary>启动时的默认工作模式（/mode 可切换），默认 code。</summary>
    public string DefaultMode { get; set; } = "code";

    /// <summary>
    /// 文件访问权限模式：strict（默认，工作区沙箱，读写都限工作区）| whitelist（工作区 + ReadOnlyDirs 只读白名单）
    /// | full（所有文件可读可写，完全放开沙箱，仅用于信任场景）。
    /// </summary>
    public string FileAccess { get; set; } = "strict";

    /// <summary>
    /// 只读白名单目录（工作区之外）：fileAccess=whitelist 时，文件读/搜索工具可访问其中的文件，
    /// 但写工具（write_file / edit_file）与命令执行仍被限制在工作区内。用于 mod 开发等场景——
    /// 需要读取兄弟项目（如 adofai-libs 反编译库）但绝不允许改动它。路径可为绝对路径；相对路径按工作区解析。
    /// </summary>
    public List<string> ReadOnlyDirs { get; set; } = new();

    /// <summary>自定义工作模式列表（/mode 可切换，配合内置模式）。</summary>
    public List<AgentModeConfig> Modes { get; set; } = new();

    /// <summary>系统提示词。</summary>
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    public const string DefaultSystemPrompt = """
        你是 CodeAgent，一名资深软件工程师助手，在用户的项目工作区内工作。你务实、精确、诚实，用中文（或与用户一致的语言）回复。

        工作方式：
        1. 先探索再动手：编辑前用 list_directory / glob / grep 了解结构并阅读相关文件，绝不猜测文件内容。
        2. 做合适大小的改动：小改动用 edit_file，新文件或整体重写用 write_file；改完代码后用项目的构建/检查/测试验证（如 run_command 执行 dotnet build），并如实报告结果。
        3. 尊重用户意图：任务确实含糊时先问清楚再动手；不做超出要求的范围蔓延。
        4. 注意安全：破坏性操作（强制删除、reset、覆盖）必须先征得同意；遵守 allowCommands / confirmCommands 配置。
        5. 诚实报告：说明做了什么、验证证据（构建/测试输出）与失败之处；没有验证过就绝不声称成功。
        6. 简洁回复：用「做了什么 → 结果 → 下一步」的结构，避免无意义的前缀。
        7. 任务完成或需要提问时调用 stop 工具结束本轮。
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 不转义非 ASCII：中文原样输出（System.Text.Json 默认会把中文转成 \uXXXX，破坏配置可读性）。
        // 仅影响序列化（Save/WriteExample），不影响反序列化（Load）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 加载配置：显式路径 → 当前目录 codeagent.json → ~/.codeagent/config.json → 内置默认。
    /// 反序列化只覆盖 JSON 中出现的字段，其余字段保留默认值。
    /// </summary>
    public static AgentConfig Load(string? explicitPath)
    {
        string? found = null;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException($"配置文件不存在: {explicitPath}");
            found = explicitPath;
        }
        else
        {
            var local = Path.Combine(Environment.CurrentDirectory, "codeagent.json");
            if (File.Exists(local))
                found = local;
            else
            {
                var home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codeagent", "config.json");
                if (File.Exists(home))
                    found = home;
            }
        }

        if (found is null)
            return new AgentConfig();

        try
        {
            var text = File.ReadAllText(found);
            var cfg = JsonSerializer.Deserialize<AgentConfig>(text, JsonOpts) ?? new AgentConfig();
            cfg.SourceFile = found;
            // 边界校验：非法值收敛到可用范围，避免空转/异常。
            // 上限防止误配超大值：MaxToolIterations 过大导致超长循环烧 token（0 或负 = 不限制），
            // MaxHistoryChars 过大导致历史永不裁剪、请求体无限膨胀（OOM）。
            cfg.MaxToolIterations = Math.Clamp(cfg.MaxToolIterations, 0, 200);
            cfg.MaxHistoryChars = Math.Clamp(cfg.MaxHistoryChars, 1_000, 20_000_000);
            cfg.ContextWindow = Math.Clamp(cfg.ContextWindow, 0, 10_000_000);
            cfg.CommandTimeoutSeconds = Math.Clamp(cfg.CommandTimeoutSeconds, 1, 300);
            cfg.MaxSessionLogs = Math.Clamp(cfg.MaxSessionLogs, 0, 1000);
            // 字符串枚举归一化：手写配置的大小写/空白差异曾让同一值在不同 Provider 上行为分叉
            //（如 "High" 在 OpenAI 侧静默不发送 reasoning_effort、在 Anthropic 侧却按默认预算开启 thinking）
            var effortRaw = cfg.ThinkingEffort;
            cfg.ThinkingEffort = NormalizeChoice(cfg.ThinkingEffort, "off", "low", "medium", "high", "auto");
            if (!string.IsNullOrWhiteSpace(effortRaw) && cfg.ThinkingEffort != effortRaw.Trim().ToLowerInvariant())
                cfg.Warnings.Add($"thinkingEffort='{effortRaw}' 不是有效档位（off/low/medium/high/auto），已回退为 '{cfg.ThinkingEffort}'。");
            var accessRaw = cfg.FileAccess;
            cfg.FileAccess = NormalizeChoice(cfg.FileAccess, "strict", "whitelist", "full");
            if (!string.IsNullOrWhiteSpace(accessRaw) && cfg.FileAccess != accessRaw.Trim().ToLowerInvariant())
                cfg.Warnings.Add($"fileAccess='{accessRaw}' 不是有效级别（strict/whitelist/full），已回退为更严格的 '{cfg.FileAccess}'。");
            cfg.Warnings.AddRange(ValidateUnknownKeys(text));
            return cfg;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"配置文件解析失败: {found}\n{ex.Message}");
        }
    }

    /// <summary>配置来源文件（用于日志展示），不参与序列化。</summary>
    [JsonIgnore]
    public string? SourceFile { get; private set; }

    /// <summary>仅会话内生效的系统提示（ADOFAI 注入等运行时增强）：
    /// Agent 的 code 模式优先用它而非 SystemPrompt。不参与序列化——
    /// /model、/thinking、/access 等命令会把整个 config 写回配置文件，
    /// 直接改 SystemPrompt 会把注入的长上下文永久写进用户的 codeagent.json。</summary>
    [JsonIgnore]
    public string? SessionOnlySystemPrompt { get; set; }

    /// <summary>启动时配置文件里持久化的 provider 名（环境变量/命令行覆盖前）。
    /// /model、/thinking、/shell、/access 等命令保存配置时按它写回 provider 字段，
    /// 避免把 CODEAGENT_PROVIDER=xxx 之类的会话级覆盖固化成配置文件的默认值。
    /// 显式 /provider 切换会同步更新它（用户明确选择应持久化）。null = 无需还原。</summary>
    [JsonIgnore]
    public string? PersistedProvider { get; set; }

    /// <summary>加载时的非致命警告（未知配置项、非法枚举回退等），由入口打印提示。
    /// 手写 JSON 的拼写错误此前会被静默忽略——「配了但不生效」且无任何线索。</summary>
    [JsonIgnore]
    public List<string> Warnings { get; } = new();

    /// <summary>配置字符串枚举归一化：去空白 + 小写；不在候选集内回退第一个候选（默认值）。
    /// FileAccess 的默认是更严格的 strict——误拼不放开沙箱。</summary>
    private static string NormalizeChoice(string? value, params string[] allowed)
    {
        var v = value?.Trim().ToLowerInvariant();
        return allowed.Contains(v) ? v! : allowed[0];
    }

    // 已知配置键（反序列化大小写不敏感，这里同样按忽略大小写比较，避免误报）
    private static readonly HashSet<string> KnownTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "provider", "providers", "maxToolIterations", "maxHistoryChars", "contextWindow",
        "pricePerMillionInput", "pricePerMillionOutput", "allowCommands", "confirmCommands",
        "commandTimeoutSeconds", "shell", "saveSessions", "maxSessionLogs", "sessionDir",
        "exportDir", "streamOutput", "showToolCalls", "renderMarkdown", "tuiAnsi",
        "thinkingEffort", "defaultMode", "fileAccess", "readOnlyDirs", "modes", "systemPrompt",
    };
    private static readonly HashSet<string> KnownProviderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "baseUrl", "model", "apiKeyEnv", "apiKey",
        "maxTokens", "temperature", "pricePerMillionInput", "pricePerMillionOutput",
    };
    private static readonly HashSet<string> KnownModeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "systemPrompt", "tools",
    };

    /// <summary>校验配置文件键名：未知顶层/供应商/自定义模式字段多半是拼写错误，
    /// 反序列化会静默丢弃——「配了但不生效」且无任何线索。返回人类可读警告。</summary>
    internal static IReadOnlyList<string> ValidateUnknownKeys(string json)
    {
        var warnings = new List<string>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch
        {
            return warnings; // 解析失败交给反序列化统一报错，这里不重复
        }
        if (root is not JsonObject obj)
            return warnings;

        foreach (var key in obj.Select(kv => kv.Key))
            if (!KnownTopLevelKeys.Contains(key))
                warnings.Add($"未知配置项 '{key}'（可能是拼写错误，该配置不会生效）。");

        if (obj["providers"] is JsonObject providers)
            foreach (var (name, pnode) in providers)
                if (pnode is JsonObject pobj)
                    foreach (var key in pobj.Select(kv => kv.Key))
                        if (!KnownProviderKeys.Contains(key))
                            warnings.Add($"Provider '{name}' 存在未知配置项 '{key}'（可能是拼写错误，该配置不会生效）。");

        if (obj["modes"] is JsonArray modes)
            for (int i = 0; i < modes.Count; i++)
                if (modes[i] is JsonObject mobj)
                    foreach (var key in mobj.Select(kv => kv.Key))
                        if (!KnownModeKeys.Contains(key))
                            warnings.Add($"自定义模式[{i}] 存在未知配置项 '{key}'（可能是拼写错误，该配置不会生效）。");

        return warnings;
    }

    /// <summary>以 camelCase 格式保存配置到指定路径。</summary>
    public static void Save(AgentConfig config, string path)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(path, json);
    }

    /// <summary>写出示例配置到指定路径（--init 用）。</summary>
    public static void WriteExample(string path)
    {
        var example = new AgentConfig
        {
            Provider = "openai",
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new() { Type = "openai", Model = "gpt-4o", ApiKeyEnv = "OPENAI_API_KEY" },
                ["deepseek"] = new() { Type = "openai", BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-chat", ApiKeyEnv = "DEEPSEEK_API_KEY" },
                ["qwen"] = new() { Type = "openai", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1", Model = "qwen3-coder-plus", ApiKeyEnv = "DASHSCOPE_API_KEY" },
                ["ollama"] = new() { Type = "openai", BaseUrl = "http://localhost:11434/v1", Model = "qwen2.5-coder:7b", ApiKey = "ollama" },
                ["anthropic"] = new() { Type = "anthropic", Model = "claude-sonnet-4-5", ApiKeyEnv = "ANTHROPIC_API_KEY" },
            },
        };
        Save(example, path);
    }
}
