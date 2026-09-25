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
        Assert.EndsWith("已截断）", preview);
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
