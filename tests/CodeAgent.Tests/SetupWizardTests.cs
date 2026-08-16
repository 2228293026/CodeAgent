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
}
