using System;
using System.IO;
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
        Assert.Equal(1, cfg.MaxToolIterations);   // 0 → 1
        Assert.Equal(1_000, cfg.MaxHistoryChars); // 10 → 1000
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
}
