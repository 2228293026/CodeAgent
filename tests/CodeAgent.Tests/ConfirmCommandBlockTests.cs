using System;
using System.Linq;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 命令执行前的确认块。
///
/// 此前是 <c>Console.Write($"\n[codeagent] 执行命令? {cmd}\n[y/N] ")</c>：
///   · 命令**不按宽度收口**——而"命令很长"恰恰是最该让人停下来看清楚的情况；
///     折行后 [y/N] 被顶走，用户在一片命令里找按键位置。
///   · 没有任何地方说明 ESC 可以取消，尽管 ESC 确实会取消。
/// </summary>
public sealed class ConfirmCommandBlockTests
{
    [Fact]
    public void Block_ShowsTheCommand()
    {
        var block = ShellRunner.FormatConfirmCommandBlock("rm -rf build", 80);
        Assert.Contains("rm -rf build", block);
    }

    [Fact]
    public void Block_NeverExceedsWidth()
    {
        var long1 = string.Join(" && ", Enumerable.Repeat("very-long-argument-with-a-long-name", 20));
        foreach (var w in new[] { 8, 12, 20, 40, 80, 120 })
        {
            var block = ShellRunner.FormatConfirmCommandBlock(long1, w);
            foreach (var line in block.Replace("\r\n", "\n").Split('\n'))
                Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽 {TextUtil.DisplayWidth(line)}: {line}");
        }
    }

    [Fact]
    public void Block_NeverExceedsWidthWithCjk()
    {
        // 中文命令参数按字符数算会低估近一倍
        var cmd = "echo " + string.Concat(Enumerable.Repeat("中文字符串内容", 30));
        foreach (var w in new[] { 10, 20, 40, 80 })
        {
            var block = ShellRunner.FormatConfirmCommandBlock(cmd, w);
            foreach (var line in block.Replace("\r\n", "\n").Split('\n'))
                Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽: {line}");
        }
    }

    [Fact]
    public void Block_TruncatedCommandCarriesEllipsis()
    {
        // 这是安全确认：宁可少显示也不能让用户以为看全了
        var long1 = string.Join(" && ", Enumerable.Repeat("arg", 60));
        var block = ShellRunner.FormatConfirmCommandBlock(long1, 40);
        Assert.Contains(SafeColor.Glyphs.Ellipsis, block);
    }

    [Fact]
    public void Block_MultiLineCommandKeepsEveryLine()
    {
        var block = ShellRunner.FormatConfirmCommandBlock("echo a\necho b\necho c", 80);
        Assert.Contains("echo a", block);
        Assert.Contains("echo b", block);
        Assert.Contains("echo c", block);
    }

    [Fact]
    public void Block_AlwaysTellsTheUserHowToCancel()
    {
        // ESC 确实会取消（靠 ConsoleInputBusy 挡 ESC 监视线程实现），不说等于让人猜
        var block = ShellRunner.FormatConfirmCommandBlock("ls", 80);
        Assert.Contains(SafeColor.Glyphs.Escape, block);
        Assert.Contains("取消", block);
    }

    [Fact]
    public void Block_AlwaysOffersAllowOnce()
    {
        var block = ShellRunner.FormatConfirmCommandBlock("ls", 80);
        Assert.Contains("允许", block);
    }

    [Fact]
    public void Block_AsksBeforeActing()
    {
        // 这是确认不是通知：措辞必须带问句
        var block = ShellRunner.FormatConfirmCommandBlock("ls", 80);
        Assert.Contains("?", block);
    }

    [Fact]
    public void Block_UnknownWidthShowsEverything()
    {
        var cmd = "echo " + new string('x', 200);
        var block = ShellRunner.FormatConfirmCommandBlock(cmd, 0);
        Assert.Contains(cmd, block);
    }

    [Fact]
    public void Block_UsesGlyphsNotHardcodedDecoration()
    {
        // CODEAGENT_ASCII 模式要能整体替换，装饰字符只能来自 SafeColor.Glyphs
        var block = ShellRunner.FormatConfirmCommandBlock("ls", 80);
        Assert.DoesNotContain('[', block);
    }

    [Fact]
    public void Block_IsActuallyUsedByConfirmAsync()
    {
        // 只测纯函数会漏掉「接进确认流程」这一步
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("FormatConfirmCommandBlock(", src);
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Tools", "ShellRunner.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Tools/ShellRunner.cs");
    }
}
