using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 缓存命中率此前**恒为 0%**，而缓存其实命中得很高。
///
/// 两个 provider 的 usage 语义**不一致**，却都被原样送到同一个公式里：
///   OpenAI    <c>prompt_tokens</c>  **包含** <c>cached_tokens</c>
///   Anthropic <c>input_tokens</c>   **不含** <c>cache_read_input_tokens</c>
///
/// 比率的分母用 <c>TurnInputTokens</c>。OpenAI 侧命中缓存的那部分被算进了 "in"，
/// 却**不在**分母里 —— 分母巨大、分子相对很小，结果一律 0%。
///
/// 实测（kilo 网关，OpenAI 兼容形状）：一轮对话报 <c>41,937 in</c> 而 <c>0% cached</c>；
/// 用户实际提示词远不到 42k，差额几乎全在命中缓存的部分里。改对之后同一轮是
/// <c>1 in / 10 out tok 99% cached</c> —— <c>in</c> 只剩本轮真正新增的 token。
///
/// 修法在 provider 边界统一口径（OpenAI 侧把 cached 从 prompt_tokens 里减掉），
/// 于是上层 <c>TurnInputTokens</c> 到处都等于「未命中的新 token」，与 Anthropic 一致。
/// 比率的分母也改成 <c>in + cached</c>，两个 provider 才对得上。
/// </summary>
public sealed class CacheRatioSemanticsTests
{
    private static int? Norm(int? prompt, int? cached) =>
        CodeAgent.Providers.OpenAiProvider.NormalizeInputTokens(prompt, cached);

    [Fact]
    public void InputExcludesCachedTokens()
    {
        // prompt_tokens=41937 含 41590 的 cached：归一化后 in 应只剩 347
        Assert.Equal(347, Norm(41937, 41590));
    }

    [Fact]
    public void FullyCachedPromptLeavesZeroNewTokens()
    {
        Assert.Equal(0, Norm(41937, 41937)); // 不能为负
    }

    [Fact]
    public void NoCacheMeansInputPassesThroughUnchanged()
    {
        Assert.Equal(1234, Norm(1234, 0));
    }

    [Fact]
    public void MissingCachedFieldLeavesInputAlone()
    {
        Assert.Equal(999, Norm(999, null));
    }

    [Fact]
    public void MissingPromptFieldStaysNull()
    {
        Assert.Null(Norm(null, 41590));
    }

    [Fact]
    public void AnthropicSemanticsAreAlreadyCorrect()
    {
        // Anthropic 的 input_tokens 本来就不含 cache_read_input_tokens，
        // 归一化**不能**作用在它上面——否则会把 Anthropic 的口径改错。
        // 因此 provider 侧没有、也不该有任何对 Anthropic 的减法：
        // 同一组数字，Anthropic 形状直接给出 347，而 OpenAI 形状归一化后也必须是 347。
        var src = ReadSource(Find("Providers", "AnthropicProvider.cs"));
        Assert.DoesNotContain("NormalizeInputTokens", src);
        Assert.DoesNotContain("input_tokens\"] - ", src);
    }

    [Fact]
    public void BothProvidersNowMeanTheSameThingByInput()
    {
        // 同一组 usage 数字：OpenAI 形状 41937/41590 归一化后，
        // 必须等于 Anthropic 形状本来就报的那个数
        Assert.Equal(347, Norm(41937, 41590));
    }

    [Theory]
    [InlineData(347, 41590, 99)]
    [InlineData(1000, 1000, 50)]
    [InlineData(0, 100, 100)]
    public void TheRatioUsesInPlusCachedAsTheDenominator(int input, int cached, int expected)
    {
        // 只用 in 会算成 0%——那正是修之前的实测值（41,937 in / 0% cached）
        Assert.Equal(expected, TextUtil.PercentOf(cached, (long)input + cached));
    }

    [Fact]
    public void BothProvidersNormalizeInTheSamePlaces()
    {
        // 非流式与流式两条路径都必须减：只减一条会让 /stats 的累计值与回合摘要对不上
        var src = ReadSource(Find("Providers", "OpenAiProvider.cs"));
        Assert.Equal(2, Regex.Matches(src, @"inTok = NormalizeInputTokens\(inTok, cachedTok\);").Count);
        // 归一化必须**只有这一个实现**：如果有人在某条路径上又内联写了一遍减法，
        // 两条路径迟早会用不同写法/不同边界，"只剩一个来源"这条就破了。
        // （Math.Max 只允许出现在 NormalizeInputTokens 内部那一个。）
        Assert.Single(Regex.Matches(src, @"Math\.Max\(0, "));
    }

    [Fact]
    public void TheTurnSummaryAndStatsUseTheSameDenominator()
    {
        // 同一个口径散在两个地方算 = 迟早分叉（此前就是回合摘要一个、/stats 另一个）
        var src = ReadSource(Find("Program.cs"));
        Assert.Contains("agent.TurnInputTokens + agent.TurnCachedTokens", src);
        Assert.Contains("agent.TotalInputTokens + agent.TotalCachedTokens", src);
    }

    private static string ReadSource(string p) => System.IO.File.ReadAllText(p).Replace("\r\n", "\n");

    private static string Find(params string[] rel)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(new[] { d, "src", "CodeAgent" }.Concat(rel).ToArray());
                if (System.IO.File.Exists(f))
                    return f;
                var parent = System.IO.Path.GetDirectoryName(d.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new System.IO.FileNotFoundException(string.Join("/", rel));
    }
}
