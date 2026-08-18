using System;
using System.IO;
using System.Linq;
using CodeAgent;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using static CodeAgent.Program;

namespace CodeAgent.Tests;

/// <summary>Config / ProviderOptions 的默认值与 Load/Save 边界测试（补充 ConfigTests 未覆盖的边界）。</summary>
public class ConfigEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-cfgedge-" + Guid.NewGuid().ToString("N"));

    public ConfigEdgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private string WriteJson(string json)
    {
        var path = Path.Combine(_dir, "cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    // ===== ProviderOptions 默认值 =====

    [Fact]
    public void Load_NormalizesStringEnumCaseAndWhitespace()
    {
        // 回归：手写配置 "High"/" FULL " 曾原样传给 Provider——OpenAI 侧静默不发送
        // reasoning_effort、Anthropic 侧却按默认预算开启 thinking，同一配置两种行为
        var path = WriteJson("""{"thinkingEffort":" High ","fileAccess":" FULL "}""");
        var c = AgentConfig.Load(path);
        Assert.Equal("high", c.ThinkingEffort);
        Assert.Equal("full", c.FileAccess);
    }

    [Fact]
    public void Load_InvalidEnumFallsBackToSafeDefault()
    {
        // 误拼值回退默认：fileAccess 应回退到更严格的 strict（不放开沙箱）
        var path = WriteJson("""{"thinkingEffort":"banana","fileAccess":"fll"}""");
        var c = AgentConfig.Load(path);
        Assert.Equal("off", c.ThinkingEffort);
        Assert.Equal("strict", c.FileAccess);
    }
    [Fact]
    public void ProviderOptions_HasSaneDefaults()
    {
        var p = new ProviderOptions();
        Assert.Equal("openai", p.Type);
        Assert.Equal("", p.BaseUrl);
        Assert.Equal("", p.Model);
        Assert.Null(p.ApiKeyEnv);
        Assert.Null(p.ApiKey);
        Assert.Equal(8192, p.MaxTokens);
        Assert.Equal(0.2, p.Temperature);
    }

    // ===== AgentConfig 默认值 =====

    [Fact]
    public void AgentConfig_HasSaneDefaults()
    {
        var c = new AgentConfig();
        Assert.Equal("openai", c.Provider);
        Assert.Equal(0, c.MaxToolIterations);      // 0 = 不限制
        Assert.Equal(160_000, c.MaxHistoryChars);
        Assert.True(c.AllowCommands);
        Assert.False(c.ConfirmCommands);
        Assert.True(c.SaveSessions);
        Assert.True(c.StreamOutput);
        Assert.True(c.ShowToolCalls);
        Assert.True(c.RenderMarkdown);
        Assert.Equal("off", c.ThinkingEffort);
        Assert.Equal("code", c.DefaultMode);
        Assert.Equal("strict", c.FileAccess);       // 默认严格沙箱
        Assert.Empty(c.ReadOnlyDirs);
        Assert.Empty(c.Modes);
        Assert.Null(c.SourceFile);
        Assert.Equal(AgentConfig.DefaultSystemPrompt, c.SystemPrompt);
    }

    [Fact]
    public void DefaultSystemPrompt_IsNotBlank() =>
        Assert.False(string.IsNullOrWhiteSpace(AgentConfig.DefaultSystemPrompt));

    // ===== Load 错误路径 =====

    [Fact]
    public void Load_ExplicitPathMissing_ThrowsFileNotFound()
    {
        var missing = Path.Combine(_dir, "nope.json");
        Assert.Throws<FileNotFoundException>(() => AgentConfig.Load(missing));
    }

    [Fact]
    public void Load_InvalidJson_ThrowsInvalidData()
    {
        var path = WriteJson("{ not valid json !!!");
        Assert.Throws<InvalidDataException>(() => AgentConfig.Load(path));
    }

    [Fact]
    public void Load_EmptyFile_ThrowsInvalidData()
    {
        var path = WriteJson("");
        Assert.Throws<InvalidDataException>(() => AgentConfig.Load(path));
    }

    // ===== Load 容错 =====

    [Fact]
    public void Load_UnknownFields_AreIgnored()
    {
        var path = WriteJson("""{ "provider": "x", "noSuchField": 123, "another": [1,2] }""");
        var cfg = AgentConfig.Load(path);
        Assert.Equal("x", cfg.Provider);
    }

    [Fact]
    public void Load_PartialJson_KeepsDefaults()
    {
        var path = WriteJson("""{ "provider": "qwen" }""");
        var cfg = AgentConfig.Load(path);
        Assert.Equal("qwen", cfg.Provider);
        Assert.Equal(160_000, cfg.MaxHistoryChars); // 未写字段保持默认
        Assert.Equal("strict", cfg.FileAccess);
    }

    [Fact]
    public void Load_PropertyNameIsCaseInsensitive()
    {
        var path = WriteJson("""{ "PROVIDER": "deepseek", "DefaultMode": "plan" }""");
        var cfg = AgentConfig.Load(path);
        Assert.Equal("deepseek", cfg.Provider);
        Assert.Equal("plan", cfg.DefaultMode);
    }

    [Fact]
    public void Load_ToleratesCommentsAndTrailingComma()
    {
        var path = WriteJson("""
        { // 注释
        "provider": "ollama",
        "modes": [ { "name": "fix", }, ],
        }
        """);
        var cfg = AgentConfig.Load(path);
        Assert.Equal("ollama", cfg.Provider);
        Assert.Single(cfg.Modes);
        Assert.Equal("fix", cfg.Modes[0].Name);
    }

    [Fact]
    public void Load_SetsSourceFile()
    {
        var path = WriteJson("""{ "provider": "x" }""");
        var cfg = AgentConfig.Load(path);
        Assert.Equal(Path.GetFullPath(path), cfg.SourceFile);
    }

    // ===== Load 钳制 =====

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(200, 200)]
    [InlineData(201, 200)]
    [InlineData(9999, 200)]
    public void Load_ClampsMaxToolIterations(int input, int expected)
    {
        var path = WriteJson($"{{\"maxToolIterations\": {input}}}");
        Assert.Equal(expected, AgentConfig.Load(path).MaxToolIterations);
    }

    [Theory]
    [InlineData(999, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(20_000_000, 20_000_000)]
    [InlineData(20_000_001, 20_000_000)]
    public void Load_ClampsMaxHistoryChars(int input, int expected)
    {
        var path = WriteJson($"{{\"maxHistoryChars\": {input}}}");
        Assert.Equal(expected, AgentConfig.Load(path).MaxHistoryChars);
    }

    [Fact]
    public void ContextWindow_DefaultsToZero()
    {
        Assert.Equal(0, new AgentConfig().ContextWindow); // 0 = 未知，仅显示绝对值
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    [InlineData(999, 300)]
    [InlineData(-3, 1)]
    public void Load_ClampsCommandTimeoutSeconds(int input, int expected)
    {
        var path = WriteJson($"{{\"commandTimeoutSeconds\": {input}}}");
        Assert.Equal(expected, AgentConfig.Load(path).CommandTimeoutSeconds);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(131072, 131072)]
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(10_000_001, 10_000_000)]
    public void Load_ClampsContextWindow(int input, int expected)
    {
        var path = WriteJson($"{{\"contextWindow\": {input}}}");
        Assert.Equal(expected, AgentConfig.Load(path).ContextWindow);
    }

    // ===== ConfigSavePath（/model //thinking //setup 的写回路径）=====

    [Fact]
    public void ConfigSavePath_ExplicitFlagWins()
    {
        var cfg = AgentConfig.Load(WriteJson("""{"provider":"x"}""")); // SourceFile 指向该文件
        Assert.Equal("-c.json", ConfigSavePath("-c.json", cfg));
    }

    [Fact]
    public void ConfigSavePath_FallsBackToLoadedSourceFile()
    {
        // 无 -c 时写回实际加载的来源文件（可能是 ~/.codeagent/config.json），
        // 而不是 cwd 新建 codeagent.json 把配置一分为二
        var path = WriteJson("""{"provider":"x"}""");
        var cfg = AgentConfig.Load(path);
        Assert.Equal(path, ConfigSavePath(null, cfg));
    }

    [Fact]
    public void ConfigSavePath_NoSource_DefaultsToLocalFile()
    {
        var cfg = new AgentConfig(); // 内置默认，无来源文件
        Assert.Equal("codeagent.json", ConfigSavePath(null, cfg));
    }

    // ===== KnownContextWindows（状态栏 ctx 百分比的自动识别表）=====

    [Theory]
    [InlineData("gpt-4o", 128_000)]
    [InlineData("gpt-4o-2024-08-13", 128_000)]        // 带日期后缀仍命中
    [InlineData("gpt-4.1-mini", 1_000_000)]           // 最长前缀优先（4.1 而非 4o）
    [InlineData("openai/gpt-4.1", 1_000_000)]         // 去厂商前缀
    [InlineData("deepseek-chat", 128_000)]
    [InlineData("deepseek/deepseek-reasoner", 128_000)]
    [InlineData("qwen3-coder-plus", 262_144)]
    [InlineData("claude-sonnet-4-5", 200_000)]
    [InlineData("claude-sonnet-5", 1_000_000)]      // 5 系 Sonnet 1M 窗口
    [InlineData("claude-opus-5", 200_000)]
    [InlineData("claude-haiku-4-5-20251001", 200_000)]
    [InlineData("gpt-5.1", 400_000)]
    [InlineData("anthropic/claude-opus-4-5", 200_000)]
    [InlineData("gemini-2.5-flash", 1_000_000)]
    [InlineData("o1", 200_000)]
    [InlineData("o3-mini", 200_000)]
    [InlineData("deepseek-r1", 128_000)]            // OpenRouter 命名（官方叫 deepseek-reasoner）
    [InlineData("x-ai/grok-4", 256_000)]            // 去厂商前缀 + 新条目
    [InlineData("grok-3-mini", 131_072)]            // 前缀命中 grok-3
    public void KnownContextWindows_RecognizesCommonModels(string model, int expected) =>
        Assert.Equal(expected, KnownContextWindows.TryGet(model));

    [Theory]
    [InlineData("hy3:free")]   // OpenRouter 后缀剥离后未知
    [InlineData("hy3")]
    [InlineData("my-private-model")]
    [InlineData("")]
    [InlineData(null)]
    public void KnownContextWindows_UnknownReturnsNull(string? model) =>
        Assert.Null(KnownContextWindows.TryGet(model));

    // ===== Save 往返 =====

    [Fact]
    public void Save_RoundTripsFullConfig()
    {
        var path = Path.Combine(_dir, "full.json");
        var cfg = new AgentConfig
        {
            Provider = "hitmargin",
            Providers = new(System.StringComparer.OrdinalIgnoreCase)
            {
                ["hitmargin"] = new ProviderOptions { Type = "openai", Model = "tencent/hy3:free", ApiKey = "dummy", MaxTokens = 4096, Temperature = 0.1 },
            },
            MaxToolIterations = 42,
            MaxHistoryChars = 50_000,
            ContextWindow = 131_072,
            AllowCommands = false,
            ConfirmCommands = true,
            Shell = "bash",
            SaveSessions = false,
            StreamOutput = false,
            ShowToolCalls = false,
            RenderMarkdown = false,
            ThinkingEffort = "high",
            DefaultMode = "moddev",
            FileAccess = "whitelist",
            ReadOnlyDirs = ["D:/x", "D:/y"],
            Modes = [new AgentModeConfig { Name = "fix", Description = "d", SystemPrompt = "p", Tools = ["read_file"] }],
            SystemPrompt = "自定义系统提示",
        };
        AgentConfig.Save(cfg, path);

        var loaded = AgentConfig.Load(path);
        Assert.Equal("hitmargin", loaded.Provider);
        Assert.Equal("tencent/hy3:free", loaded.Providers["hitmargin"].Model);
        Assert.Equal(4096, loaded.Providers["hitmargin"].MaxTokens);
        Assert.Equal(0.1, loaded.Providers["hitmargin"].Temperature);
        Assert.Equal(42, loaded.MaxToolIterations);
        Assert.Equal(50_000, loaded.MaxHistoryChars);
        Assert.Equal(131_072, loaded.ContextWindow);
        Assert.False(loaded.AllowCommands);
        Assert.True(loaded.ConfirmCommands);
        Assert.Equal("bash", loaded.Shell);
        Assert.False(loaded.SaveSessions);
        Assert.False(loaded.StreamOutput);
        Assert.False(loaded.ShowToolCalls);
        Assert.False(loaded.RenderMarkdown);
        Assert.Equal("high", loaded.ThinkingEffort);
        Assert.Equal("moddev", loaded.DefaultMode);
        Assert.Equal("whitelist", loaded.FileAccess);
        Assert.Equal(2, loaded.ReadOnlyDirs.Count);
        Assert.Equal("fix", loaded.Modes[0].Name);
        Assert.Equal(new[] { "read_file" }, loaded.Modes[0].Tools);
        Assert.Equal("自定义系统提示", loaded.SystemPrompt);
    }

    [Fact]
    public void Save_ToMissingDirectory_Throws()
    {
        var bad = Path.Combine(_dir, "no", "such", "dir", "cfg.json");
        Assert.ThrowsAny<IOException>(() => AgentConfig.Save(new AgentConfig(), bad));
    }

    // ===== FileAccess / ReadOnlyDirs 与 Workspace 联动 =====

    [Fact]
    public void Workspace_IgnoresBlankReadOnlyDirs()
    {
        // 空白项被忽略：只保留有效项（不断言具体路径值，避免 Windows/Linux 路径分隔符差异）
        var ws = new Workspace(_dir, new[] { "", "   ", "D:/real" }, "whitelist");
        Assert.Single(ws.ReadOnlyRoots);
    }

    [Fact]
    public void Workspace_UnknownFileAccess_IsStrictLike()
    {
        // 未知权限值（如 "admin"）不应意外放开：不 full、不 whitelist，等价 strict
        var ws = new Workspace(_dir, new[] { Path.Combine(Path.GetTempPath(), "ext") }, "admin");
        Assert.False(ws.FullAccess);
        Assert.Throws<ToolException>(() => ws.ResolveRead(Path.Combine("..", "ext", "x.txt")));
    }

    [Fact]
    public void Workspace_FileAccessCaseInsensitive()
    {
        Assert.True(new Workspace(_dir, null, "FULL").FullAccess);
        Assert.False(new Workspace(_dir, null, "Whitelist").FullAccess); // whitelist 不是 full
    }
}
