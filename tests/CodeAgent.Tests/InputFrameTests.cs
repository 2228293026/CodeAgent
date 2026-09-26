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
        Assert.Equal("[code] D:\\P> ", lines[1]);
        // 上边框是满宽的规则线（右侧挂了档位提示，所以不是纯规则）
        Assert.Equal(60, TextUtil.DisplayWidth(lines[0]));
        Assert.Matches("^─+ ◈ high · /thinking$", lines[0]);
    }

    [Fact]
    public void RightHintSitsAtTheFarRightAndTheLineStaysInBudget()
    {
        const string hint = "◈ high · /thinking";
        var text = Program.BuildInputFrameTop("> ", hint, 60);
        var top = Lines(text)[0];
        Assert.Equal(60, TextUtil.DisplayWidth(top));
        Assert.EndsWith(" " + hint, top);
    }

    [Fact]
    public void HintIsMeasuredNotAssumedOneColumnPerChar()
    {
        // ◈ 恰好 1 列，但 /thinking 全是 ASCII；如果哪天真换成 CJK 档位名，
        // 这里的断言会立刻抓到「按 Length 算」的实现（线会超宽）。
        var hint = Program.InputModeHint("高");
        var top = Lines(Program.BuildInputFrameTop("> ", hint, 60))[0];
        Assert.Equal(60, TextUtil.DisplayWidth(top));
        Assert.Equal(hint, top[(TextUtil.DisplayWidth(top) - TextUtil.DisplayWidth(hint))..].TrimStart());
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
    public void BottomFrameIsAFullWidthRule()
    {
        Assert.Equal(60, TextUtil.DisplayWidth(Program.BuildInputFrameBottom(60)));
        Assert.Matches("^─+$", Program.BuildInputFrameBottom(60));
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
            Assert.Matches("^-+ # high \\| /thinking$", top);
            Assert.Matches("^-+$", Program.BuildInputFrameBottom(60));
        }
        finally { SafeColor.ReadEnv = saved; }
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
