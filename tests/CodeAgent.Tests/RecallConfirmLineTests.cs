using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 空输入时按 ESC 的**二次确认行**。
///
/// 它此前用 <c>promptPlain</c>（整段 = 上边框 + 提示符，**两行**）拼接，
/// 于是在输入框中间把**整个边框又打了一遍**。重绘是「\r\x1b[2K + text」，
/// 清行只作用一行——多出来的边框行没人负责擦除，于是：
///   · 按别的键取消 → 边框和确认文字留在屏幕上，下一次重绘从错误的位置开始
///   · 按 ESC 确认   → 同样留一行
/// 与用户最早报的「敲一个字就多出一行」是**同一个根因**（边框混进重绘块），
/// 只是触发键从"任意字符"换成了"ESC"。
///
/// 同一文件里其余快捷键（Alt+U / Alt+D / …）都只 <c>WriteLine()</c> 一行就返回，
/// 只有这条路径在重打整段提示符。
/// </summary>
public sealed class RecallConfirmLineTests
{
    private const string Prompt = "[code] CodeAgent> ";

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void TheConfirmLineIsExactlyOneLine(int width)
    {
        var framed = Program.BuildInputFrameTop(Prompt, Program.InputModeHint("high"), width);
        var tail = Program.BuildInputTextTail(framed);

        var confirm = InputLine.FormatRecallConfirm(tail);

        // 1) 绝不能含换行：多一行就多一行残留
        Assert.DoesNotContain('\n', confirm);

        // 2) 绝不能以边框字符开头：那就是"把边框又打了一遍"
        Assert.False(confirm.StartsWith(new string(SafeColor.Glyphs.RuleChar, 1), StringComparison.Ordinal),
            $"宽度 {width}：确认行里混进了边框 → [{confirm}]");

        // 3) 必须仍然带着提示符，否则用户不知道自己在确认什么
        Assert.StartsWith(tail.TrimEnd(SafeColor.Glyphs.Ellipsis), Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfirmLineKeepsTheRecallWording()
    {
        var confirm = InputLine.FormatRecallConfirm("> ");
        Assert.Contains("再按 Esc 撤回上一条消息", confirm);
        Assert.Contains("其他键继续", confirm);
    }

    [Fact]
    public void TheWholeFrameWouldHaveBrokenIt()
    {
        // 前提钉住：把整段 promptPlain 喂进去，断言 1) 立刻不成立。
        // 没有这条，将来有人"顺手改回 promptPlain"时很难一眼看出差别在哪。
        var framed = Program.BuildInputFrameTop(Prompt, Program.InputModeHint("high"), 80);
        var whole = framed.TrimStart('\n') + "(再按 Esc 撤回上一条消息，其他键继续)";
        Assert.Contains('\n', whole);
    }

    [Fact]
    public void TheCallSiteUsesTheTailNotTheWholeFrame()
    {
        var src = ReadSource(FindInputLine());
        Assert.Contains("var confirm = FormatRecallConfirm(promptTail);", src);
        // 内联拼整段的写法必须消失
        Assert.DoesNotContain(@"promptPlain + ""(再按 Esc", src);
    }

    [Fact]
    public void TheWholeFrameIsNeverPrintedMidInput()
    {
        // 比逐个快捷键检查更强：promptPlain（整段）现在**只**用来推导 tail，
        // 运行时再没有任何地方把它打出去——"边框混进重绘块"从此没有入口。
        var code = ReadSource(FindInputLine())
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();
        var uses = code
            .Select((l, i) => (Line: l.Trim(), No: i + 1))
            .Where(x => x.Line.Contains("promptPlain", StringComparison.Ordinal))
            .Select(x => x.No)
            .ToList();
        // 只允许两处：声明，以及把它喂给 BuildInputTextTail
        Assert.Equal(2, uses.Count);
        var src = ReadSource(FindInputLine());
        Assert.Contains("Program.BuildInputTextTail(promptPlain)", src);
    }

    private static string ReadSource(string p) => File.ReadAllText(p).Replace("\r\n", "\n");

    private static string FindInputLine()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(d, "src", "CodeAgent", "InputLine.cs");
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/InputLine.cs");
    }
}
