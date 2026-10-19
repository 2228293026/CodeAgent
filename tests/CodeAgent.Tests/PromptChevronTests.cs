using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入提示符的终止符（Claude Code 同款 <c>❯</c>）。
///
/// 这个字符定义了 <c>SafeColor.Glyphs.PromptChevron</c> 却一直没人用——
/// <c>BuildPromptText</c> 里六处全部硬编码 <c>&quot;&gt; &quot;</c>。
/// 后果不是「少一个装饰」：CODEAGENT_ASCII 能换掉几乎所有框线与标记，
/// 唯独用户每分钟都在看的那一个字符换不掉。
///
/// 而它偏偏是最该能换的那个——ASCII 终端上 <c>❯</c> 会渲染成方块或问号。
/// </summary>
/// <remarks>
/// 在 <c>ConsoleOutput</c> 集合里：字形取自进程级可替换的 <c>SafeColor.ReadEnv</c>。
/// </remarks>
[Collection("ConsoleOutput")]
public class PromptChevronTests
{
    [Fact]
    public void UnicodeModeUsesTheChevron()
    {
        var text = Program.BuildPromptText("code", "m", "proj", 80);
        Assert.EndsWith("❯ ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("> ", text);
    }

    [Fact]
    public void AsciiModeUsesTheGreaterThanSign()
    {
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
            var text = Program.BuildPromptText("code", "m", "proj", 80);
            Assert.EndsWith("> ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("❯", text);
        }
        finally { SafeColor.ReadEnv = saved; }
    }

    [Fact]
    public void EveryDegradedFormEndsWithTheChevron()
    {
        // 降级路径有六七个分支，**每一支**都得走字形表。
        // 只改主路径的话，窄终端上会突然变回 >（而窄终端恰恰是 ASCII 兜底最常触发的场景）。
        foreach (var width in new[] { 12, 16, 20, 30, 40, 60, 80, 120 })
        {
            var text = Program.BuildPromptText("代码审查模式", new string('m', 100), new string('d', 100), width);
            Assert.True(text.EndsWith("❯ ", StringComparison.Ordinal) || text.EndsWith("❯", StringComparison.Ordinal) || text == "[",
                $"宽度 {width} 的降级形态没有走字形表 → [{text}]");
        }
    }

    [Fact]
    public void ChevronIsOneColumnInBothModes()
    {
        // 终止符占几列直接决定提示符预算算得对不对；差 1 列就会把输入行顶到折行
        Assert.Equal(1, TextUtil.DisplayWidth("❯"));
        Assert.Equal(1, TextUtil.DisplayWidth(">"));
    }

    [Fact]
    public void SourceNeverHardcodesThePromptTerminator()
    {
        // 源码守卫：防止有人再次在 BuildPromptText 里手打 "> "。
        // 那条路径上任何一处硬编码，都会在 ASCII 模式下露出一个方块。
        var src = ReadSource("Program.cs");
        var at = src.IndexOf("internal static string BuildPromptText", StringComparison.Ordinal);
        Assert.True(at > 0);
        var body = src.Substring(at, Math.Min(3000, src.Length - at));
        Assert.Contains("SafeColor.Glyphs.PromptChevron", body);
    }

    private static string ReadSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = System.IO.Path.Combine(dir, "src", "CodeAgent", file);
                if (System.IO.File.Exists(candidate) && new System.IO.FileInfo(candidate).Length > 1000)
                    return System.IO.File.ReadAllText(candidate);
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException($"找不到 src/CodeAgent/{file}");
    }
}
