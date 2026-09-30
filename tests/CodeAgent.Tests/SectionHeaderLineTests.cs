using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 小节标题行与提示行的宽度收口。
///
/// <c>/providers</c>、<c>/resume</c>、<c>/session</c> 的标题/说明此前都是裸插值：
/// 40 列终端上实测 47~60 列，整行硬折行。
///
/// 它们躲过了仓库级"用户内容会超宽"扫描：括号里是**固定文案**，
/// 只有个别的名字/计数是变量——扫描器按设计只盯用户可控内容。
/// 但<b>固定文案一样会长过窄屏</b>，所以要在这里单独收口。
/// </summary>
public sealed class SectionHeaderLineTests
{
    [Fact]
    public void FullFormAtGenerousWidth()
    {
        Assert.Equal("已配置的 Provider（当前: custom，-p <名> 或改 provider 切换）:",
            Program.FormatSectionHeaderLine("已配置的 Provider", "当前: custom，-p <名> 或改 provider 切换", 120));
    }

    [Fact]
    public void TooNarrow_DropsTheNoteButKeepsTheTitle()
    {
        // 用户是奔着这个标题点进来的，标题必须留下
        Assert.Equal("已配置的 Provider:",
            Program.FormatSectionHeaderLine("已配置的 Provider", "当前: custom，-p <名> 或改 provider 切换", 40));
    }

    [Fact]
    public void EmptyNote_JustTheTitle()
    {
        Assert.Equal("最近的会话:", Program.FormatSectionHeaderLine("最近的会话", null, 80));
        Assert.Equal("最近的会话:", Program.FormatSectionHeaderLine("最近的会话", "   ", 80));
    }

    [Fact]
    public void UnknownWidthKeepsEverything()
    {
        // width<=0 = 宽度未知，不裁剪（这条语义反复被栽）
        var note = "当前: custom，-p <名> 或改 provider 切换";
        Assert.Equal($"已配置的 Provider（{note}）:",
            Program.FormatSectionHeaderLine("已配置的 Provider", note, 0));
    }

    [Fact]
    public void NeverExceedsTheBudget()
    {
        foreach (var w in new[] { 4, 8, 12, 16, 24, 40, 60, 80, 120 })
        {
            var line = Program.FormatSectionHeaderLine(
                "已配置的 Provider", "当前: custom，-p <名> 或改 provider 切换", w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"宽度 {w} 超宽：{line}");
        }
    }

    [Fact]
    public void IndentIsCounted()
    {
        var line = Program.FormatSectionHeaderLine("最近的会话", "很长很长很长很长很长很长", 40, indent: true);
        Assert.StartsWith("  ", line);
        Assert.True(TextUtil.DisplayWidth(line) <= 40, line);
    }

    [Fact]
    public void AllFourCallSitesAreRouted()
    {
        // 只测纯函数会漏掉"这四处压根没用它"——那正是原来溢出的原因
        var code = StripComments(File.ReadAllText(FindSource()));
        Assert.Equal(3, Occurrences(code, "FormatSectionHeaderLine(")); // 定义 + /resume + /providers
        Assert.DoesNotContain("Console.WriteLine(\"最近的会话（输入 /resume", code);
        Assert.DoesNotContain("Console.WriteLine($\"已配置的 Provider（当前:", code);
        Assert.True(Occurrences(code, "FormatHintLine(") >= 2, "两处提示行应走 FormatHintLine");
    }

    [Fact]
    public void TheSessionPathStaysWhole()
    {
        // /session 的全部意义就是把路径交给用户去复制/贴进工单：截掉尾巴只剩一个用不了的半截。
        // 这条不是"忘了收口"，是**刻意不收口**——写进注释并用测试钉住，
        // 免得将来"把所有输出都收口"的一轮把它顺手改坏。
        var code = StripComments(File.ReadAllText(FindSource()));
        Assert.DoesNotContain("FormatHintLine(agent.SessionPath", code);
        Assert.DoesNotContain("FormatResultLine(agent.SessionPath", code);
        var commented = File.ReadAllText(FindSource());
        Assert.Contains("路径**故意不做宽度收口**", commented);
    }

    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var raw in src.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            var at = line.IndexOf("///", StringComparison.Ordinal);
            if (at >= 0)
                line = line[..at];
            else
            {
                var at2 = line.IndexOf("//", StringComparison.Ordinal);
                if (at2 >= 0)
                    line = line[..at2];
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string FindSource()
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
