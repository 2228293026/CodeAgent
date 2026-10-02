using System;
using System.IO;
using System.Linq;
using CodeAgent.Providers;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/clear</c> 之后，<c>/stats</c> 面板把**被清掉的那段对话**算在里面。
///
/// 实测：聊了两轮然后 <c>/clear</c>，面板给的是
///   请求次数 3 · 新输入 129 · 缓存命中 129,266 tokens（99%）· 会话时长 20s
/// 而当前对话是**空的**——一句话都没说。
///
/// 根因：<c>Reset()</c> 清了回合 token、上下文、任务清单、文件修改记录，
/// 唯独没清 <c>ProviderCalls</c> / <c>Total*</c>；而 <c>SessionStopwatch</c>
/// 还带着一句注释「/clear 不重置——统计的是会话进程本身」。
///
/// 结果是同一块面板里混着**两段时间**的数字：请求次数说的是旧对话，
/// 回合摘要说的是新对话。没有任何一行读得出意义。
///
/// 这条把"换了一整份对话，累计就重新开始"钉成规则：
/// <c>/clear</c>、<c>/load</c>、<c>/resume</c>、<c>--continue</c> 四条路径都必须归零。
/// </summary>
public sealed class ClearResetsSessionTotalsTests
{
    private static AgentClass NewAgent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ca-clear-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "codeagent.json"), "{}");
        return new AgentClass(
            new AgentConfig { SaveSessions = false },
            new FakeProvider { NextResponse = new Providers.ProviderResponse { Text = "ok" } },
            ToolRegistry.CreateDefault(),
            dir);
    }

    [Fact]
    public void ResetZeroesTheSessionTotals()
    {
        var agent = NewAgent();
        // 直接反射写入"用过的"状态，比跑一次真实对话便宜且确定
        Set(agent, nameof(AgentClass.ProviderCalls), 7);
        Set(agent, nameof(AgentClass.TotalInputTokens), 42_000L);
        Set(agent, nameof(AgentClass.TotalOutputTokens), 900L);
        Set(agent, nameof(AgentClass.TotalCachedTokens), 130_000L);

        agent.Reset();

        Assert.Equal(0, Get<int>(agent, nameof(AgentClass.ProviderCalls)));
        Assert.Equal(0L, Get<long>(agent, nameof(AgentClass.TotalInputTokens)));
        Assert.Equal(0L, Get<long>(agent, nameof(AgentClass.TotalOutputTokens)));
        Assert.Equal(0L, Get<long>(agent, nameof(AgentClass.TotalCachedTokens)));
    }

    [Fact]
    public void EveryConversationSwapZeroesTheTotals()
    {
        // 三条换对话的路径：Reset(/clear)、LoadSession(/load)、LoadSessionLog(/resume)
        // 少一条就会让面板回到"两段时间混在一起"的状态。
        var files = Sources().ToList();
        Assert.True(files.Count >= 2, $"只扫到 {files.Count} 个源文件，扫描范围退化了");

        var reset = files.Select(f => (f, t: File.ReadAllText(f).Replace("\r\n", "\n")))
            .First(x => x.t.Contains("public void Reset()", StringComparison.Ordinal));
        AssertZeroedBlock(reset.f, reset.t, "Reset() 之后");

        foreach (var pair in files.Select(f => (f, t: File.ReadAllText(f).Replace("\r\n", "\n"))))
        {
            var f = pair.f;
            var t = pair.t;
            foreach (var entry in new[]
            {
                ("internal void LoadSessionLog", "LoadSessionLog"),
                ("LoadSession(string", "LoadSession"),
            })
            {
                var marker = entry.Item1;
                var name = entry.Item2;
                var i = t.IndexOf(marker, StringComparison.Ordinal);
                if (i < 0)
                    continue;
                var block = t.Substring(i, Math.Min(3000, t.Length - i));
                Assert.True(block.Contains("TotalCachedTokens = 0;", StringComparison.Ordinal)
                            && block.Contains("ProviderCalls = 0;", StringComparison.Ordinal),
                    $"{Path.GetFileName(f)} 的 {name} 没有归零会话累计（/stats 会混着两段时间）");
            }
        }
    }

    private static void AssertZeroedBlock(string file, string src, string what)
    {
        var i = src.IndexOf("public void Reset()", StringComparison.Ordinal);
        var block = src.Substring(i, Math.Min(2500, src.Length - i));
        foreach (var name in new[] { "ProviderCalls = 0;", "TotalInputTokens = 0;", "TotalOutputTokens = 0;", "TotalCachedTokens = 0;" })
            Assert.True(block.Contains(name, StringComparison.Ordinal), $"{what} 缺少 {name}（{Path.GetFileName(file)}）");
    }

    [Fact]
    public void TheSessionTimerRestartsOnEveryConversationSwap()
    {
        var src = File.ReadAllText(Find("Program.cs")).Replace("\r\n", "\n");
        // 四条路径：/clear、/load、/resume、--continue
        Assert.Equal(4, System.Text.RegularExpressions.Regex
            .Matches(src, @"RestartSessionTimer\(\);").Count);
        foreach (var cmd in new[] { "case \"/clear\":", "case \"/load\":", "case \"/resume\":" })
        {
            var i = src.IndexOf(cmd, StringComparison.Ordinal);
            Assert.True(i > 0, $"找不到 {cmd}");
            var slice = src.Substring(i, Math.Min(1200, src.Length - i));
            Assert.True(slice.Contains("RestartSessionTimer();", StringComparison.Ordinal),
                $"{cmd} 之后没有重启会话计时（会话时长会包含被换掉的那段）");
        }
    }

    [Fact]
    public void TheTimerHelperIsNotADeadMethod()
    {
        // 上一条只查调用点；这里查定义真的存在且会真的 Restart
        var src = File.ReadAllText(Find("Program.cs")).Replace("\r\n", "\n");
        Assert.Contains("private static void RestartSessionTimer() => SessionStopwatch.Restart();", src);
    }

    [Fact]
    public void ResetStillClearsTheRoundLevelStateItUsedTo()
    {
        // 加了累计归零之后，不能顺手把原有的清零弄丢：
        // 回合 token / 上下文档位必须仍然是 0（否则 /clear 后状态栏会残留上一回合）
        var agent = NewAgent();
        agent.Reset();
        Assert.Equal(0, agent.TurnInputTokens);
        Assert.Equal(0, agent.TurnOutputTokens);
        Assert.Equal(0, agent.TurnCachedTokens);
        Assert.Equal(0, agent.TurnRounds);
        Assert.Equal(0, agent.LastInputTokens);
        Assert.Equal(0, agent.LastCachedTokens);
    }

    private static void Set(object target, string prop, object value)
    {
        var p = target.GetType().GetProperty(prop)!;
        p.SetValue(target, value);
    }

    private static T Get<T>(object target, string prop) =>
        (T)target.GetType().GetProperty(prop)!.GetValue(target)!;

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
