using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 连续工具调用的分组汇总行（<c>⏵⏵ 5 次工具调用（Read×3 Grep×2）(3.4s)</c>）。
///
/// 回归点：一轮里连续跑十几次 grep/read 时，逐行状态会把对话刷走，
/// 真正重要的模型输出被顶到屏幕外。
///
/// 关键取舍：只**追加**汇总，不隐藏任何单行。折叠需要"之后再展开"的交互模型，
/// 而这里��流式输出、没法回头——藏起来的内容用户就再也看不到了。
/// </summary>
[Collection("ConsoleOutput")]
public class ToolGroupLineTests
{
    private static List<(string, bool)> Run(params (string Verb, bool Err)[] items) =>
        items.ToList();

    [Fact]
    public void CountsRepeatedVerbsAndSortsByFrequency()
    {
        var line = AgentClass.FormatToolGroupLine(
            Run(("Read", false), ("Grep", false), ("Read", false), ("Grep", false), ("Read", false)),
            TimeSpan.FromSeconds(3.4));
        Assert.StartsWith("⏵⏵ 5 次工具调用", line);
        Assert.Contains("Read×3", line);
        Assert.Contains("Grep×2", line);
        // 多的排前面：一眼看到主要在干什么
        Assert.True(line.IndexOf("Read×3", StringComparison.Ordinal) < line.IndexOf("Grep×2", StringComparison.Ordinal));
        Assert.Contains("3.4s", line);
    }

    [Fact]
    public void SingleOccurrenceHasNoMultiplier()
    {
        var line = AgentClass.FormatToolGroupLine(Run(("Read", false), ("Bash", false), ("Edit", false)), TimeSpan.FromSeconds(1));
        Assert.Contains("Read Bash Edit", line);
        Assert.DoesNotContain("×1", line);
    }

    [Fact]
    public void FailuresAreCountedNotHidden()
    {
        // 汇总行**不能**把失败抹平：那会让一屏"成功"里藏着没看见的失败
        var line = AgentClass.FormatToolGroupLine(
            Run(("Read", false), ("Bash", true), ("Read", false), ("Edit", true)), TimeSpan.FromSeconds(2));
        Assert.StartsWith("⏵⏵ 4 次工具调用", line);
        Assert.Contains("⚠ 2 失败", line);
    }

    [Fact]
    public void EmptyRunPrintsNothing()
    {
        Assert.Equal(string.Empty, AgentClass.FormatToolGroupLine(new List<(string, bool)>(), TimeSpan.Zero));
    }

    [Fact]
    public void TheThresholdSuppressesShortRuns()
    {
        // 少于 3 次不打印：两行能一眼看完，再加一行汇总只是噪声
        Assert.Equal(3, AgentClass.ToolGroupMin);
    }

    [Fact]
    public void NeverOverflowsAtAnyWidth()
    {
        var many = Run(Enumerable.Range(0, 12).Select(i => (i % 3 == 0 ? "Read" : "Grep", false)).ToArray());
        foreach (var w in new[] { 1, 5, 12, 20, 40, 120 })
        {
            var line = AgentClass.FormatToolGroupLine(many, TimeSpan.FromSeconds(12), w);
            Assert.False(string.IsNullOrEmpty(line), $"宽度 {w} 输出为空");
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"宽度 {w} 溢出: {TextUtil.DisplayWidth(line)}");
        }
    }

    [Fact]
    public void AsciiModeUsesTheAsciiMark()
    {
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
            var line = AgentClass.FormatToolGroupLine(Run(("Read", false), ("Read", false), ("Read", false)), TimeSpan.FromSeconds(1));
            Assert.StartsWith(">> 3 ", line);
        }
        finally { SafeColor.ReadEnv = saved; }
    }

    [Fact]
    public void TheRunIsActuallyRecordedAndFlushed()
    {
        // 源码守卫：只测 formatter 会漏掉「没记进本批」和「批结束没打印」两步，
        // 而那正是这个功能唯一能被用户看到的地方。
        var source = ReadSource();
        Assert.Contains("acc.Add((ToolVerb(tc.Name), isError))", source);
        Assert.Contains("FinishRun();", source);
        Assert.Contains("run.Count < ToolGroupMin", source);
    }

    private static string ReadSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = System.IO.Path.Combine(dir, "src", "CodeAgent", "Agent", "Agent.cs");
                if (System.IO.File.Exists(candidate) && new System.IO.FileInfo(candidate).Length > 1000)
                    return System.IO.File.ReadAllText(candidate);
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Agent/Agent.cs");
    }
}
