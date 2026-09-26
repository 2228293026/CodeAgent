using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 模式切换确认行的窄屏降级。
/// 回归点：确认行此前是 `已切换模式: 名称 — 说明` 整体交给 FormatConfirmLine 硬截尾部，
/// 模式**名称很长**时被一起切掉：
///   ✔ 已切换模式: automati…
/// 用户根本没看到自己切到了哪个模式——这行确认存在的意义就没了。
/// 现在：名称优先完整，说明按剩余列数截；说明一行都放不下时只留名称。
/// </summary>
public class ModeSwitchedLineTests
{
    [Fact]
    public void WideLineKeepsEverything()
    {
        var line = Program.FormatModeSwitchedLine("plan", "自动规划并执行", 80);
        Assert.Equal("已切换模式: plan — 自动规划并执行", line);
    }

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        var line = Program.FormatModeSwitchedLine("plan", "自动规划并执行", 0);
        Assert.Equal("已切换模式: plan — 自动规划并执行", line);
    }

    [Fact]
    public void LongNameIsKeptWhole()
    {
        // 核心不变式：模式名称必须完整（此前被硬截成「已切换模式: automati…」）。
        // 只在**名称本身放得下**的宽度上断言——33 列的名称在 20 列终端里
        // 无论如何都放不下，那是物理限制，截断时也会带「…」。
        foreach (var width in new[] { 34, 40, 60 })
        {
            var line = Program.FormatModeSwitchedLine("a-very-long-mode-name", "说明", width);
            Assert.Contains("a-very-long-mode-name", line);
        }
    }

    [Fact]
    public void LongNameIsStillBounded()
    {
        // 名称本身比终端还宽时只能截，但必须标「…」
        var line = Program.FormatModeSwitchedLine(new string('n', 100), "说明", 20);
        Assert.True(TextUtil.DisplayWidth(line) <= 20, line);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 6, 10, 14, 20, 30, 50, 80 })
        {
            foreach (var (name, desc) in new[]
            {
                ("plan", "自动规划并执行"),
                ("a-very-long-mode-name", "一段很长的模式说明文字内容"),
                ("x", ""),
                ("自动模式", "自动规划"),
            })
            {
                var line = Program.FormatModeSwitchedLine(name, desc, width);
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
            }
        }
    }

    [Fact]
    public void DescriptionIsTruncatedBeforeTheName()
    {
        var line = Program.FormatModeSwitchedLine("plan", new string('d', 100), 30);
        Assert.Contains("plan", line);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void NoRoomForDescriptionDropsItWhole()
    {
        // 「已切换模式: plan」= 15 列；16 列放得下但说明只剩 1 列，不该显示半个说明
        var line = Program.FormatModeSwitchedLine("plan", "自动规划并执行", 16);
        Assert.Equal("已切换模式: plan", line);
    }

    [Fact]
    public void CjkDescriptionIsMeasuredByDisplayWidth()
    {
        var line = Program.FormatModeSwitchedLine("plan", "这是一段很长的中文模式说明文字", 30);
        Assert.True(TextUtil.DisplayWidth(line) <= 30, line);
    }

    [Fact]
    public void MinimumDescriptionWidthIsSane()
    {
        Assert.InRange(Program.MinModeDescriptionWidth, 3, 12);
    }

    [Fact]
    public void FitsInsideTheConfirmLineFrame()
    {
        // 确认行还要给 "✔ " 留 3 列
        foreach (var width in new[] { 20, 30, 50, 80 })
        {
            var body = Program.FormatModeSwitchedLine("plan", "自动规划并执行", width - 3);
            var line = Program.FormatConfirmLine(body, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }
}
