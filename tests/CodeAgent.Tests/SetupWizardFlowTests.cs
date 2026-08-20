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
        SetupWizard.Run(config, reader, writer, null);
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
    public void SelectCustom_EmptyModelPrompt_ReasksInsteadOfCancelling()
    {
        // 回归：必填项直接回车曾把「空输入」误判为 EOF（Ask 把空输入映射回 null 默认值），
        // 整个向导被取消；应提示必填并继续询问
        var (config, output) = RunWizard("7\n\nmy-model\nhttps://x.example/v1\n3\n");

        Assert.Equal("custom", config.Provider);
        Assert.Equal("my-model", config.Providers["custom"].Model);
        Assert.Contains("必填", output); // 重新询问的提示
        Assert.Contains("配置已保存", output);
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

        Assert.Throws<OperationCanceledException>(() => SetupWizard.Run(config, reader, writer, null));
        Assert.False(File.Exists(Path.Combine(_dir, "codeagent.json")));
    }

    [Fact]
    public void ExistingCustomProvider_IsListedAndReused()
    {
        // 配置中已存在、不在预设表里的 provider（如手工编辑 codeagent.json 加的自定义项）：
        // 应显示为「已配置」选项，选中后直接沿用原设置（模型/地址/Key 都不再询问）
        var config = new AgentConfig();
        config.Providers["freescdn"] = new ProviderOptions
        {
            Type = "openai",
            BaseUrl = "https://ai.freescdn.com/v1",
            Model = "DeepSeek-V4-Flash",
            ApiKey = "sk-test",
        };
        var writer = new StringWriter();
        using var reader = new StringReader("8\n"); // 预设 7 项之后是第 8 项 freescdn

        SetupWizard.Run(config, reader, writer, null);

        Assert.Contains("freescdn（已配置）", writer.ToString());
        Assert.Contains("沿用已有配置: freescdn", writer.ToString());
        Assert.Equal("freescdn", config.Provider);
        var opts = config.Providers["freescdn"];
        Assert.Equal("https://ai.freescdn.com/v1", opts.BaseUrl); // 原设置原样保留
        Assert.Equal("DeepSeek-V4-Flash", opts.Model);
        Assert.Equal("sk-test", opts.ApiKey);
    }

    [Fact]
    public void ExistingCustomProviders_AreSortedAfterPresets()
    {
        // 多个自定义 provider 按名称排序，统一排在预设之后（编号从 8 起）
        var config = new AgentConfig();
        config.Providers["zproxy"] = new ProviderOptions { Type = "openai", BaseUrl = "https://z/v1", Model = "m1" };
        config.Providers["alpha"] = new ProviderOptions { Type = "openai", BaseUrl = "https://a/v1", Model = "m2" };
        var writer = new StringWriter();
        using var reader = new StringReader("8\n"); // 排序后第 8 项是 alpha

        SetupWizard.Run(config, reader, writer, null);

        var output = writer.ToString();
        var alphaIdx = output.IndexOf("alpha（已配置）", StringComparison.Ordinal);
        var zIdx = output.IndexOf("zproxy（已配置）", StringComparison.Ordinal);
        Assert.True(alphaIdx > 0 && zIdx > alphaIdx, "自定义项应按名称排序且排在预设之后");
        Assert.Contains("8) alpha（已配置）", output);
        Assert.Contains("9) zproxy（已配置）", output);
        Assert.Equal("alpha", config.Provider); // 选中排序后的第一项
    }

    [Fact]
    public void CustomSavePath_IsRespected()
    {
        // 回归：曾硬编码保存到当前目录 codeagent.json，-c 指定的配置路径被忽略；
        // 现在应保存到 savePath 指定的位置
        var config = new AgentConfig();
        var savePath = Path.Combine(_dir, "custom-config.json");
        using var reader = new StringReader("1\n\n\n1\n\n");
        using var writer = new StringWriter();

        SetupWizard.Run(config, reader, writer, savePath);

        Assert.True(File.Exists(savePath)); // 保存到指定路径
        Assert.False(File.Exists(Path.Combine(_dir, "codeagent.json"))); // 不再覆盖默认路径
        Assert.Contains("配置已保存", writer.ToString());
        Assert.Contains(savePath, writer.ToString()); // 提示使用指定路径
    }
}
