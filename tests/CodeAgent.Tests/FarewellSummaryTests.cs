using System;
using System.IO;
using System.Linq;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 退出小结：一句话交代这次会话做了多少。
///
/// 此前 /exit 只丢下一句「再见」，EOF 更是静默退出。一次会话可能持续几小时、
/// 跑过几十次请求，花了多少、做了多少一个字都没有——用户想核对，
/// 只能赶在退出前先敲 /stats，而那时往往已经想走了。
/// </summary>
/// <remarks>
/// 在 <c>ConsoleOutput</c> 集合里：字形来自 <c>SafeColor.Glyphs</c>（读进程级可替换的
/// <c>SafeColor.ReadEnv</c>）。
/// </remarks>
[Collection("ConsoleOutput")]
public class FarewellSummaryTests
{
    private static AgentClass NewAgent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ca-bye-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "codeagent.json"), "{}");
        return new AgentClass(
            new AgentConfig { SaveSessions = false },
            new FakeProvider { NextResponse = new Providers.ProviderResponse { Text = "ok" } },
            ToolRegistry.CreateDefault(),
            dir);
    }

    [Fact]
    public void NoRequestsMeansNoSummaryAtAll()
    {
        // 打开看看、或者只敲了 /help 就退出：打一行「0 轮 · 0 次」纯属噪音
        var agent = NewAgent();
        Assert.Equal(string.Empty, Program.FormatFarewellSummary(agent, TimeSpan.FromSeconds(30), 120));
    }

    [Fact]
    public void ReportsRequestsToolCallsAndTime()
    {
        var agent = NewAgent();
        var type = typeof(AgentClass);
        type.GetProperty(nameof(AgentClass.ProviderCalls))!.SetValue(agent, 3);
        var line = Program.FormatFarewellSummary(agent, TimeSpan.FromSeconds(260), 120);
        Assert.Contains("3", line);
        Assert.Contains(TextUtil.FormatSessionTime(TimeSpan.FromSeconds(260)), line);
    }

    [Fact]
    public void NeverExceedsTheGivenWidth()
    {
        // 摘要属于**最后一眼**：折行会把「再见」顶开，看起来像出错
        var agent = NewAgent();
        typeof(AgentClass).GetProperty(nameof(AgentClass.ProviderCalls))!.SetValue(agent, 123);
        for (var w = 1; w <= 120; w++)
        {
            var line = Program.FormatFarewellSummary(agent, TimeSpan.FromSeconds(3721), w);
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"宽度 {w}：小结 {TextUtil.DisplayWidth(line)} 列越界 → [{line}]");
        }
    }

    [Fact]
    public void BothExitPathsPrintIt()
    {
        // 两条出口各写一份必然有一份被忘掉；这里要求它们都调用同一个函数。
        var src = ReadSource("Program.cs");
        var calls = 0;
        var at = 0;
        while ((at = src.IndexOf("FormatFarewellSummary(agent", at, StringComparison.Ordinal)) > 0)
        {
            calls++;
            at += 10;
        }
        Assert.True(calls >= 2, $"只有 {calls} 处打印退出小结；/exit 与 EOF 两条出口都要有");
    }

    [Fact]
    public void ItIsPrintedBeforeTheFarewellWordNotAfter()
    {
        // 顺序反了会变成「再见！… 本次 3 轮」——小结像是挂在告别后面的一句补充
        var src = ReadSource("Program.cs");
        var at = src.IndexOf("case \"/exit\" or \"/quit\":", StringComparison.Ordinal);
        Assert.True(at > 0);
        // 注释里也写着「再见」：只取**代码行**（去掉 // 之后的部分）再比位置，
        // 否则注释里的那个词会抢先命中，断言以「顺序反了」的名义误报。
        var body = string.Join("\n", src.Substring(at, Math.Min(900, src.Length - at))
            .Split('\n')
            .Select(l =>
            {
                var idx = l.IndexOf("//", StringComparison.Ordinal);
                return idx >= 0 ? l[..idx] : l;
            }));
        var summaryAt = body.IndexOf("FormatFarewellSummary", StringComparison.Ordinal);
        var byeAt = body.IndexOf("再见", StringComparison.Ordinal);
        Assert.True(summaryAt > 0 && byeAt > 0);
        Assert.True(summaryAt < byeAt, "退出小结必须打在「再见」之前");
    }

    private static string ReadSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", file);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return File.ReadAllText(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/{file}");
    }
}
