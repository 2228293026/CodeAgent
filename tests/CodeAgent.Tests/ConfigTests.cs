using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-config-" + Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void Load_ClampsInvalidBounds()
    {
        // 回归：maxToolIterations=0 曾导致 Agent 空转一轮；加载时应收敛到合法值
        var path = Path.Combine(_dir, "codeagent.json");
        File.WriteAllText(path, """
            {
              "provider": "openai",
              "providers": { "openai": { "type": "openai", "model": "gpt-4o" } },
              "maxToolIterations": 0,
              "maxHistoryChars": 10
            }
            """);

        var cfg = AgentConfig.Load(path);
        Assert.Equal(0, cfg.MaxToolIterations);     // 0 → 0（0 = 无限，不钳到 1）
        Assert.Equal(1_000, cfg.MaxHistoryChars); // 10 → 1000
    }

    [Fact]
    public void Load_ClampsHugeBoundsToCaps()
    {
        // 回归：maxToolIterations=9999 曾导致超长循环烧 token、maxHistoryChars=2e9 曾导致
        // 历史永不裁剪请求体无限膨胀（OOM）；现在超大值收敛到上限
        var path = Path.Combine(_dir, "huge.json");
        File.WriteAllText(path, """
            {
              "provider": "openai",
              "providers": { "openai": { "type": "openai", "model": "gpt-4o" } },
              "maxToolIterations": 9999,
              "maxHistoryChars": 2000000000
            }
            """);

        var cfg = AgentConfig.Load(path);
        Assert.Equal(200, cfg.MaxToolIterations);      // 9999 → 200（上限）
        Assert.Equal(20_000_000, cfg.MaxHistoryChars); // 2e9 → 20M（上限）
    }

    [Fact]
    public void Load_ClampsAutoCompactPercent()
    {
        // autoCompactPercent 收敛到 0-99：负值/超值不产生危险阈值
        var path = Path.Combine(_dir, "acp.json");
        File.WriteAllText(path, """{ "autoCompactPercent": 150 }""");
        Assert.Equal(99, AgentConfig.Load(path).AutoCompactPercent);

        var path2 = Path.Combine(_dir, "acp2.json");
        File.WriteAllText(path2, """{ "autoCompactPercent": -5 }""");
        Assert.Equal(0, AgentConfig.Load(path2).AutoCompactPercent); // 关闭
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        var path = Path.Combine(_dir, "nope.json");
        Assert.Throws<FileNotFoundException>(() => AgentConfig.Load(path));
    }

    [Fact]
    public void Load_InvalidJson_ThrowsInvalidData()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ not json !!");
        Assert.Throws<InvalidDataException>(() => AgentConfig.Load(path));
    }

    [Fact]
    public void Load_UnknownTopLevelKey_ProducesWarning()
    {
        // 拼写错误的配置项此前被反序列化静默丢弃——「配了但不生效」无任何线索
        var path = Path.Combine(_dir, "typo.json");
        File.WriteAllText(path, """
            {
              "provider": "openai",
              "providers": { "openai": { "type": "openai", "model": "gpt-4o" } },
              "maxToolIteratons": 5
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.Contains(cfg.Warnings, w => w.Contains("maxToolIteratons") && w.Contains("未知配置项"));
    }

    [Fact]
    public void Load_KnownKeys_NoWarnings()
    {
        var path = Path.Combine(_dir, "clean.json");
        File.WriteAllText(path, """
            {
              // 注释与尾逗号不应触发警告
              "provider": "openai",
              "providers": { "openai": { "type": "openai", "model": "gpt-4o", "apiKeyEnv": "OPENAI_API_KEY", } },
              "modes": [ { "name": "fix", "description": "d", "systemPrompt": "p", "tools": [] } ],
              "fileAccess": "whitelist",
              "readOnlyDirs": ["../libs"],
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.Empty(cfg.Warnings);
    }

    [Fact]
    public void Load_MaxHistoryChars_ClampedToBounds()
    {
        // 越界值必须收敛到安全范围（过大→历史永不裁剪撑爆请求；过小→几乎无历史）
        var path = Path.Combine(_dir, "mh.json");
        File.WriteAllText(path, "{ \"maxHistoryChars\": 999999999 }");
        Assert.Equal(20_000_000, AgentConfig.Load(path).MaxHistoryChars);

        var path2 = Path.Combine(_dir, "mh2.json");
        File.WriteAllText(path2, "{ \"maxHistoryChars\": 10 }");
        Assert.Equal(1_000, AgentConfig.Load(path2).MaxHistoryChars);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(500, 300)]
    [InlineData(60, 60)]
    public void Load_CommandTimeoutSeconds_ClampedToRange(int input, int expected)
    {
        // 命令超时必须收敛到 [1,300]（0/负=立即超时无意义；过大超过全局上限）
        var path = Path.Combine(_dir, $"to-{input}.json");
        File.WriteAllText(path, $"{{ \"commandTimeoutSeconds\": {input} }}");
        Assert.Equal(expected, AgentConfig.Load(path).CommandTimeoutSeconds);
    }

    [Fact]
    public void Load_OtherClampedFields_StayInRange()
    {
        // 其余带上下界的字段也应在加载时收敛，避免非法值导致空转/异常
        var path = Path.Combine(_dir, "clamp.json");
        File.WriteAllText(path, """
            {
              "autoCompactPercent": 200,
              "contextWindow": -5,
              "maxToolIterations": 99999,
              "maxSessionLogs": -1
            }
            """);
        var cfg = AgentConfig.Load(path);
        Assert.InRange(cfg.AutoCompactPercent, 0, 99);   // 200 → 99
        Assert.InRange(cfg.ContextWindow, 0, 10_000_000); // -5 → 0
        Assert.InRange(cfg.MaxToolIterations, 0, 200);    // 99999 → 200
        Assert.InRange(cfg.MaxSessionLogs, 0, 1000);       // -1 → 0
    }

    [Fact]
    public void Load_UnknownProviderAndModeKeys_ProduceWarnings()
    {
        var path = Path.Combine(_dir, "nested-typo.json");
        File.WriteAllText(path, """
            {
              "providers": {
                "deepseek": { "type": "openai", "baseurl": "https://x/v1", "api_key": "k" }
              },
              "modes": [ { "name": "a", "prompts": "p" } ]
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.Contains(cfg.Warnings, w => w.Contains("Provider 'deepseek'") && w.Contains("api_key"));
        Assert.DoesNotContain(cfg.Warnings, w => w.Contains("baseurl")); // 大小写不敏感绑定，不算未知
        Assert.Contains(cfg.Warnings, w => w.Contains("自定义模式[0]") && w.Contains("prompts"));
    }

    [Fact]
    public void Load_InvalidEnumValue_FallsBackWithWarning()
    {
        var path = Path.Combine(_dir, "enum.json");
        File.WriteAllText(path, """
            {
              "thinkingEffort": "ultra",
              "fileAccess": "god"
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.Equal("off", cfg.ThinkingEffort);   // 回退默认
        Assert.Equal("strict", cfg.FileAccess);    // 回退更严格默认
        Assert.Contains(cfg.Warnings, w => w.Contains("thinkingEffort"));
        Assert.Contains(cfg.Warnings, w => w.Contains("fileAccess"));
    }

    [Fact]
    public void Load_DuplicateOrShadowedModeNames_ProduceWarnings()
    {
        // 重名：只有第一个生效；与内置同名：内置优先，自定义被静默遮蔽——都是隐形坑
        var path = Path.Combine(_dir, "modes.json");
        File.WriteAllText(path, """
            {
              "modes": [
                { "name": "fix", "systemPrompt": "first" },
                { "name": "FIX", "systemPrompt": "second" },
                { "name": "review", "systemPrompt": "shadowing builtin" }
              ]
            }
            """);

        var cfg = AgentConfig.Load(path);

        // 警告点名后出现的重复项（大小写不敏感地与首个撞名）
        Assert.Contains(cfg.Warnings, w => w.Contains("'FIX' 重复"));
        Assert.Contains(cfg.Warnings, w => w.Contains("'review'") && w.Contains("内置模式同名"));
    }

    [Fact]
    public void Load_DefaultModeNotKnown_ProducesWarning()
    {
        // defaultMode 指向不存在的模式会静默回退到 code——必须警告，否则「配了不生效」无线索
        var path = Path.Combine(_dir, "defmode.json");
        File.WriteAllText(path, """
            {
              "defaultMode": "nonexistent",
              "modes": [
                { "name": "fix", "systemPrompt": "fix things" }
              ]
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.Contains(cfg.Warnings, w => w.Contains("defaultMode='nonexistent'") && w.Contains("回退到 code"));
    }

    [Fact]
    public void Load_DefaultModeReferencesCustomMode_NoWarning()
    {
        var path = Path.Combine(_dir, "defmode2.json");
        File.WriteAllText(path, """
            {
              "defaultMode": "fix",
              "modes": [
                { "name": "fix", "systemPrompt": "fix things" }
              ]
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.DoesNotContain(cfg.Warnings, w => w.Contains("defaultMode"));
    }

    [Fact]
    public void Load_ModeEntryMissingName_Warns()
    {
        // 自定义模式缺 name：会被 /mode 静默忽略——必须警告
        var path = Path.Combine(_dir, "modename.json");
        File.WriteAllText(path, """
            {
              "modes": [
                { "systemPrompt": "no name here" },
                { "name": "ok", "systemPrompt": "fine" }
              ]
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.Contains(cfg.Warnings, w => w.Contains("自定义模式缺少 name 字段"));
    }

    [Fact]
    public void Load_ProviderNotInProviders_Warns()
    {
        // provider 名拼错/漏配：运行时取不到连接配置会直接报错——提前警告
        var path = Path.Combine(_dir, "prov.json");
        File.WriteAllText(path, """
            {
              "provider": "mistral",
              "providers": {
                "openai": { "model": "gpt-4o" }
              }
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.Contains(cfg.Warnings, w => w.Contains("provider='mistral'") && w.Contains("不存在"));
    }

    [Fact]
    public void Load_ProviderMatchesProviders_NoWarning()
    {
        var path = Path.Combine(_dir, "prov2.json");
        File.WriteAllText(path, """
            {
              "provider": "openai",
              "providers": {
                "openai": { "model": "gpt-4o" }
              }
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.DoesNotContain(cfg.Warnings, w => w.Contains("provider='"));
    }

    [Fact]
    public void Load_ShellInvalid_Warns()
    {
        // shell 拼错（如 fish/zsh）：会被静默当默认处理——警告而非无提示失效
        var path = Path.Combine(_dir, "shell.json");
        File.WriteAllText(path, "{ \"shell\": \"fish\" }");

        var cfg = AgentConfig.Load(path);

        Assert.Contains(cfg.Warnings, w => w.Contains("shell='fish'") && w.Contains("支持的命令解释器"));
    }

    [Fact]
    public void Load_ShellValid_NoWarning()
    {
        foreach (var shell in new[] { "", "cmd", "powershell", "bash", "BASH" })
        {
            var path = Path.Combine(_dir, $"shell-{shell}.json");
            File.WriteAllText(path, $"{{ \"shell\": \"{shell}\" }}");
            var cfg = AgentConfig.Load(path);
            Assert.False(cfg.Warnings.Any(w => w.Contains("shell='")), $"shell={shell} 不应警告，实际警告: {string.Join("; ", cfg.Warnings)}");
        }
    }

    [Fact]
    public void Load_ProviderTypeInvalid_Warns()
    {
        // type 拼错（如 gemini）：连接时会直接报错——提前警告
        var path = Path.Combine(_dir, "ptype.json");
        File.WriteAllText(path, """
            {
              "providers": {
                "my": { "type": "gemini", "model": "gemini-x" }
              }
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.Contains(cfg.Warnings, w => w.Contains("Provider 'my'") && w.Contains("type='gemini'") && w.Contains("受支持的类型"));
    }

    [Fact]
    public void Load_ProviderTypeValid_NoWarning()
    {
        foreach (var type in new[] { "openai", "anthropic", "OPENAI" })
        {
            var path = Path.Combine(_dir, $"ptype-{type}.json");
            File.WriteAllText(path, $"{{ \"providers\": {{ \"p\": {{ \"type\": \"{type}\", \"model\": \"m\" }} }} }}");
            var cfg = AgentConfig.Load(path);
            Assert.False(cfg.Warnings.Any(w => w.Contains("type='")), $"type={type} 不应警告");
        }
    }

    [Fact]
    public void Load_ProviderMissingApiKey_Warns()
    {
        // apiKeyEnv/apiKey 都缺：运行时拿不到凭据直接报错——提前警告
        var path = Path.Combine(_dir, "nokey.json");
        File.WriteAllText(path, """
            {
              "providers": {
                "remote": { "type": "openai", "model": "x" }
              }
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.Contains(cfg.Warnings, w => w.Contains("Provider 'remote'") && w.Contains("apiKeyEnv") && w.Contains("apiKey"));
    }

    [Fact]
    public void Load_ProviderHasApiKeyEnv_NoApiKeyWarning()
    {
        var path = Path.Combine(_dir, "keyenv.json");
        File.WriteAllText(path, """
            {
              "providers": {
                "local": { "type": "openai", "model": "x", "apiKeyEnv": "MY_KEY" }
              }
            }
            """);

        var cfg = AgentConfig.Load(path);

        Assert.DoesNotContain(cfg.Warnings, w => w.Contains("apiKeyEnv") && w.Contains("Provider 'local'"));
    }

    [Fact]
    public void ValidateUnknownKeys_MalformedJson_ReturnsEmpty()
    {
        // 解析失败交给反序列化统一报错，校验器不重复抛异常
        Assert.Empty(AgentConfig.ValidateUnknownKeys("{ not json !!"));
    }

    [Fact]
    public void ValidateUnknownKeys_ScalarNestedValues_Ignored()
    {
        // providers/modes 的值不是对象（手写 JSON 手滑）时不应抛异常，也不产生噪音警告
        var warnings = AgentConfig.ValidateUnknownKeys(
            """{ "providers": { "x": "not-an-object" }, "modes": ["oops", 42] }""");
        Assert.Empty(warnings);
    }

    [Fact]
    public void ExampleConfig_HasNoUnknownKeys_AndLoadsClean()
    {
        // 防漂移守护：codeagent.example.json 必须被加载器无警告解析。
        // 新增/改名配置字段而忘了同步示例时，这个测试会红
        var dir = new DirectoryInfo(AppContext.BaseDirectory!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "codeagent.example.json")))
            dir = dir.Parent;
        Assert.NotNull(dir); // 仓库根必须能找到示例配置

        var json = File.ReadAllText(Path.Combine(dir!.FullName, "codeagent.example.json"));
        Assert.Empty(AgentConfig.ValidateUnknownKeys(json)); // 无未知键

        var tmp = Path.Combine(_dir, "example.json");
        File.WriteAllText(tmp, json);
        var cfg = AgentConfig.Load(tmp);
        Assert.Empty(cfg.Warnings); // 完整 Load 也无警告（枚举值合法等）
    }

    [Fact]
    public void SaveLoad_RoundTripsAllFields()
    {
        // 回归：camelCase 序列化往返后，多 provider、自定义模式、开关项都应存活
        var path = Path.Combine(_dir, "roundtrip.json");
        var original = new AgentConfig
        {
            Provider = "qwen",
            Providers =
            {
                ["qwen"] = new ProviderOptions { Type = "openai", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1", Model = "qwen3-coder-plus", ApiKeyEnv = "DASHSCOPE_API_KEY" },
                ["anthropic"] = new ProviderOptions { Type = "anthropic", Model = "claude-sonnet-4-5" },
            },
            MaxToolIterations = 5,
            MaxHistoryChars = 20_000,
            AllowCommands = false,
            ConfirmCommands = true,
            StreamOutput = false,
            ShowToolCalls = false,
            RenderMarkdown = false,
            ThinkingEffort = "medium",
            DefaultMode = "plan",
            ExportDir = ".codeagent/out",
            Modes = { new AgentModeConfig { Name = "fix", Description = "修复", SystemPrompt = "fix it", Tools = ["read_file"] } },
        };

        AgentConfig.Save(original, path);
        var loaded = AgentConfig.Load(path);

        Assert.Equal("qwen", loaded.Provider);
        Assert.True(loaded.Providers.ContainsKey("anthropic"));
        Assert.Equal("claude-sonnet-4-5", loaded.Providers["anthropic"].Model);
        Assert.Equal(5, loaded.MaxToolIterations);
        Assert.Equal(20_000, loaded.MaxHistoryChars);
        Assert.False(loaded.AllowCommands);
        Assert.True(loaded.ConfirmCommands);
        Assert.False(loaded.StreamOutput);
        Assert.False(loaded.ShowToolCalls);
        Assert.False(loaded.RenderMarkdown);
        Assert.Equal("medium", loaded.ThinkingEffort);
        Assert.Equal("plan", loaded.DefaultMode);
        Assert.Equal(".codeagent/out", loaded.ExportDir);
        var fix = Assert.Single(loaded.Modes);
        Assert.Equal("fix", fix.Name);
        Assert.Equal(new[] { "read_file" }, fix.Tools);
    }

    [Fact]
    public void WriteExample_ProducesLoadableConfig()
    {
        // 回归：--init 生成的示例配置应能被 Load 正常解析，且含全部默认 provider
        var path = Path.Combine(_dir, "example.json");
        AgentConfig.WriteExample(path);

        var cfg = AgentConfig.Load(path);
        Assert.Equal("openai", cfg.Provider);
        foreach (var name in new[] { "openai", "deepseek", "qwen", "ollama", "anthropic" })
            Assert.True(cfg.Providers.ContainsKey(name), $"示例配置缺少 provider: {name}");
        Assert.Equal("deepseek-chat", cfg.Providers["deepseek"].Model);
        Assert.Equal("anthropic", cfg.Providers["anthropic"].Type);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsFileAccess()
    {
        // /access 切换后写回配置文件：fileAccess 与 readOnlyDirs 应能保存并重新加载（重启保持）
        var path = Path.Combine(_dir, "access.json");
        var cfg = new AgentConfig
        {
            Provider = "hitmargin",
            FileAccess = "whitelist",
            ReadOnlyDirs = ["D:/Projects/adofai-libs"],
            MaxToolIterations = 0,
        };
        AgentConfig.Save(cfg, path);

        var loaded = AgentConfig.Load(path);
        Assert.Equal("whitelist", loaded.FileAccess);
        Assert.Contains("D:/Projects/adofai-libs", loaded.ReadOnlyDirs);
        Assert.Equal(0, loaded.MaxToolIterations); // 0 = 无限，保存后不被钳制
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsFullAccess()
    {
        // full 模式同样能持久化（Shift+Tab 切到 full 后重启保持）
        var path = Path.Combine(_dir, "full.json");
        var cfg = new AgentConfig { FileAccess = "full" };
        AgentConfig.Save(cfg, path);

        Assert.Equal("full", AgentConfig.Load(path).FileAccess);
    }

    [Fact]
    public void Save_KeepsChineseLiteral_NotEscaped()
    {
        // 回归：System.Text.Json 默认把非 ASCII 转成 \uXXXX；保存后文件应含中文原文（可读、diff 友好）
        var path = Path.Combine(_dir, "cn.json");
        var cfg = new AgentConfig { SystemPrompt = "你是 CodeAgent，务实、精确、诚实。" };
        AgentConfig.Save(cfg, path);

        var text = File.ReadAllText(path);
        Assert.Contains("你是 CodeAgent", text);
        Assert.DoesNotContain("\\u4F60", text); // 不应是 \uXXXX 转义
    }
}
