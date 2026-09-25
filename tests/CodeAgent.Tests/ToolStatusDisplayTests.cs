using System;
using System.Linq;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具结果行与输出预览。
/// 回归点：失败路径的输出预览此前没有任何上限——24,000 字符的编译错误会整屏刷掉对话与输入行，
/// 而成功路径只显示 800 字符。同一个工具，失败时反而最看不清。
/// </summary>
public class ToolStatusDisplayTests
{
    [Fact]
    public void FormatToolStatusLine_SuccessAndErrorDiffer()
    {
        var ok = AgentClass.FormatToolStatusLine("read_file {\"path\":\"a.cs\"}", false, TimeSpan.FromMilliseconds(120));
        var bad = AgentClass.FormatToolStatusLine("read_file {\"path\":\"a.cs\"}", true, TimeSpan.FromMilliseconds(120));
        Assert.StartsWith("  ✔ ", ok);
        Assert.StartsWith("  ⚠ ", bad);
        Assert.Contains("(", ok);
        Assert.Contains(")", ok);
    }

    [Fact]
    public void FormatToolStatusLine_TruncatesToTerminalWidth()
    {
        var summary = new string('x', 200);
        var line = AgentClass.FormatToolStatusLine(summary, false, TimeSpan.FromSeconds(1), 40);
        Assert.True(TextUtil.DisplayWidth(line) <= 40);
        // 耗时属于固定框架，不该为参数文本让位：旧实现把整行硬截，耗时被吃掉，
        // 窄屏下反而看不出这步花了多久。现在先丢参数，耗时始终保留。
        Assert.EndsWith("(1.0s)", line);
    }

    [Fact]
    public void FormatToolOutputPreview_TruncatesOnLineBoundary()
    {
        // 800 字符预算落在多行文本中间：必须停在整行，不能留半行让人误以为那就是完整的一行
        var lines = Enumerable.Range(1, 500).Select(i => $"error CS{i:D4}: 某处的说明文字").ToList();
        var text = string.Join("\n", lines);
        var preview = AgentClass.FormatToolOutputPreview(text);
        var body = preview[..preview.IndexOf("…（共", StringComparison.Ordinal)];
        var kept = body.Split('\n');
        // 保留的每一行都必须与源文本的整行逐字相同（没有半截行）
        for (var i = 0; i < kept.Length; i++)
            Assert.Equal(lines[i], kept[i]);
        Assert.True(kept.Length < lines.Count, "应当发生了截断");
    }

    [Fact]
    public void FormatToolOutputPreview_ReportsLineCounts()
    {
        var text = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"error CS{i:D4}: 说明"));
        var preview = AgentClass.FormatToolOutputPreview(text);
        Assert.Contains("共 500 行", preview);
        Assert.Contains("已保留", preview);
    }

    [Fact]
    public void FormatToolOutputPreview_NeverSplitsSurrogatePair()
    {
        // 预算正好切在 emoji 的高代理项上
        var text = new string('a', 30) + "\U0001F600" + new string('b', 100);
        var preview = AgentClass.FormatToolOutputPreview(text, 32);
        // 预览里不应出现落单的代理项
        for (var i = 0; i < preview.Length; i++)
        {
            if (!char.IsHighSurrogate(preview[i]))
                continue;
            Assert.True(i + 1 < preview.Length && char.IsLowSurrogate(preview[i + 1]), "落单的高代理项");
        }
    }

    [Fact]
    public void FormatToolOutputPreview_SingleLongLineStillTruncates()
    {
        var text = new string('x', 2000);
        var preview = AgentClass.FormatToolOutputPreview(text);
        Assert.Contains("共 1 行", preview);
        Assert.Contains("已保留 1 行", preview);
    }

    [Fact]
    public void FormatToolOutputPreview_ShortTextUnchanged()
    {
        Assert.Equal("ok", AgentClass.FormatToolOutputPreview("ok"));
    }

    [Fact]
    public void FormatToolOutputPreview_TruncatesAndReportsOriginalLength()
    {
        var text = new string('y', 24_000);
        var preview = AgentClass.FormatToolOutputPreview(text);
        Assert.True(preview.Length < text.Length);
        Assert.Contains("24,000 字符", preview);
        // 措辞从"已截断"改为"共 N 行 / 已保留 N 行"：多行输出更需要知道丢了多少行
        Assert.EndsWith("）", preview);
        Assert.Contains("已保留 1 行", preview);
    }

    [Fact]
    public void FormatToolOutputPreview_ZeroBudgetReturnsEmpty()
    {
        Assert.Equal("", AgentClass.FormatToolOutputPreview("abc", 0));
        Assert.Equal("", AgentClass.FormatToolOutputPreview("abc", -1));
    }

    [Fact]
    public void ToolOutputPreviewChars_IsSharedByBothPaths() =>
        Assert.Equal(800, AgentClass.ToolOutputPreviewChars);

    [Fact]
    public void FailurePreview_IsBoundedLikeSuccessPath()
    {
        // 失败路径经过 8 行 + 字符预算双重收敛，最坏情况不会再整屏刷出
        var raw = string.Join('\n', Enumerable.Range(0, 500).Select(i => new string((char)('a' + i % 26), 200)));
        var preview = AgentClass.FormatToolOutputPreview(AgentClass.BuildToolOutputPreview(raw));
        Assert.True(preview.Length <= AgentClass.ToolOutputPreviewChars + 40);
    }
}
