using System.Text.Json;
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

    /// <summary>单轮任务中最大工具调用轮数（防止死循环）。</summary>
    public int MaxToolIterations { get; set; } = 20;

    /// <summary>历史消息总字符上限，超过后从最旧处截断/裁剪。</summary>
    public int MaxHistoryChars { get; set; } = 160_000;

    /// <summary>是否允许 run_command 执行命令。</summary>
    public bool AllowCommands { get; set; } = true;

    /// <summary>执行命令前是否逐个询问确认。</summary>
    public bool ConfirmCommands { get; set; } = false;

    /// <summary>命令使用的 shell：cmd | powershell | bash；留空自动（Windows 用 cmd）。</summary>
    public string Shell { get; set; } = "";

    /// <summary>是否把每轮对话写入会话日志（.codeagent/sessions/*.jsonl）。</summary>
    public bool SaveSessions { get; set; } = true;

    /// <summary>会话日志目录（相对工作目录）。</summary>
    public string SessionDir { get; set; } = ".codeagent/sessions";

    /// <summary>是否流式输出模型回复（逐字打印，默认开启）。</summary>
    public bool StreamOutput { get; set; } = true;

    /// <summary>是否在终端实时显示工具调用过程（动作、耗时，默认开启）。</summary>
    public bool ShowToolCalls { get; set; } = true;

    /// <summary>是否对模型回复做 Markdown 渲染（代码块/行内代码/加粗/标题，默认开启）。</summary>
    public bool RenderMarkdown { get; set; } = true;

    /// <summary>是否用 ANSI 转义做菜单原地渲染（过滤在基础列表上原地更新、方向键高亮移动）；Windows Terminal 等支持 ANSI 的终端默认开启，老式终端设 false 用滚动式。</summary>
    public bool TuiAnsi { get; set; } = true;

    /// <summary>模型思考强度（reasoning effort）：off / low / medium / high，默认 off（不启用）。</summary>
    public string ThinkingEffort { get; set; } = "off";

    /// <summary>启动时的默认工作模式（/mode 可切换），默认 code。</summary>
    public string DefaultMode { get; set; } = "code";

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
