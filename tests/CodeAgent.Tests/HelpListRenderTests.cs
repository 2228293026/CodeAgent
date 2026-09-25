using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /help 的宽度自适应渲染。
/// 回归点：命令清单此前是手对齐的固定列。窄终端下说明文字直接交给终端硬折行——
/// 词中间被断开、缩进丢失，续行看起来像另一条内容。
/// </summary>
public class HelpListRenderTests
{
    [Fact]
    public void WrapDisplay_UnknownWidth_ReturnsUnchanged()
    {
        Assert.Equal("原样返回的一行文字", TextUtil.WrapDisplay("原样返回的一行文字", 0));
    }

    [Fact]
    public void WrapDisplay_SplitsAsciiWordsOnSpaces()
    {
        var wrapped = TextUtil.WrapDisplay("aaa bbb ccc ddd eee", 8);
        var lines = wrapped.Split('\n');
        Assert.All(lines, l => Assert.True(TextUtil.DisplayWidth(l) <= 8));
        Assert.Equal("aaa bbb", lines[0]);
    }

    [Fact]
    public void WrapDisplay_BreaksBetweenCjkCharacters()
    {
        // 中文没有空格，按字断行才是正确排版（每个汉字 2 列，宽 8 = 每行 4 字）
        var wrapped = TextUtil.WrapDisplay("中文排版需要按字断行", 8);
        var lines = wrapped.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("中文排版", lines[0]);
        Assert.Equal("需要按字", lines[1]);
        Assert.Equal("断行", lines[2]);
    }

    [Fact]
    public void WrapDisplay_KeepsSurrogatePairsIntact()
    {
        var wrapped = TextUtil.WrapDisplay("😀😀😀😀😀", 4);
        foreach (var line in wrapped.Split('\n'))
            Assert.Equal(0, line.Length % 2);
        Assert.DoesNotContain("\ud83d", wrapped);
    }

    [Fact]
    public void WrapDisplay_IndentsEveryLine()
    {
        var wrapped = TextUtil.WrapDisplay("aaa bbb ccc ddd", 6, "    ");
        Assert.All(wrapped.Split('\n'), l => Assert.StartsWith("    ", l));
    }

    [Fact]
    public void WrapDisplay_HardSplitsWordLongerThanLine()
    {
        var wrapped = TextUtil.WrapDisplay(new string('x', 30), 10);
        Assert.All(wrapped.Split('\n'), l => Assert.True(l.Length <= 10));
        Assert.Equal(30, wrapped.Replace("\n", string.Empty).Length);
    }

    [Fact]
    public void WrapDisplay_HardSplitKeepsSurrogatePairsIntact()
    {
        // 宽度 3 装不下一个 emoji（2 列）+ 下一个：必须整对保留，不得出现半个代理对
        var wrapped = TextUtil.WrapDisplay(string.Concat(Enumerable.Repeat("\U0001F600", 4)), 3);
        Assert.All(wrapped.Split('\n'), l =>
        {
            Assert.True(TextUtil.DisplayWidth(l) <= 3);
            Assert.Equal(0, l.Length % 2);
        });
        Assert.Equal(4, wrapped.Split('\n').Sum(l => l.Length / 2));
    }

    [Fact]
    public void TakeDisplayWidth_KeepsPairsAndRespectsBudget()
    {
        Assert.Equal("中", TextUtil.TakeDisplayWidth("中文", 2));
        Assert.Equal("中文", TextUtil.TakeDisplayWidth("中文", 4));
        // 宽 3 装不下第二个全角字（2+2=4）：宁可留 1 列空隙，也不把字拆成半个
        Assert.Equal("中", TextUtil.TakeDisplayWidth("中文", 3));
        Assert.Equal("😀", TextUtil.TakeDisplayWidth("😀😀", 2));
        Assert.Equal(string.Empty, TextUtil.TakeDisplayWidth("😀", 1));
        Assert.Equal(string.Empty, TextUtil.TakeDisplayWidth("abc", 0));
    }

    [Fact]
    public void FormatHelpList_WideKeepsTwoColumns()
    {
        var entries = new[] { new Program.HelpEntry("/a", "短说明"), new Program.HelpEntry("/bb", "短说明") };
        var lines = FormatHelpList(entries, 80);
        Assert.Contains("  /a   短说明", lines);
    }

    [Fact]
    public void FormatHelpList_NarrowMovesDescriptionToOwnLine()
    {
        var entries = new[] { new Program.HelpEntry("/x", "这是一条很长的说明文字，用于验证窄终端下的换行行为是否正确") };
        var lines = FormatHelpList(entries, 40);
        var rows = lines.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("  /x", rows[0]);
        Assert.StartsWith("      ", rows[1]);
        Assert.All(rows, l => Assert.True(TextUtil.DisplayWidth(l) <= 40, $"溢出: {l}"));
        Assert.Contains("这是一条很长的说明文字", lines);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(80)]
    [InlineData(120)]
    public void FormatHelpList_NeverExceedsWidth(int width)
    {
        var lines = FormatHelpList(Program.ReplCommands, width);
        foreach (var row in lines.Replace("\r\n", "\n").Split('\n'))
            Assert.True(TextUtil.DisplayWidth(row) <= width, $"宽度 {width} 溢出: {row}");
    }

