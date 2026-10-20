using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /status 面板在各种终端宽度下都不许越界。
///
/// 渲染走 <c>FormatKeyValueLine</c>（已按显示宽度对齐与截断），
/// 但键值本身长度不受控：工作区路径可以很长、模型名固定 25 字符、
/// 「上下文 60%（120.0K/200.0K）」这种值也不短。
/// width 1..60 逐个量，每一行都不许超过。
/// </summary>
public class NarrowStatusBarTests
{
    private static List<(string Key, string Value)> Rows() => Program.BuildStatusRows(
        mode: "debug",
        model: "claude-sonnet-4-5-20250929",
        provider: "anthropic",
        cwd: @"D:\Projects\CodeAgent",
        branch: "main",
        contextTokens: 123456,
        contextWindow: 200000,
        turns: 42,
        toolCalls: 137,
        sessionTime: TimeSpan.FromHours(2) + TimeSpan.FromMinutes(13),
        version: "1.2.3",
        build: "Release",
        changedFiles: 7,
        taskDone: 3,
        taskTotal: 9);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(120)]
    public void NoRowExceedsTheWidth(int width)
    {
        var text = Program.FormatStatusPanel(Rows(), width);
        foreach (var line in text.Split('\n'))
        {
            var bare = line.TrimEnd('\r');
            if (bare.Length == 0)
                continue;
            var w = TextUtil.DisplayWidth(bare);
            Assert.True(w <= width, $"width={width} 的一行有 {w} 列：{bare}");
        }
    }

    [Fact]
    public void ZeroWidthIsUnknownAndKeepsEverything()
    {
        // 宽度未知（0）时不猜：字段一个都不丢
        var text = Program.FormatStatusPanel(Rows(), 0);
        Assert.Contains("模式", text);
        Assert.Contains("模型", text);
    }

    [Fact]
    public void WideTerminalKeepsAllRows()
    {
        var rows = Rows();
        var text = Program.FormatStatusPanel(rows, 120);
        foreach (var (key, _) in rows)
            Assert.Contains(key, text);
    }
}
