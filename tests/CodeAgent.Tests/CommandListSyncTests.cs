using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 命令清单的两份副本必须一致：<c>Program.ReplCommands</c>（/help 打印）
/// 与 <c>InputLine.Commands</c>（Tab 补全）各自维护一份。
///
/// 这是典型的**静默失效**：命令能执行，但 Tab 补不出来、<c>/help</c> 里也没有。
/// 用户只会得出"这功能不存在"的结论——而它明明能跑。
/// 而且没有任何编译错误或测试会指向它。
/// </summary>
public sealed class CommandListSyncTests
{
    private static HashSet<string> HelpNames() =>
        Program.ReplCommands
            .Select(c => c.Command)
            // "/export [名/编号/all]" → "/export"；"/exit, /quit" → 拆成两个
            .SelectMany(SplitNames)
            .Select(Normalize)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> CompletionNames() =>
        InputLine.Commands
            .Select(c => c.Name)
            .Select(Normalize)
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string s) =>
        s.StartsWith('/') ? s : "/" + s;

    private static IEnumerable<string> SplitNames(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            // 先 Trim 再按空格切："/exit, /quit" 的第二段是 " /quit"，
            // 不先 Trim 的话 Split(' ')[0] 是空串，/quit 会被整条丢掉
            .Select(s => s.Trim().Split(' ')[0].Trim())
            .Where(s => s.Length > 0);

    [Fact]
    public void EveryHelpCommandIsCompletable()
    {
        var missing = HelpNames().Except(CompletionNames()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "这些命令 /help 里有、Tab 补全里没有（用户敲出来补不出来）：" + string.Join(" ", missing));
    }

    [Fact]
    public void EveryCompletableCommandIsDocumented()
    {
        var missing = CompletionNames().Except(HelpNames()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "这些命令 Tab 能补、/help 里没有：" + string.Join(" ", missing));
    }

    [Fact]
    public void KnownCommandsArePresentInBoth()
    {
        // 逐个点名：防止将来把某条从两份清单里同时删掉，差集测试看不出问题
        foreach (var cmd in new[]
        {
            "/help", "/clear", "/compact", "/cls", "/model", "/provider", "/copy",
            "/prompt", "/files", "/config", "/session", "/setup", "/undo", "/diff",
            "/save", "/load", "/export", "/stats", "/status", "/retry", "/tools",
            "/providers", "/models", "/diag", "/history", "/resume", "/find",
            "/thinking", "/shell", "/mode", "/access", "/exit", "/quit",
        })
        {
            Assert.True(HelpNames().Contains(cmd), $"{cmd} 不在 /help 清单里");
            Assert.True(CompletionNames().Contains(cmd), $"{cmd} 不在 Tab 补全清单里");
        }
    }

    [Fact]
    public void BothListsAreNonEmptyAndUnique()
    {
        var help = Program.ReplCommands;
        Assert.True(help.Length > 0);
        var dupHelp = help.GroupBy(c => c.Command, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.True(!dupHelp.Any(), "/help 清单有重复条目：" + string.Join(" ", dupHelp));

        var dupComplete = InputLine.Commands.GroupBy(c => c.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.True(!dupComplete.Any(), "补全清单有重复条目：" + string.Join(" ", dupComplete));
    }

    [Fact]
    public void EveryCommandHasADescription()
    {
        foreach (var c in Program.ReplCommands)
            Assert.False(string.IsNullOrWhiteSpace(c.Description), $"{c.Command} 没有说明");
        foreach (var c in InputLine.Commands)
            Assert.False(string.IsNullOrWhiteSpace(c.Desc), $"{c.Name} 没有说明");
    }

    [Fact]
    public void EveryHelpCommandIsActuallyImplemented()
    {
        // 清单里有、源码里查无此名 = 敲下去没反应又不说为什么。**静默失效**。
        // 判定用"源码里出现过这个带引号的命令名"，而不是只找 `case "x"`：
        // /retry 在 REPL 循环里被 line.Equals 提前拦下，根本不进 switch。
        var source = System.IO.File.ReadAllText(FindProgram());
        var missing = HelpNames()
            .Where(n => !source.Contains($"\"{n}\"", StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0,
            "这些命令列在清单里但源码中查无此名：" + string.Join(" ", missing));
    }

    private static string FindProgram()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = System.IO.Path.Combine(dir, "src", "CodeAgent");
                var f = System.IO.Path.Combine(candidate, "Program.cs");
                if (System.IO.Directory.Exists(candidate) && System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
