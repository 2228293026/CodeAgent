using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class InputLineCommandsTests
{
    [Fact]
    public void Commands_CoverAllReplCommands()
    {
        // 回归：菜单目录须覆盖 REPL 支持的全部命令（Program.HandleCommand 的 case
        // 加 REPL 循环特殊处理的 /retry），防止新增命令后忘记同步菜单。
        var handled = new[]
        {
            "/help", "/clear", "/cls", "/model", "/config", "/session", "/setup",
            "/undo", "/diff", "/save", "/load", "/history", "/export", "/stats",
            "/tools", "/providers", "/mode", "/diag", "/models", "/thinking",
            "/exit", "/quit", "/retry",
        };
        var menu = InputLine.Commands.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var cmd in handled)
            Assert.True(menu.Contains(cmd), $"命令菜单缺少: {cmd}");
    }

    [Fact]
    public void Commands_NoDuplicatesAndSlashPrefixed()
    {
        // 菜单项应互不重复、都以 / 开头、且都有说明文字
        Assert.Equal(
            InputLine.Commands.Length,
            InputLine.Commands.Select(c => c.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(InputLine.Commands, c => Assert.StartsWith("/", c.Name));
        Assert.All(InputLine.Commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Desc)));
    }

    [Theory]
    // 回归：菜单 23 项、窗口 8 项时，数字 9 应返回 -1（第 9 项不可见，不应被触发）
    [InlineData(1, 0, 8, 23, 0)]   // 窗口第 1 项
    [InlineData(8, 0, 8, 23, 7)]   // 窗口最后一项
    [InlineData(9, 0, 8, 23, -1)]  // 超出可见窗口 → 忽略
    [InlineData(3, 5, 8, 23, 7)]   // 窗口已滚动：第 3 项对应列表下标 5+3-1=7
    [InlineData(9, 5, 8, 23, -1)]  // 滚动后数字 9 仍超出可见窗口
    [InlineData(4, 0, 2, 2, -1)]   // 菜单只有 2 项：数字 4 无效
    [InlineData(2, 0, 2, 2, 1)]    // 菜单 2 项：数字 2 有效
    [InlineData(1, 0, 0, 23, 0)]   // 滚动模式（menuShown=0）：视为全部项可见
    [InlineData(0, 0, 8, 23, -1)]  // 数字 0 无效
    public void DigitKeySelection_RespectsVisibleWindow(int n, int offset, int shown, int count, int expected) =>
        Assert.Equal(expected, InputLine.DigitKeySelection(n, offset, shown, count));
}
