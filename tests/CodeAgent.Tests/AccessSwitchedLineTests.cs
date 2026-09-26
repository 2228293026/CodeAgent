using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 权限切换确认行的窄屏降级。
/// 回归点：`已切换权限: {mode}（{desc}）` 整行硬截尾部，权限名一长就**连名字**被切掉：
///   ✔ 已切换权限: whi…
/// 而 Shift+Tab 正是连着按下去的——用户连按三次，屏幕上三次都是「whi…」，
/// 根本不知道自己在 strict / whitelist / full 之间到了哪一档。
/// （模式切换在 UI Round 74 已修，权限切换这一处当时漏掉了。）
/// </summary>
public class AccessSwitchedLineTests
{
    [Fact]
    public void WideLineKeepsEverything()
    {
        var line = Program.FormatAccessSwitchedLine("whitelist", "工作区读写 + 只读白名单目录", 80);
        Assert.Equal("已切换权限: whitelist（工作区读写 + 只读白名单目录）", line);
    }

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        var line = Program.FormatAccessSwitchedLine("strict", "仅工作区可读写", 0);
        Assert.Equal("已切换权限: strict（仅工作区可读写）", line);
    }

    [Fact]
    public void ModeNameIsKeptWhole()
    {
        foreach (var mode in new[] { "strict", "whitelist", "full" })
        {
            foreach (var width in new[] { 26, 30, 40, 60 })
            {
                var line = Program.FormatAccessSwitchedLine(mode, "工作区读写 + 只读白名单目录", width);
                Assert.Contains(mode, line);
            }
        }
    }

    [Fact]
    public void BracketsDropBeforeTheModeName()
    {
        // 「已切换权限: whitelist」= 21 列；22 列只剩 1 列给括号+说明，整段丢掉
        var line = Program.FormatAccessSwitchedLine("whitelist", "工作区读写 + 只读白名单目录", 22);
        Assert.Equal("已切换权限: whitelist", line);
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var mode in new[] { "strict", "whitelist", "full", new string('m', 60) })
        {
            foreach (var width in new[] { 4, 8, 12, 20, 22, 26, 40, 60, 100 })
            {
                var line = Program.FormatAccessSwitchedLine(mode, "工作区读写 + 只读白名单目录", width);
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
            }
        }
    }

    [Fact]
    public void FitsInsideTheConfirmLineFrame()
    {
        foreach (var width in new[] { 30, 40, 60, 100 })
        {
            var body = Program.FormatAccessSwitchedLine("whitelist", "工作区读写 + 只读白名单目录", width - 3);
            var line = Program.FormatConfirmLine(body, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void BracketsAreFullWidthAndBalanced()
    {
        var line = Program.FormatAccessSwitchedLine("strict", "仅工作区可读写", 60);
        Assert.StartsWith("已切换权限: strict（", line);
        Assert.EndsWith("）", line);
    }

    [Fact]
    public void TruncatedDescriptionKeepsBrackets()
    {
        var line = Program.FormatAccessSwitchedLine("full", new string('d', 80), 40);
        Assert.Contains("（", line);
        Assert.EndsWith("）", line);
    }

    [Fact]
    public void UnknownModeFallsBackToItsOwnName()
    {
        var line = Program.FormatAccessSwitchedLine("custom", "custom", 60);
        Assert.Equal("已切换权限: custom（custom）", line);
    }

    [Fact]
    public void ModeAndAccessShareOneLadder()
    {
        // 两个调用点必须走同一套阶梯：说明放不下时都只剩值。
        // 宽度按**实际渲染**算：「已切换权限: strict」是 18 列，手数容易少算。
        var width = TextUtil.DisplayWidth("已切换权限: strict");
        Assert.True(width >= TextUtil.DisplayWidth("已切换模式: plan"), "测试前提：两个值都要放得下");
        Assert.Equal("已切换模式: plan", Program.FormatModeSwitchedLine("plan", new string('d', 80), width));
        Assert.Equal("已切换权限: strict", Program.FormatAccessSwitchedLine("strict", new string('d', 80), width));
    }
}
