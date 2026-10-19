using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// REPL 输入框（Claude Code 风格的上下边框 + 右上角档位提示）。
///
/// 回归点：此前提示符是**裸露**的一行，上一轮输出与它之间没有任何视觉分界，
/// 长会话里根本分不清「哪行是我刚打的」「哪行是模型刚说的」。
/// </summary>
/// <remarks>
/// 必须在 <c>ConsoleOutput</c> 集合里：框线与标记取自 <c>SafeColor.Glyphs</c>，
/// 它读的是**进程级**可替换的 <c>SafeColor.ReadEnv</c>；不串行时会被别的类
/// 正在跑的 ASCII 作用域污染（单跑全过、全量并行跑挂）。
/// </remarks>
[Collection("ConsoleOutput")]
public class InputFrameTests
{
    /// <summary>把行按显示宽度拆开，跨平台稳定（不依赖换行符）。</summary>
    private static string[] Lines(string s) => s.Replace("\r\n", "\n").Split('\n');

    private static string ReadSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return File.ReadAllText(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }

    [Fact]
    public void TopFrameIsAFrameLineThenThePrompt()
    {
        var text = Program.BuildInputFrameTop("[code] D:\\P> ", "◈ high · /thinking", 60);
        var lines = Lines(text);
        Assert.Equal(2, lines.Length);
        // 输入行带左侧竖线（输入行的左边框，与内容同行，必须参与重绘）
        Assert.Equal(SafeColor.Glyphs.FrameSide + " [code] D:\\P> ", lines[1]);
        // 上边框是满宽的**圆角**框线（右侧挂了档位提示，所以不是纯规则）
        Assert.Equal(60, TextUtil.DisplayWidth(lines[0]));
        Assert.Matches("^╭─+ ◈ high · /thinking╮$", lines[0]);
    }

    [Fact]
    public void RightHintSitsAtTheFarRightAndTheLineStaysInBudget()
    {
        const string hint = "◈ high · /thinking";
        var text = Program.BuildInputFrameTop("> ", hint, 60);
        var top = Lines(text)[0];
        Assert.Equal(60, TextUtil.DisplayWidth(top));
        // 提示贴着右边的圆角（`╮` 占走最后 1 列）
        Assert.EndsWith(" " + hint + SafeColor.Glyphs.FrameTopRight, top);
    }

    [Fact]
    public void HintIsMeasuredNotAssumedOneColumnPerChar()
    {
        // ◈ 恰好 1 列，但 /thinking 全是 ASCII；如果哪天真换成 CJK 档位名，
        // 这里的断言会立刻抓到「按 Length 算」的实现（线会超宽）。
        var hint = Program.InputModeHint("高");
        var top = Lines(Program.BuildInputFrameTop("> ", hint, 60))[0];
        Assert.Equal(60, TextUtil.DisplayWidth(top));
        // 提示必须**整段**出现在右端：按显示宽度实现时 width 恰好 60，
        // 按 Length 实现时会超宽，上面那条断言先炸。
        Assert.EndsWith(" " + hint + SafeColor.Glyphs.FrameTopRight, top);
    }

    [Fact]
    public void NTooNarrowForTheHintKeepsTheFrameAndDropsTheHint()
    {
        // 关键：提示放不下时整段省略，而不是把线画超宽
        var top = Lines(Program.BuildInputFrameTop("> ", "◈ high · /thinking", 10))[0];
        Assert.True(TextUtil.DisplayWidth(top) <= 10, $"上边框超宽: {TextUtil.DisplayWidth(top)}");
        Assert.DoesNotContain("/thinking", top);
    }

    [Fact]
    public void UnknownWidthOmitsTheFrameEntirely()
    {
        // 宽度未知时不猜：宁可少一条线，也不能画一条必然越界的线
        Assert.Equal("> ", Program.BuildInputFrameTop("> ", "◈ high", 0));
        Assert.Equal(string.Empty, Program.BuildInputFrameBottom(0));
    }

    [Fact]
    public void BottomFrameIsAFullWidthRoundedRule()
    {
        var bottom = Program.BuildInputFrameBottom(60);
        Assert.Equal(60, TextUtil.DisplayWidth(bottom));
        // 与上边框同形：左右圆角夹满宽横线（上下不等宽的框会歪）
        Assert.Matches("^╰─+╯$", bottom);
    }

    [Fact]
    public void AsciiModeUsesTheAsciiRuleAndMark()
    {
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
            var top = Lines(Program.BuildInputFrameTop("> ", Program.InputModeHint("high"), 60))[0];
            Assert.DoesNotContain("─", top);
            // ASCII 下圆角落 `+`、横线落 `-`
            Assert.Matches("^\\+-+ # high \\| /thinking\\+$", top);
            Assert.Matches("^\\+-+\\+$", Program.BuildInputFrameBottom(60));
        }
        finally { SafeColor.ReadEnv = saved; }
    }

    [Fact]
    public void LeadingBlankLineStaysAboveTheFrameNotInsideIt()
    {
        // 用户截图里的形状：一条框线、一行空白、再一行提示符——框看起来是破的。
        // 成因是 PromptFor 返回的提示符自带前导 `\n`（那本该是「输出」与「输入框」
        // 之间的空行），被原样夹进了「边框」与「提示符」中间。
        var framed = Program.BuildInputFrameTop("\n[code] D:\\P> ", "◈ high", 60);
        var lines = Lines(framed);
        Assert.Equal(3, lines.Length);
        Assert.Equal(string.Empty, lines[0]);          // 空行在最上面
        Assert.StartsWith("╭", lines[1], StringComparison.Ordinal); // 然后是边框
        Assert.Equal(SafeColor.Glyphs.FrameSide + " [code] D:\\P> ", lines[2]); // 提示符紧跟边框
    }

    [Fact]
    public void EveryWidthFromZeroUpwardsStaysInBudget()
    {
        // 圆角占 2 列，窄到极限时曾多画 1 列（width=8 出 9 列）。
        // 这里把 0..80 全扫一遍：越界一次就够把终端撑折行。
        for (var w = 0; w <= 80; w++)
        {
            var bottom = Program.BuildInputFrameBottom(w);
            Assert.True(TextUtil.DisplayWidth(bottom) <= Math.Max(0, w),
                $"宽度 {w}：下边框 {TextUtil.DisplayWidth(bottom)} 列越界 → [{bottom}]");
            var framed = Program.BuildInputFrameTop("\n> ", "◈ high · /thinking", w);
            foreach (var line in Lines(framed))
                Assert.True(TextUtil.DisplayWidth(line) <= Math.Max(0, w) || w == 0,
                    $"宽度 {w}：上边框行 {TextUtil.DisplayWidth(line)} 列越界 → [{line}]");
        }
    }

    [Fact]
    public void EmptyEffortProducesNoHint()
    {
        Assert.Equal(string.Empty, Program.InputModeHint(""));
        Assert.Equal(string.Empty, Program.InputModeHint(null!));
    }

    [Fact]
    public void TheLoopActuallyDrawsBothBorders()
    {
        // 源码守卫：只测 BuildInputFrameTop 会漏掉「接进 REPL 循环」这一步，
        // 而那正是用户唯一能看到的地方。
        var source = ReadSource();
        Assert.Contains("BuildInputFrameTop(", source);
        Assert.Contains("BuildInputFrameBottom(", source);
        // 原地重绘时不能重复画上边框
        Assert.Matches(@"(?s)var framed = inlinePrompt is null;.*if \(framed\)\s*\n", source);
    }
}
