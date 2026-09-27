using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 启动横幅末尾的分隔线，以及 <c>/todos</c> 别名。
/// </summary>
public sealed class BannerRuleAndTodosAliasTests
{
    // —— 分隔线 ——

    [Fact]
    public void Rule_IsFullWidthOnWideTerminals()
    {
        var rule = Program.BuildBannerRule(200);
        Assert.Equal(Program.BannerRuleWidth, TextUtil.DisplayWidth(rule));
    }

    [Fact]
    public void Rule_NeverExceedsWidth()
    {
        for (var w = 1; w <= 200; w++)
            Assert.True(TextUtil.DisplayWidth(Program.BuildBannerRule(w)) <= w);
    }

    [Fact]
    public void Rule_NeverEndsWithAnEllipsis()
    {
        // 此前是 FitToWidth(rule×58, width)：窄终端截出的是一条**以省略号结尾的横线**。
        // 横线后面本来就没有内容，那个「还有」的暗示是假的，看起来像渲染出错。
        for (var w = 1; w <= 200; w++)
            Assert.DoesNotContain(SafeColor.Glyphs.Ellipsis, Program.BuildBannerRule(w));
    }

    [Fact]
    public void Rule_UsesTheGlyphSoAsciiModeApplies()
    {
        // 硬编码 '─' 的话 CODEAGENT_ASCII 覆盖不到
        Assert.Contains(SafeColor.Glyphs.RuleChar, Program.BuildBannerRule(10));
    }

    [Fact]
    public void Rule_UnknownWidthUsesTheFullWidth()
    {
        // width<=0 = 宽度未知，不做猜测（这条语义反复被栽）
        Assert.Equal(Program.BannerRuleWidth, TextUtil.DisplayWidth(Program.BuildBannerRule(0)));
    }

    [Fact]
    public void Rule_StaysUsableAtOneColumn()
    {
        Assert.Equal(1, TextUtil.DisplayWidth(Program.BuildBannerRule(1)));
    }

    [Fact]
    public void Rule_BannerActuallyUsesIt()
    {
        // 只测纯函数会漏掉"横幅根本没调它"
        var src = File.ReadAllText(Find("Program.cs"));
        Assert.Contains("BuildBannerRule(width)", src);
        Assert.DoesNotContain("new string(SafeColor.Glyphs.RuleChar, 58)", src);
    }

    // —— /todos 别名 ——

    [Fact]
    public void Todos_IsDispatchedLikeTasks()
    {
        var src = File.ReadAllText(Find("Program.cs"));
        // 两个 case 相邻落空 → 同一段逻辑，不是各自实现一份
        Assert.Contains("case \"/todos\":", src);
        var at = src.IndexOf("case \"/todos\":", StringComparison.Ordinal);
        var window = src.Substring(at, Math.Min(200, src.Length - at));
        Assert.Contains("case \"/tasks\":", window);
    }

    [Fact]
    public void Todos_IsCompletableAndDocumented()
    {
        // 用户敲 /todos 必须能补全出来，且 /help 里有说明
        var help = string.Join("\n", Program.ReplCommands.Select(c => c.Command));
        Assert.Contains("/todos", help);
        Assert.Contains("/todos", string.Join("\n", InputLine.Commands.Select(c => c.Name)));
    }

    [Fact]
    public void Todos_HelpTextPointsAtTheOtherName()
    {
        // 两个名字都在 /help 里时，得说清是同一个东西，否则用户以为是两件事
        var row = Program.ReplCommands.First(c => c.Command == "/todos");
        Assert.Contains("/tasks", row.Description);
    }

    [Fact]
    public void Todos_AndTasks_AreNotTheSameEntryTwice()
    {
        // 重复登记会让 /help 里出现两行一样的东西
        var names = Program.ReplCommands.Select(c => c.Command).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    private static string Find(string name)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", name);
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/{name}");
    }
}
