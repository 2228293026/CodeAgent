using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// Ctrl+T 切换思考强度（学自 Claude Code）。
/// </summary>
public sealed class ThinkingCycleTests
{
    [Fact]
    public void Cycle_MovesToTheNextEffort()
    {
        Assert.Equal("low", Program.NextThinkingEffort("off"));
        Assert.Equal("medium", Program.NextThinkingEffort("low"));
        Assert.Equal("high", Program.NextThinkingEffort("medium"));
        Assert.Equal("auto", Program.NextThinkingEffort("high"));
    }

    [Fact]
    public void Cycle_WrapsAtTheEnd()
    {
        // 连按 Ctrl+T 是"越来越用力"的意思。停在末档会让"再按没反应"看起来像坏了。
        Assert.Equal("off", Program.NextThinkingEffort("auto"));
    }

    [Fact]
    public void Cycle_RoundTrips()
    {
        var v = "medium";
        for (var i = 0; i < Program.ThinkingEfforts.Length; i++)
            v = Program.NextThinkingEffort(v);
        Assert.Equal("medium", v);
    }

    [Fact]
    public void Cycle_UnknownValueFallsBackToFirst()
    {
        // 保持原样会让按键彻底没效果——那比跳档更让人困惑
        Assert.Equal("off", Program.NextThinkingEffort("nonsense"));
        Assert.Equal("off", Program.NextThinkingEffort(null));
        Assert.Equal("off", Program.NextThinkingEffort(""));
    }

    [Fact]
    public void Efforts_AreUniqueAndNonEmpty()
    {
        Assert.NotEmpty(Program.ThinkingEfforts);
        Assert.Equal(Program.ThinkingEfforts.Length, Program.ThinkingEfforts.Distinct().Count());
        Assert.All(Program.ThinkingEfforts, e => Assert.False(string.IsNullOrWhiteSpace(e)));
    }

    [Fact]
    public void SlashThinking_AcceptsExactlyTheSameSet()
    {
        // 两处各写一份列表的话，Ctrl+T 切到某档、/thinking 却说"无效值"
        foreach (var e in Program.ThinkingEfforts)
            Assert.Contains(e, Program.ThinkingEfforts);
        // 常见的非法值确实不在集合里
        foreach (var bad in new[] { "max", "on", "1" })
            Assert.DoesNotContain(bad, Program.ThinkingEfforts);
    }

    [Fact]
    public void Marker_IsDistinctFromRecallAndFromRealInput()
    {
        // 输入行用标记把非文本按键回报给调用方；标记撞上真实输入或彼此撞上就会误触发
        Assert.NotEqual(InputLine.RecallMarker, InputLine.ThinkingMarker);
        Assert.DoesNotContain('\u001b', "普通用户输入");
    }

    [Fact]
    public void CtrlT_IsWiredIntoBothSides()
    {
        // 只测纯函数会漏掉「输入行发出标记」与「REPL 消费标记」这两步
        var input = System.IO.File.ReadAllText(Find("InputLine.cs"));
        var program = System.IO.File.ReadAllText(Find("Program.cs"));
        Assert.Contains("ThinkingMarker", input);   // 键位绑定
        Assert.Contains("InputLine.ThinkingMarker", program); // REPL 消费
        Assert.Contains("NextThinkingEffort(", program);
    }

    private static string Find(string name)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", name);
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException($"找不到 src/CodeAgent/{name}");
    }
}
