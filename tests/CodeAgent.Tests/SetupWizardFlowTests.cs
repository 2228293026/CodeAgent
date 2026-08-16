using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class SetupWizardFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-wizard-" + Guid.NewGuid().ToString("N"));
    private readonly string _origCwd;

    public SetupWizardFlowTests()
    {
        Directory.CreateDirectory(_dir);
        _origCwd = Environment.CurrentDirectory;
        // Run 内部保存到 CurrentDirectory/codeagent.json：临时切换到隔离目录，避免污染项目
        Environment.CurrentDirectory = _dir;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _origCwd;
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private static (AgentConfig Config, string Output) RunWizard(string input)
    {
        var config = new AgentConfig();
        var writer = new StringWriter();
        using var reader = new StringReader(input);
        SetupWizard.Run(config, reader, writer);
        return (config, writer.ToString());
    }

    [Fact]
    public void SelectOpenAi_WithEnvKey_GeneratesConfig()
    {
        // 输入：选 openai(1) → 模型回车默认 → 地址回车默认 → Key 方式 1(环境变量) → 环境变量名回车默认
        var (config, output) = RunWizard("1\n\n\n1\n\n");

        Assert.Equal("openai", config.Provider);
        var opts = config.Providers["openai"];
        Assert.Equal("https://api.openai.com/v1", opts.BaseUrl);
        Assert.Equal("gpt-4o", opts.Model);
        Assert.Equal("OPENAI_API_KEY", opts.ApiKeyEnv);
        Assert.Contains("配置已保存", output);
        Assert.True(File.Exists(Path.Combine(_dir, "codeagent.json"))); // 真实落盘
    }

    [Fact]
    public void SelectCustom_WithDirectKey_WritesProvidedValues()
    {
        // 输入：选 custom(7) → 模型 my-model → 地址 https://x/v1 → Key 方式 2(直接输入) → key sk-test
        var (config, _) = RunWizard("7\nmy-model\nhttps://x.example/v1\n2\nsk-test\n");

        Assert.Equal("custom", config.Provider);
        var opts = config.Providers["custom"];
        Assert.Equal("my-model", opts.Model);
        Assert.Equal("https://x.example/v1", opts.BaseUrl);
        Assert.Equal("sk-test", opts.ApiKey);
        Assert.Null(opts.ApiKeyEnv);
    }

    [Fact]
    public void SelectOllama_FreeService_GetsPlaceholderKey()
    {
        // 输入：选 ollama(4)，免费服务不询问 Key
        var (config, _) = RunWizard("4\n");

        Assert.Equal("ollama", config.Provider);
        Assert.Equal("ollama", config.Providers["ollama"].ApiKey);
    }

    [Fact]
    public void InvalidChoice_Reprompts()
    {
        // 第一次输入非法(99)，第二次输入 openai(1)
        var (config, output) = RunWizard("99\n1\n\n\n1\n\n");

        Assert.Equal("openai", config.Provider);
        Assert.Contains("请输入 1-7 之间的数字", output); // 提示后重问
    }

    [Fact]
    public void EofInput_CancelsWizard()
    {
        // 输入立即 EOF：AskChoice 应抛 OperationCanceledException，且不保存配置
        var config = new AgentConfig();
        using var reader = new StringReader("");
        using var writer = new StringWriter();

        Assert.Throws<OperationCanceledException>(() => SetupWizard.Run(config, reader, writer));
        Assert.False(File.Exists(Path.Combine(_dir, "codeagent.json")));
    }
}
