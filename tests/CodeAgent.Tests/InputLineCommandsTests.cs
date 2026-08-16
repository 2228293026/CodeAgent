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
}
