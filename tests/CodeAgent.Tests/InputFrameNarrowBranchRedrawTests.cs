using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入框形状不变量：**「边框在上、提示符在下」在宽窄两种终端下都成立**。
///
/// 用户报上来的现象：每敲一个字，屏幕多出一行「◈ high · /thinking」。
/// 根因是重绘块里混进了边框——重绘是「\r\x1b[2K + text」，清行只作用一行，
/// 多一行就往下堆一行。
///
/// <see cref="RedrawInvariantTests"/> 里的重绘不变量只跑了 **width=80（宽分支）**。
/// 而 <c>BuildInputFrameTop</c> 的**窄分支**（放不下右侧提示时）历史上返回过
/// 「提示符 + 换行 + 边框」——形状正好相反，于是「最后一行就是输入行」的前提不成立。
/// 也就是说：**这个 bug 已经因为窄分支犯过一次，而现有守卫看不到窄分支。**
/// 把它再塞回去，今天所有测试仍然是绿的。
/// </summary>
public sealed class InputFrameNarrowBranchRedrawTests
{
    private const string Prompt = "[code] CodeAgent> ";

    private static int CountNewlines(string s) => s.Count(c => c == '\n');

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(200)]
    public void TheLastLineIsAlwaysThePromptAndNeverTheBorder(int width)
    {
        var framed = Program.BuildInputFrameTop(Prompt, Program.InputModeHint("high"), width);
        var tail = Program.BuildInputTextTail(framed);
        var ruleChar = SafeColor.Glyphs.RuleChar.ToString();

        // 1) 尾行绝不能是边框：边框参与重绘就是"敲一个字多一行"的根因
        Assert.False(tail.StartsWith(ruleChar, StringComparison.Ordinal),
            $"宽度 {width}：重绘块里混进了边框 → tail=[{tail}]");

        // 2) 尾行必须**是提示符本身**（可能按宽度收口），否则光标列会按边框宽度算，
        //    敲一个字符就折行。窄终端下提示符会被收口成「[code] Co…」，
        //    所以比的是"去掉省略号后是提示符的前缀"，不是"包含完整提示符"。
        Assert.DoesNotContain('\n', tail);
        Assert.StartsWith(tail.TrimEnd(SafeColor.Glyphs.Ellipsis), Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(80)]
    public void TypingNeverEmitsANewlineAtThisWidth(int width)
    {
        var framed = Program.BuildInputFrameTop(Prompt, Program.InputModeHint("high"), width);
        // 刻意用被测的辅助函数取尾行，而不是行内联展开——真实调用点走的就是它
        var tail = Program.BuildInputTextTail(framed);
        var budget = width > 24 ? width - 4 : 20;

        var emitted = new System.Text.StringBuilder();
        emitted.Append(framed);
        var lastInputLines = 1; // 重绘块基线（**不含边框**）
        var lastCursorLine = 0;

        for (var i = 1; i <= 20; i++)
        {
            var text = InputLine.FormatInputText(tail, false, "", false, -1, 0, new string('x', i),
                24, budget, null, colored: false);
            var seq = InputLine.BuildRedrawAnsi(text, lastInputLines, lastCursorLine, out var newLines);
            emitted.Append(seq);
            lastInputLines = newLines;
            lastCursorLine = 0;
        }

        // 首绘只有「边框与提示符之间」那 1 个换行，之后 20 次按键一个都不许产生
        Assert.Equal(1, CountNewlines(emitted.ToString()));
    }

    [Fact]
    public void ANarrowFrameStillHasTheBorderOnTop()
    {
        // 直接钉住形状：窄分支也必须是「边框行 + 提示符行」，不是反过来
        var framed = Program.BuildInputFrameTop(Prompt, Program.InputModeHint("high"), 10);
        var lines = framed.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith(new string(SafeColor.Glyphs.RuleChar, 1), lines[0], StringComparison.Ordinal);
        // 提示符在窄屏下会被收口，只要求它是提示符的前缀
        Assert.StartsWith(lines[1].TrimEnd(SafeColor.Glyphs.Ellipsis), Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void WidthUnknownMeansNoFrameAtAll()
    {
        // width<=0：宁可少一条线也不画必然越界的。此时尾行就是提示符本身
        var framed = Program.BuildInputFrameTop(Prompt, Program.InputModeHint("high"), 0);
        Assert.Equal(Prompt, framed);
        Assert.Equal(Prompt, Program.BuildInputTextTail(framed));
    }

    [Fact]
    public void TheCallSiteUsesTheSameTailHelper()
    {
        // 只测辅助函数会漏掉「重绘压根没用它」——那正是 bug 的形状
        var src = System.IO.File.ReadAllText(FindInputLine()).Replace("\r\n", "\n");
        // 必须走辅助函数，不能内联展开同一套切法：两份切法迟早走偏一份，
        // 而走偏的后果就是边框混进重绘块。
        Assert.Contains("Program.BuildInputTextTail(promptPlain)", src);
        Assert.DoesNotContain(@"promptPlain.LastIndexOf('\n')", src);
        // 首绘仍然写整段（含边框），重绘只写尾行
        Assert.Contains("Console.Write(prompt);", src);
    }

    private static string FindInputLine()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(d, "src", "CodeAgent", "InputLine.cs");
                if (System.IO.File.Exists(f))
                    return f;
                var parent = System.IO.Path.GetDirectoryName(d.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/InputLine.cs");
    }
}
