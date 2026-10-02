using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// token 费用必须把**命中缓存**的那部分算进去。
///
/// 第 298 轮把 OpenAI 的 <c>prompt_tokens</c> 归一化成「不含 cached」之后，
/// 命中缓存的 token 就**不再出现在** <c>TurnInputTokens</c> 里了。
/// 而 <c>UsdCost</c> 的入参只有 input/output——于是那部分费用凭空消失。
///
/// 实测：命中 99% 缓存的一轮，摘要行报 <c>≈$0.0004</c>。按 $3/M 的输入价算，
/// 真实开销约 <c>$0.13</c>——**少报两个数量级**。而这行数字正是用户判断
/// "该继续用这个模型还是换便宜的"的唯一依据。少报等于让他以为很便宜而继续烧钱。
/// </summary>
public sealed class CostIncludesCachedTokensTests
{
    private const double In3 = 3.0;   // $/M
    private const double Out15 = 15.0; // $/M

    [Fact]
    public void CachedTokensAreBilledAtTheInputPrice()
    {
        // (347 + 41590) * $3/M + 10 * $15/M
        //   = 0.001041 + 0.124770 + 0.000150 = 0.125961
        var cost = TextUtil.UsdCost(347, 10, In3, Out15, 41_590)!.Value;
        Assert.Equal(0.125961, cost, 6);
    }

    [Fact]
    public void WithoutCachedTokensTheSameCallWouldBeNearlyFree()
    {
        // 这正是第 298 轮之后的状态：只按 347 个输入 token 计费
        var before = TextUtil.UsdCost(347, 10, In3, Out15)!.Value;
        var after = TextUtil.UsdCost(347, 10, In3, Out15, 41_590)!.Value;
        // 少报两个数量级——不是误差，是完全不同的量级
        Assert.True(after / before > 50, $"before={before} after={after}");
    }

    [Fact]
    public void ZeroCachedBehavesExactlyAsBefore()
    {
        // 单价都已配置，所以两次都必然非 null；!.Value 是必要的而非掩饰
        Assert.Equal(
            TextUtil.UsdCost(1000, 500, In3, Out15)!.Value,
            TextUtil.UsdCost(1000, 500, In3, Out15, 0)!.Value,
            12);
    }

    [Fact]
    public void MissingPricesStillSuppressTheCost()
    {
        Assert.Null(TextUtil.UsdCost(1000, 500, 0, Out15, 41_590));
        Assert.Null(TextUtil.UsdCost(1000, 500, In3, 0, 41_590));
    }

    [Fact]
    public void OutputPriceIsUnaffected()
    {
        // 两次调用的差额必须**恰好**等于缓存那部分的输入价：
        // 输出不含缓存，不能被 cachedTokens 污染，差额里不能混进输出成本
        var a = TextUtil.UsdCost(1000, 500, In3, Out15)!.Value;
        var b = TextUtil.UsdCost(1000, 500, In3, Out15, 41_590)!.Value;
        Assert.Equal(41_590 * In3 / 1_000_000.0, b - a, 9);
    }

    [Fact]
    public void BothCallSitesPassTheCachedCount()
    {
        // 纯函数测得再全，调用点不传就是白测——两处（回合摘要 / /stats）
        // 曾各自漏过一次，只修一处会让两个数字对不上。
        var src = ReadSource(Find("Program.cs"));
        Assert.Contains("agent.TurnCachedTokens);", src);
        Assert.Contains("agent.TotalCachedTokens);", src);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src, @"UsdCost\(").Count);
    }

    [Fact]
    public void TheCachedCountMatchesTheRatioDenominator()
    {
        // 比率分母是 in + cached，费用分子也必须是 in + cached：
        // 同一组 token，两个地方给出两种总量，用户会看出矛盾
        var src = ReadSource(Find("Program.cs"));
        Assert.Contains("agent.TurnInputTokens + agent.TurnCachedTokens", src);
        Assert.Contains("agent.TotalInputTokens + agent.TotalCachedTokens", src);
    }

    private static string ReadSource(string p) => File.ReadAllText(p).Replace("\r\n", "\n");

    private static string Find(string rel)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(d, "src", "CodeAgent", rel);
                if (File.Exists(f))
                    return f;
                var parent = System.IO.Path.GetDirectoryName(d.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException(rel);
    }
}
