using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 端到端钉死「敲一个字符不新增行」这条不变量。
///
/// 用户报上来的现象：每敲一个字，屏幕就多出一行边框提示（◈ high · /thinking）。
/// 之前这段重绘逻辑内联在 ScrollInput 里，只能靠肉眼看终端，任何改动都无法回归验证。
/// 现在 BuildRedrawAnsi 是纯函数，可以把「从空输入一路敲到 20 个字符」整个模拟一遍，
/// 直接数总共吐出了多少个换行符。
/// </summary>
public sealed class RedrawInvariantTests
{
    private const string Prompt = "[code|space-bunny-alpha] CodeAgent> ";
    private const string Hint = "◈ high · /thinking";

    private static int CountNewlines(string s) => s.TrimEnd('\n').Split('\n').Length - 1;

    [Fact]
    public void TypingOneCharacterAtATime_NeverEmitsANewline()
    {
        // 模拟：首绘（边框 + 提示符），然后逐字输入 20 次
        var framed = Program.BuildInputFrameTop(Prompt, Hint, 80);
        var tail = framed[(framed.LastIndexOf('\n') + 1)..];

        var emitted = new System.Text.StringBuilder();
        emitted.Append(framed);            // 首绘：2 行
        var lastInputLines = 1;             // 重绘块基线（**不含边框**）
        var lastCursorLine = 0;

        for (var i = 1; i <= 20; i++)
        {
            var text = InputLine.FormatInputText(tail, false, "", false, -1, 0, new string('x', i), 24, 76, null, colored: false);
            var seq = InputLine.BuildRedrawAnsi(text, lastInputLines, lastCursorLine, out var newLines);
            emitted.Append(seq);
            lastInputLines = newLines;
            lastCursorLine = 0; // 单行输入光标恒在第 0 行
        }

        // 首绘 1 个换行（边框与提示符之间），之后 20 次按键必须一个换行都不产生
        var totalNewlines = CountNewlines(emitted.ToString());
        Assert.Equal(1, totalNewlines);
    }

    [Fact]
    public void BackspacingNeverEmitsANewline()
    {
        var tail = Program.BuildInputTextTail(Prompt);
        var emitted = new System.Text.StringBuilder();
        var lastInputLines = 1;
        var lastCursorLine = 0;
        for (var i = 20; i >= 0; i--)
        {
            var text = InputLine.FormatInputText(tail, false, "", false, -1, 0, new string('x', i), 24, 76, null, colored: false);
            emitted.Append(InputLine.BuildRedrawAnsi(text, lastInputLines, lastCursorLine, out lastInputLines));
        }
        Assert.Equal(0, CountNewlines(emitted.ToString()));
    }

    [Fact]
    public void ArrowKeysAndEditingNeverEmitANewline()
    {
        // 上下键浏览历史会改变 text，但只要仍是单行就不该换行
        var tail = Program.BuildInputTextTail(Prompt);
        var lastInputLines = 1;
        foreach (var (idx, draft) in new[] { (0, "/help"), (1, ""), (0, "/model next"), (2, "x") })
        {
            var text = InputLine.FormatInputText(tail, false, "", false, idx, 3, draft, 24, 76, null, colored: false);
            var seq = InputLine.BuildRedrawAnsi(text, lastInputLines, 0, out lastInputLines);
            Assert.Equal(0, CountNewlines(seq));
        }
    }

    [Fact]
    public void SearchStateAlsoStaysOnOneLine()
    {
        // Ctrl+R 搜索态换了外观，但仍是单行输入
        var tail = Program.BuildInputTextTail(Prompt);
        foreach (var q in new[] { "", "g", "git", new string('g', 40) })
        {
            var text = InputLine.FormatInputText(tail, true, q, true, -1, 0, "", 24, 76, null, colored: false);
            var seq = InputLine.BuildRedrawAnsi(text, 1, 0, out _);
            Assert.Equal(0, CountNewlines(seq));
        }
    }

    [Fact]
    public void SingleLineSequence_ClearsAndRewritesInPlace()
    {
        var seq = InputLine.BuildRedrawAnsi(Prompt + "ls", 1, 0, out var lines);
        Assert.Equal(1, lines);
        Assert.StartsWith("\r\x1b[2K", seq);
        Assert.Equal(Prompt + "ls", seq[5..]); // "\r\x1b[2K" 占 5 个字符
    }

    [Fact]
    public void MultilinePasteStillEmitsNewlines()
    {
        // 反向保护：真正多行时仍然要逐行清写，不能被上面的"永远单行"改坏
        var text = "line1\nline2\nline3";
        var seq = InputLine.BuildRedrawAnsi(text, 1, 0, out var lines);
        Assert.Equal(3, lines);
        Assert.Equal(2, CountNewlines(seq));
        Assert.Contains("\x1b[2K", seq);
    }

    [Fact]
    public void ShrinkingFromMultilineClearsLeftoverRows()
    {
        // 从 3 行删回 1 行：按新旧最大值清，多余的行必须被清空而不是留着残字
        var seq = InputLine.BuildRedrawAnsi("x", 3, 2, out var lines);
        Assert.Equal(1, lines);
        Assert.Equal(2, CountNewlines(seq)); // 仍然写满 3 行（第 3 行清空）
        Assert.Equal(3, CountNewlines(seq) + 1);
    }

    [Fact]
    public void ZeroCursorLineEmitsNoCursorUp()
    {
        // \x1b[0A 会被多数终端当成上移 1 行，覆盖掉输入块上方的行
        var seq = InputLine.BuildRedrawAnsi("a\nb", 2, 0, out _);
        Assert.DoesNotContain("\x1b[0A", seq);
    }
}
