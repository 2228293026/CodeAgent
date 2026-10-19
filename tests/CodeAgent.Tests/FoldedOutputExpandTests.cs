using System;
using System.IO;
using System.Linq;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 折叠输出的「还能展开」（学自 Claude Code 的 <c>ctrl+o</c>）。
///
/// 折叠脚注此前写着「共 40 行，仅显示前 8 行」就**到此为止**——剩下 32 行
/// 在屏幕上根本不存在，用户想看只能重跑命令。而重跑可能花几十秒，
/// 甚至改变状态（重启服务、重跑迁移）。
///
/// 所以「折叠」与「丢失」必须能区分开：脚注要指出还有展开口，按键要真能展开。
/// </summary>
/// <remarks>
/// 在 <c>ConsoleOutput</c> 集合里：断言里会碰 <c>SafeColor.Glyphs</c>（读进程级可替换的
/// <c>ReadEnv</c>），与字形相关的测试串行才稳定。
/// </remarks>
[Collection("ConsoleOutput")]
public class FoldedOutputExpandTests
{
    private static string[] LongOutput(int lines)
        => Enumerable.Range(1, lines).Select(i => $"line {i}").ToArray();

    [Fact]
    public void FoldFootnotePointsAtTheExpandKey()
    {
        // 光说「仅显示前 8 行」等于宣告剩下的行永远看不到。
        var text = string.Join('\n', LongOutput(8));
        var preview = AgentClass.FormatToolOutputPreview(text, trueTotalLines: 40, trueTotalChars: 2310);
        Assert.Contains("仅显示前", preview);
        Assert.Contains("ctrl+o", preview);
    }

    [Fact]
    public void NothingFoldedMeansNothingToExpand()
    {
        // 没折叠过时说「没有可展开的内容」，而不是把刚打过的内容再刷一遍
        var agent = NewAgent();
        Assert.Null(agent.LastFoldedOutput);
        Assert.Null(agent.ExpandLastFoldedOutput());
    }

    private static AgentClass NewAgent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ca-fold-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "codeagent.json"), "{}");
        return new AgentClass(
            new AgentConfig { SaveSessions = false },
            new FakeProvider { NextResponse = new Providers.ProviderResponse { Text = "ok" } },
            ToolRegistry.CreateDefault(),
            dir);
    }

    [Fact]
    public void ExpandConsumesTheStoredOutput()
    {
        // 展开一次就消费掉：再按一次不该重复刷屏。
        // 这里用反射注入，因为把真实工具调用跑起来要起一个子进程。
        var agent = NewAgent();
        var field = typeof(AgentClass).GetField("_lastFoldedOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(agent, ("原文\n第二行", "✔ Run 0.3s"));

        Assert.Equal("原文\n第二行", agent.LastFoldedOutput);
        Assert.Equal("原文\n第二行", agent.ExpandLastFoldedOutput());
        // 第二次按：已经消费掉
        Assert.Null(agent.ExpandLastFoldedOutput());
        Assert.Null(agent.LastFoldedOutput);
    }

    [Fact]
    public void TheKeyIsActuallyWiredIntoTheInputLoop()
    {
        // 纯函数测得再全，按键没接进 REPL 也是白搭——那正是用户唯一能按到的地方。
        var src = ReadSource("Program.cs");
        Assert.Contains("line == InputLine.ExpandMarker", src);
        Assert.Contains("agent.ExpandLastFoldedOutput()", src);
        // 没内容时必须有提示，不能静默吞掉按键
        Assert.Contains("没有折叠的输出可展开", src);
    }

    [Fact]
    public void TheMarkerExistsAndDoesNotCollideWithOtherSentinels()
    {
        // 哨兵值必须是**非文本**的：混进正常输入会被当成命令执行。
        Assert.StartsWith("\u001b", InputLine.ExpandMarker, StringComparison.Ordinal);
        Assert.NotEqual(InputLine.RecallMarker, InputLine.ExpandMarker);
        Assert.NotEqual(InputLine.ThinkingMarker, InputLine.ExpandMarker);
    }

    [Fact]
    public void TheFailurePathKeepsItsOriginalToo()
    {
        // 失败路径恰恰是最需要展开的：一条 40 行的编译错误只露 8 行，
        // 而那 32 行正是判断错在哪一行的依据。
        // 此前只有成功路径留原件，于是最容易需要 ctrl+o 的时候它偏偏没有内容。
        var src = ReadSource("Agent.cs");
        Assert.Contains("if (failTotalLines > DiffUtil.CountLines(failPreview))", src);
    }

    [Fact]
    public void OnlyReallyFoldedOutputIsKept()
    {
        // 源码守卫：留原件的判定必须按**真实总行数**，而不是按预览文本的行数——
        // 后者恒等于 8，判定永远成立，于是没折叠过的输出也会被留下，
        // 之后按 ctrl+o 就把刚打完的内容再刷一遍。
        var src = ReadSource("Agent.cs");
        Assert.Contains("if (previewTotalLines > DiffUtil.CountLines(preview))", src);
    }

    private static string ReadSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", file);
                if (!File.Exists(candidate))
                    candidate = Path.Combine(dir, "src", "CodeAgent", "Agent", file); // Agent.cs 在子目录
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
