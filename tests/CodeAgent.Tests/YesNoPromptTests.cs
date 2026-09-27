using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 是/否确认提示（放开沙箱、覆盖快照等）。
///
/// 此前是 <c>output.Write($"{Warn} {question} [y/N] ")</c>：
///   · <c>question</c> 由调用方拼装（<c>$"会话「{name}」已存在，覆盖？"</c>），长度不受控；
///   · 折行后 <c>[y/N]</c> 被顶走，用户在一行文字里找按键位置；
///   · **没说回车就是取消**——而 EOF/非 y 一律按取消处理，
///     用户不知道"直接回车"是安全的那一侧。
/// </summary>
public sealed class YesNoPromptTests
{
    [Fact]
    public void Prompt_KeepsTheQuestion()
    {
        Assert.Contains("覆盖", Program.FormatYesNoPrompt("覆盖？", 80));
    }

    [Fact]
    public void Prompt_NeverExceedsWidth()
    {
        var long1 = "会话「" + new string('长', 200) + "」已存在，覆盖？";
        foreach (var w in new[] { 8, 12, 20, 30, 40, 80, 120 })
        {
            var line = Program.FormatYesNoPrompt(long1, w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"w={w} 超宽 {TextUtil.DisplayWidth(line)}");
        }
    }

    [Fact]
    public void Prompt_NeverExceedsWidthWithShortQuestions()
    {
        foreach (var w in new[] { 4, 6, 8, 10, 20, 80 })
            Assert.True(TextUtil.DisplayWidth(Program.FormatYesNoPrompt("覆盖？", w)) <= w);
    }

    [Fact]
    public void Prompt_TellsTheUserHowToCancel()
    {
        var p = Program.FormatYesNoPrompt("覆盖？", 120);
        Assert.Contains(SafeColor.Glyphs.Escape, p);
        Assert.Contains("取消", p);
    }

    [Fact]
    public void Prompt_TellsTheUserHowToConfirm()
    {
        var p = Program.FormatYesNoPrompt("覆盖？", 120);
        Assert.Contains("y", p);
        Assert.Contains("确认", p);
    }

    [Fact]
    public void Prompt_DropsTheHintBeforeTheQuestionWhenTooNarrow()
    {
        // 极窄时两者放不下。**丢提示、留问题**——这是安全确认：
        // 用户看不到"要确认什么"就按 y，是真正危险的情形；
        // 而看不到按键提示只是稍微费解（y 是通用惯例，直接回车也安全=取消）。
        var narrow = Program.FormatYesNoPrompt("会话「很长很长很长」已存在，覆盖？", 24);
        Assert.DoesNotContain("取消", narrow);
        // 问题开头必须还在：24 列装不下全文，但开头正是"是什么操作"的关键信息
        Assert.Contains("会话", narrow);
        Assert.StartsWith(SafeColor.Glyphs.Warn, narrow);
    }

    [Fact]
    public void Prompt_KeepsTheHintOnceThereIsRoom()
    {
        // 宽度刚够时提示就该回来
        var p = Program.FormatYesNoPrompt("覆盖？", 60);
        Assert.Contains("取消", p);
        Assert.Contains("覆盖", p);
    }

    [Fact]
    public void Prompt_UnknownWidthShowsEverything()
    {
        var p = Program.FormatYesNoPrompt("会话「名字」已存在，覆盖？", 0);
        Assert.Contains("会话「名字」已存在，覆盖？", p);
    }

    [Fact]
    public void Prompt_UsesGlyphsNotHardcodedDecoration()
    {
        var p = Program.FormatYesNoPrompt("覆盖？", 120);
        Assert.DoesNotContain("[y/N]", p);
    }

    // —— 行为不变：只有明确 y 放行 ——

    [Fact]
    public void Replace_OnlyExplicitYProceeds()
    {
        Assert.True(Program.ConfirmReplace(Reader("y"), new StringWriter(), "覆盖？"));
        Assert.True(Program.ConfirmReplace(Reader("Y"), new StringWriter(), "覆盖？"));
        Assert.False(Program.ConfirmReplace(Reader("n"), new StringWriter(), "覆盖？"));
        Assert.False(Program.ConfirmReplace(Reader(""), new StringWriter(), "覆盖？"));
        Assert.False(Program.ConfirmReplace(Reader("yes"), new StringWriter(), "覆盖？"));
        // EOF：安全默认 = 取消
        Assert.False(Program.ConfirmReplace(Reader(null), new StringWriter(), "覆盖？"));
    }

    [Fact]
    public void FullAccess_OnlyExplicitYProceeds()
    {
        Assert.True(Program.ConfirmFullAccess(Reader("y"), new StringWriter()));
        Assert.False(Program.ConfirmFullAccess(Reader(""), new StringWriter()));
        Assert.False(Program.ConfirmFullAccess(Reader(null), new StringWriter()));
    }

    [Fact]
    public void FullAccess_AnnouncesTheCancel()
    {
        var sw = new StringWriter();
        Program.ConfirmFullAccess(Reader("n"), sw);
        Assert.Contains("取消", sw.ToString());
    }

    [Fact]
    public void BothConfirmPromptsShareOneRenderer()
    {
        // 两处各写一份格式的话，迟早一边改了另一边没改
        var src = File.ReadAllText(FindProgram());
        Assert.Equal(2, CountOccurrences(src, "FormatYesNoPrompt(") - 1); // 减去定义那一处
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static TextReader Reader(string? s) => new StringReader(s ?? "");

    private static string FindProgram()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
