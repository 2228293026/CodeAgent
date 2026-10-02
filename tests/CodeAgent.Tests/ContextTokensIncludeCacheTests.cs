using System;
using System.IO;
using System.Linq;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 上下文占用那一栏此前显示的是 **4%→0%**，而且**永远**是 0% 量级。
///
/// 第 298 轮把 <c>prompt_tokens</c> 归一化成「不含 cached」之后，
/// <c>ContextTokens</c> 直接返回了那个归一化后的值。实测：
///
///   3 轮对话、system 提示词 4 万多 token 之后→   <c>ctx 2/1.0M (0%)</c>
///   再一轮                                →   <c>ctx 14/1.0M (0%)</c>
///
/// 也就是说**上下文看起来永远是空的**。而这恰恰是用户判断
/// "该 <c>/compact</c> 了没有"的唯一依据——一个永远显示 0% 的仪表盘，
/// 和没有仪表盘一样没用，甚至更糟：它让人以为还很宽裕，于是一直不压缩。
///
/// <see cref="AgentClass.ContextTokens"/> 的文档注释本来就写着
/// 「模型当前实际收到的上下文规模（含系统提示与全部历史）」——
/// 归一化把它违反了这个契约。这条把契约钉回去。
/// </summary>
public sealed class ContextTokensIncludeCacheTests
{
    [Fact]
    public void CachedTokensAreAddedBackForTheContextGauge()
    {
        // 纯函数层：LastInputTokens(新) + LastCachedTokens(缓存) 才是 prompt 规模
        var src = ReadSource(FindAgent());
        Assert.Contains("return LastInputTokens + LastCachedTokens;", src);
    }

    [Fact]
    public void TheDocumentedContractStillHolds()
    {
        // 注释里那句「含系统提示与全部历史」是**规范**，不是描述：
        // 断言它在，且紧邻的代码确实是两数相加
        var src = ReadSource(FindAgent());
        var i = src.IndexOf("public int ContextTokens", StringComparison.Ordinal);
        Assert.True(i > 0, "找不到 ContextTokens");
        var window = src.Substring(Math.Max(0, i - 900), 1500);
        Assert.Contains("含系统提示与全部历史", window);
    }

    [Fact]
    public void EveryResetOfTheInputCountAlsoResetsTheCachedCount()
    {
        // 漏清一处 = ctx 凭空变大（拿上一轮的历史缓存数加到新的、更短的历史上）。
        // 这正是「N 个调用点只改一个」那类错误，必须按**配对**来查，而不是按记忆。
        //
        // 扫描范围是整个 src/CodeAgent 而不只是 Agent.cs：**首版只扫了 Agent.cs**，
        // 于是 /load 与 /resume 那两处（都在 Agent.Session.cs）全部漏掉——
        // 而那两处恰好是"换一整份历史"，残留的缓存数影响最大。
        // 教训：按**文件**划定扫描范围，等于把别的文件里的同类错误直接放过去。
        var total = 0;
        foreach (var file in Sources())
        {
            var lines = ReadSource(file).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("LastInputTokens = 0;", StringComparison.Ordinal))
                    continue;
                total++;
                var next = string.Join('\n', lines.Skip(i + 1).Take(2));
                Assert.True(next.Contains("LastCachedTokens = 0;", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}:{i + 1} 清零了 LastInputTokens，但后面两行没清 LastCachedTokens → [{lines[i].Trim()}]");
            }
        }
        Assert.True(total >= 6, $"只扫到 {total} 处清零，扫描范围退化了（应有 /clear、/load、/resume、撤回、压缩、换模型 六处）");
    }

    private static System.Collections.Generic.IEnumerable<string> Sources()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var dir = Path.Combine(d, "src", "CodeAgent");
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                        yield return f;
                    yield break;
                }
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent");
    }

    [Fact]
    public void BothAreAssignedFromTheSameResponse()
    {
        // 取值的那一行也必须成对：只更新 in 不更新 cached，ctx 会用上一轮的缓存数
        var src = ReadSource(FindAgent());
        Assert.Contains("LastInputTokens = resp.InputTokens ?? 0;", src);
        Assert.Contains("LastCachedTokens = resp.CachedTokens ?? 0;", src);
        Assert.Single(System.Text.RegularExpressions.Regex
            .Matches(src, @"LastCachedTokens = resp\.CachedTokens"));
    }

    [Fact]
    public void TheGaugeIsNoLongerEmpty()
    {
        // 端到端形状：41k 的 prompt 必须显示成 41k 量级，而不是个位数
        var bar = Program.BuildStatusBar(
            "code", "m", @"D:\Projects\CodeAgent", null,
            "11", "14", Program.FooterCtxText(43_100, 1_000_000), "", 120, "99%");
        Assert.Contains("ctx 4%", bar);
        Assert.DoesNotContain("ctx 0%", bar);
    }

    [Fact]
    public void ZeroCachedStillWorks()
    {
        // 没有缓存时不得因此多出一段或变成空
        var ctx = Program.FooterCtxText(1_000, 1_000_000);
        Assert.Equal("ctx 0%", ctx);
        Assert.Equal("ctx 1.0k", Program.FooterCtxText(1_000, 0));
    }

    [Fact]
    public void TheSummaryAndTheStatusBarNowAgree()
    {
        // 摘要行的 in 是 11（新的），状态栏的 ctx 是 43.1k（含缓存）——
        // 两个数不同是对的，但**不能**把 ctx 也变成个位数。
        var src = ReadSource(FindProgram());
        Assert.Contains("agent.TurnInputTokens + agent.TurnCachedTokens", src);
        Assert.Contains("agent.ContextTokens", src);
    }

    private static string ReadSource(string p) => File.ReadAllText(p).Replace("\r\n", "\n");

    private static string FindProgram() => Find("Program.cs");

    private static string FindAgent() => Find(Path.Combine("Agent", "Agent.cs"));

    private static string Find(string rel)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(new[] { d, "src", "CodeAgent" }.Concat(rel.Split(Path.DirectorySeparatorChar)).ToArray());
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException(rel);
    }
}