    [Fact]
    public void FormatHelpList_UnknownWidth_StaysSingleColumn()
    {
        var lines = FormatHelpList(Program.ReplCommands, 0);
        Assert.DoesNotContain("\n    ", lines); // 没有缩进续行 = 未知宽度时不猜测折行
        Assert.Contains("/export [名/编号/all]", lines);
    }

    [Fact]
    public void FormatHelpList_EmptyInputReturnsEmpty() =>
        Assert.Equal(string.Empty, FormatHelpList(Array.Empty<Program.HelpEntry>(), 80));

    [Fact]
    public void FormatHelpList_HandlesLongToolNames()
    {
        // /tools 的真实形状：工具名很长，说明是中文
        var entries = new[]
        {
            new Program.HelpEntry("git_executable_bit_report", "只读检查已跟踪文件的可执行位设置"),
            new Program.HelpEntry("read_file", "读取文件内容并按显示宽度编号"),
        };
        var lines = FormatHelpList(entries, 80);
        Assert.Contains("git_executable_bit_report", lines);
        Assert.Contains("只读检查已跟踪文件的可执行位设置", lines);
    }

    private static List<Program.HelpEntry> RealToolEntries() =>
        ToolRegistry.CreateDefault().ToToolSpecs()
            .Select(t => new Program.HelpEntry(t.Name, t.Description))
            .OrderBy(e => e.Command, StringComparer.Ordinal)
            .ToList();

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(140)]
    public void FormatToolList_RealRegistryShapeNeverOverflows(int width)
    {
        // 用真实工具名/说明形状压测：长名 + 中文说明
        var lines = FormatHelpList(RealToolEntries(), width);
        Assert.NotEmpty(lines);
        foreach (var row in lines.Replace("\r\n", "\n").Split('\n'))
            Assert.True(TextUtil.DisplayWidth(row) <= width, $"宽度 {width} 溢出: {row}");
    }

    [Fact]
    public void FormatToolList_KeepsEveryToolNamePresent()
    {
        var specs = ToolRegistry.CreateDefault().ToToolSpecs();
        var lines = FormatHelpList(
            specs.Select(t => new Program.HelpEntry(t.Name, t.Description)).ToList(), 40);
        foreach (var spec in specs)
            Assert.Contains(spec.Name, lines);
    }

    [Fact]
    public void FormatHelpList_TruncatesCommandNarrowerThanName()
    {
        // 命令名比终端还宽时必须截断，否则该行硬折行会打散整列表的网格
        var entries = new[] { new Program.HelpEntry("dependency_lockfile_report", "只读检查依赖锁文件") };
        var lines = FormatHelpList(entries, 24);
        foreach (var row in lines.Replace("\r\n", "\n").Split('\n'))
            Assert.True(TextUtil.DisplayWidth(row) <= 24, $"溢出: {row}");
        Assert.Contains("…", lines);
    }

    [Fact]
    public void ModeListText_UsesWidthAwareRendererAndKeepsMarker()
    {
        // /mode 列表与 /help、/tools 走同一渲染器：中文说明在窄终端必须折行且不溢出
        var lines = Program.ModeListText(new AgentConfig(), "plan");
        var rows = lines.Replace("\r\n", "\n").Split('\n');
        Assert.Contains(rows, l => l.StartsWith("  plan") && l.EndsWith("←"));
        foreach (var width in new[] { 30, 50, 80, 120 })
        {
            var rendered = Program.FormatHelpList(
                Program.ReplCommands, width);
            foreach (var row in rendered.Replace("\r\n", "\n").Split('\n'))
                Assert.True(TextUtil.DisplayWidth(row) <= width, $"宽度 {width} 溢出: {row}");
        }
    }

    [Fact]
    public void FormatHelpList_ProviderShapeWithLongBaseUrlWraps()
    {
        // /providers 的真实形状：名称列 + 含长 URL 的说明
        var entries = new[]
        {
            new Program.HelpEntry("openai", "(openai) 模型: gpt-5  baseUrl: https://api.openai.com/v1"),
            new Program.HelpEntry("local", "(openai-compatible) 模型: qwen  baseUrl: http://127.0.0.1:11434/v1  ←"),
        };
        var lines = FormatHelpList(entries, 40);
        foreach (var row in lines.Replace("\r\n", "\n").Split('\n'))
            Assert.True(TextUtil.DisplayWidth(row) <= 40, $"溢出: {row}");
        Assert.Contains("api.openai.com", lines);
    }

    [Fact]
    public void ReplCommands_AreUniqueAndNonEmpty()
    {
        var commands = Program.ReplCommands.Select(c => c.Command).ToList();
        Assert.Equal(commands.Count, commands.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Program.ReplCommands, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Command));
            Assert.False(string.IsNullOrWhiteSpace(e.Description));
            Assert.StartsWith("/", e.Command);
        });
    }

    private static string FormatHelpList(IReadOnlyList<Program.HelpEntry> entries, int width) =>
        Program.FormatHelpList(entries, width);
}
