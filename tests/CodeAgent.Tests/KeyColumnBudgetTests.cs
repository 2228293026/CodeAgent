using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 键值行在窄终端下的键列上限。
/// 回归点：键列宽度由最长键决定（`IsOutputRedirected` = 18 列），窄屏上它会吃掉大半行，
/// 只给值留 7 列——用户看得见「是哪个设置」，却看不见「设成了什么」。
/// 键是标签、值是数据，标签列必须有上限。
/// </summary>
public class KeyColumnBudgetTests
{
    private const string LongKey = "IsOutputRedirected";
    private const string LongValue = "gpt-5-codex-high-reasoning";

    [Fact]
    public void UnknownWidthMeansNoCap()
    {
        Assert.Equal(0, Program.KeyColumnBudget(0));
        Assert.Equal(0, Program.KeyColumnBudget(-1));
    }

    [Fact]
    public void NarrowWidthCapsTheColumn()
    {
        // 40 列终端上键列最多 16 列，值至少拿 40-2-16-3 = 19 列
        var budget = Program.KeyColumnBudget(40);
        Assert.Equal(16, budget);
    }

    [Fact]
    public void ValueGetsItsShareOnNarrowTerminals()
    {
        // 契约：键列不超过 KeyColumnBudget(40) = 16 列；值拿到 40-2-16-3 = 19 列
        const int width = 40;
        var line = Program.FormatKeyValueLine(LongKey, LongValue, keyWidth: 18, width: width);
        var keyColumns = line.IndexOf(" : ", StringComparison.Ordinal) - 2; // 减去缩进
        Assert.True(keyColumns <= Program.KeyColumnBudget(width), $"键列 {keyColumns} 超过上限");
        var valueColumns = width - (2 + keyColumns + 3);
        Assert.True(valueColumns >= width * 0.4, $"值只拿到 {valueColumns}/{width} 列：{line}");
    }

    [Fact]
    public void WideTerminalKeepsTheFullKeyColumn()
    {
        var line = Program.FormatKeyValueLine(LongKey, "True", keyWidth: 18, width: 120);
        Assert.Equal($"  {LongKey} : True", line);
    }

    [Fact]
    public void AllRowsStillShareTheSameSeparatorColumn()
    {
        // 键列收窄是对**整列**等比生效的：冒号位置必须仍然一致，否则整列失去对齐
        const int width = 40;
        var keys = new[] { "a", LongKey, "medium_length_key" };
        var columns = keys
            .Select(k => Program.FormatKeyValueLine(k, "值", keyWidth: 18, width: width)
                .IndexOf(" : ", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        Assert.True(columns.Count == 1, $"冒号列应一致，实际 {string.Join(",", columns)}");
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 12, 20, 24, 30, 40, 60, 80 })
        {
            var line = Program.FormatKeyValueLine(LongKey, LongValue, keyWidth: 18, width: width);
            Assert.True(TextUtil.DisplayWidth(line) <= width,
                $"预算 {width} 实际 {TextUtil.DisplayWidth(line)}：{line}");
        }
    }

    [Fact]
    public void TruncatedValueIsMarked()
    {
        var line = Program.FormatKeyValueLine(LongKey, LongValue, keyWidth: 18, width: 30);
        Assert.Contains("…", line);
    }

    [Fact]
    public void RightAlignedNumbersStillWork()
    {
        var line = Program.FormatKeyValueLine("tokens", "1,234,567", keyWidth: 10, width: 40, rightAlignValue: true);
        Assert.Contains("1,234,567", line);
        Assert.EndsWith("1,234,567", line);
    }

    [Fact]
    public void KeyColumnMaxRatioIsSane()
    {
        // 局部变量：绕开编译器对 const 的常量折叠（否则这条断言会被判定恒真）
        double ratio = Program.KeyColumnMaxRatio;
        Assert.True(ratio > 0.2 && ratio < 0.6, $"键列占比应在 20%~60% 之间，实际 {ratio}");
    }

    [Fact]
    public void UnknownWidthKeepsThePreviousAppearance()
    {
        // 宽度未知时不做任何猜测性收窄，观测与此前一致
        var line = Program.FormatKeyValueLine(LongKey, LongValue, keyWidth: 18, width: 0);
        Assert.Equal($"  {LongKey} : {LongValue}", line);
    }
}
