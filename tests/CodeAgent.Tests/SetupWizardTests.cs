using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class SetupWizardTests
{
    [Fact]
    public void Presets_HaveRequiredFields()
    {
        // 回归：向导预设表每一项都必须有名称与类型，custom 之外应有 baseUrl/model/环境变量名
        var presets = SetupWizard.Presets;
        Assert.NotEmpty(presets);

        foreach (var p in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name), "预设缺少 name");
            Assert.False(string.IsNullOrWhiteSpace(p.Label), "预设缺少 label");
            Assert.True(p.Type is "openai" or "anthropic", $"预设 {p.Name} 的 type 非法: {p.Type}");
            if (p.Name != "custom")
            {
                Assert.False(string.IsNullOrWhiteSpace(p.BaseUrl), $"预设 {p.Name} 缺少 baseUrl");
                Assert.False(string.IsNullOrWhiteSpace(p.Model), $"预设 {p.Name} 缺少 model");
            }
        }
    }

    [Fact]
    public void Presets_IncludeCoreProviders()
    {
        // 向导应覆盖主流供应商：openai/deepseek/qwen/ollama/anthropic/custom
        var names = SetupWizard.Presets.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "openai", "deepseek", "qwen", "ollama", "anthropic", "custom" })
            Assert.True(names.Contains(expected), $"预设表缺少: {expected}");
    }

    [Fact]
    public void Presets_OpenAiCompatTypes_HaveAnthropicCounterpart()
    {
        // anthropic 预设的 type 必须是 anthropic，其余为 openai（协议分类正确）
        var anthropic = SetupWizard.Presets.First(p => p.Name == "anthropic");
        Assert.Equal("anthropic", anthropic.Type);
        Assert.All(
            SetupWizard.Presets.Where(p => p.Name != "anthropic"),
            p => Assert.Equal("openai", p.Type));
    }

    [Fact]
    public void Presets_Names_AreUnique()
    {
        // 预设名称重复会导致向导选择歧义（按序号选择不会歧义，但作为 Provider 键会覆盖）
        var names = SetupWizard.Presets.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Presets_Custom_HasEmptyBaseUrlAndModel()
    {
        // custom 预设由用户填写 baseUrl/model，预设本身应为空（向导会必填询问）
        var custom = SetupWizard.Presets.First(p => p.Name == "custom");
        Assert.Equal("", custom.BaseUrl);
        Assert.Equal("", custom.Model);
    }

    [Fact]
    public void Presets_FreeTier_HaveNoApiKeyEnv()
    {
        // 免费服务（ollama/hitmargin）不需要 API Key 环境变量
        foreach (var name in new[] { "ollama", "hitmargin" })
        {
            var p = SetupWizard.Presets.First(x => x.Name == name);
            Assert.Equal("", p.Env);
        }
    }

    [Fact]
    public void Presets_PaidTier_AllHaveApiKeyEnv()
    {
        // 付费服务（除 custom 与免费 tier）都应指定 API Key 环境变量
        foreach (var p in SetupWizard.Presets)
        {
            if (p.Name is "custom" or "ollama" or "hitmargin")
                continue;
            Assert.False(string.IsNullOrWhiteSpace(p.Env), $"预设 {p.Name} 缺少 env");
        }
    }

    [Theory]
    [InlineData("openai", "OPENAI_API_KEY")]
    [InlineData("deepseek", "DEEPSEEK_API_KEY")]
    [InlineData("anthropic", "ANTHROPIC_API_KEY")]
    public void Presets_KnownProviders_HaveStandardEnvNames(string name, string expectedEnv)
    {
        var p = SetupWizard.Presets.First(x => x.Name == name);
        Assert.Equal(expectedEnv, p.Env);
    }

    [Fact]
    public void TestConnection_NoKeyAndEnvUnset_SkipsWithHint()
    {
        // 无 Key 且环境变量未设置：跳过测试并给出明确提示（而不是含糊的 401）
        var envName = "CODEAGENT_NEVER_SET_" + Guid.NewGuid().ToString("N")[..8];
        var sw = new System.IO.StringWriter();
        SetupWizard.TestConnection("openai", new ProviderOptions { ApiKeyEnv = envName }, sw);
        Assert.Contains("跳过连接测试", sw.ToString());
        Assert.Contains(envName, sw.ToString());
    }
}
