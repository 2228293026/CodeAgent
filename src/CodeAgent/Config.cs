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

    /// <summary>系统提示词。</summary>
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    public const string DefaultSystemPrompt = """
        You are CodeAgent, an autonomous coding assistant working inside the user's project.
        Rules you must follow:
        1. Understand the task before acting: explore with list_directory/glob/grep before reading whole files. Never guess file contents — read first, then edit.
        2. Use edit_file for small targeted changes; use write_file for new files or full rewrites. After changing code, run the project's build/check/tests (e.g. `dotnet build`) with run_command to verify.
        3. Prefer precise searches (grep/glob) over dumping large files; use read_file offsets to read only what you need.
        4. All paths are relative to the workspace root. Do not read or write files outside the workspace.
        5. Never claim success without evidence; report verification results honestly, including failures.
        6. Keep your replies concise: state what you did and the results.
        7. Call the `stop` tool when the task is finished or you need to ask the user something.
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
