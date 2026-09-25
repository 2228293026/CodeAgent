using System;
using CodeAgent;
using CodeAgent.Agent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具状态行的参数排版。
/// 回归点：①参数值按**字符数**截 60，60 个汉字实际占 120 列；
/// ②行超宽时直接按显示宽度硬截，会把参数劈成半截（path=C:\Users\very\lo）——
/// 那看起来像一个真实路径，比截断本身更危险。
/// </summary>
public class ToolStatusArgTests
{
    [Fact]
    public void SummarizeCall_TruncatesByDisplayWidthNotCharCount()
    {
        var json = "{\"path\":\"" + new string('中', 100) + "\"}";
        var summary = AgentClass.SummarizeCall("read_file", json, 20);
        var value = summary["read_file(path=".Length..^1];
        // 20 列最多装 10 个汉字；按字符数截 60 会装 60 个汉字 = 120 列
        Assert.True(TextUtil.DisplayWidth(value) <= 22, $"值超预算: {TextUtil.DisplayWidth(value)} 列");
        Assert.Contains("…", value);
    }

    [Fact]
    public void SummarizeCall_DefaultWidthStillCapsValue()
    {
        var json = "{\"path\":\"" + new string('x', 500) + "\"}";
        var summary = AgentClass.SummarizeCall("read_file", json);
        Assert.True(summary.Length < 120, summary);
    }

    [Fact]
    public void SummarizeCall_SmallArgumentSurvivesUntouched()
    {
        var summary = AgentClass.SummarizeCall("read_file", "{\"path\":\"docs/a.md\"}", 60);
        Assert.Equal("read_file(path=docs/a.md)", summary);
    }

    [Fact]
    public void TrimToolSummaryArgs_DropsWholeArgsNotHalves()
    {
        var summary = "run_command(command=dotnet test -c Release max_results=50 timeout_seconds=300)";
        var trimmed = AgentClass.TrimToolSummaryArgs(summary, 40);
        Assert.Contains("…+", trimmed); // 标注省略
        Assert.True(TextUtil.DisplayWidth(trimmed) <= 40, $"超宽: {trimmed}");
        // 保留的参数必须完整：不能出现 path=C:\Users\very 这种半截值
        foreach (var arg in trimmed[(trimmed.IndexOf('(') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Assert.DoesNotContain("=…", arg);
    }

    [Fact]
    public void TrimToolSummaryArgs_FitsSummaryIsUnchanged()
    {
        var summary = "read_file(path=docs/a.md)";
        Assert.Equal(summary, AgentClass.TrimToolSummaryArgs(summary, 80));
    }

    [Fact]
    public void TrimToolSummaryArgs_UnknownBudgetIsUnchanged()
    {
        var summary = "run_command(command=dotnet test max_results=50)";
        Assert.Equal(summary, AgentClass.TrimToolSummaryArgs(summary, 0));
    }

    [Fact]
    public void TrimToolSummaryArgs_NonArgSummaryFallsBackToFit()
    {
        var trimmed = AgentClass.TrimToolSummaryArgs(new string('x', 200), 30);
        Assert.True(TextUtil.DisplayWidth(trimmed) <= 30, $"超宽: {trimmed}");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void FormatToolStatusLine_NeverExceedsWidth(int width)
    {
        var summary = "run_command(command=dotnet test -c Release max_results=50 timeout_seconds=300)";
        var line = AgentClass.FormatToolStatusLine(summary, false, TimeSpan.FromSeconds(1.5), width);
        Assert.True(TextUtil.DisplayWidth(line) <= width, $"宽度 {width} 溢出: {line}");
    }

    [Fact]
    public void FormatToolStatusLine_KeepsMarkerAndDuration()
    {
        var line = AgentClass.FormatToolStatusLine("read_file(path=a.md)", true, TimeSpan.FromMilliseconds(120), 80);
        Assert.StartsWith("  ⚠ ", line);
        Assert.Contains("read_file(path=a.md)", line);
    }

    [Fact]
    public void FormatToolStatusLine_UnknownWidthKeepsFullSummary()
    {
        var summary = "run_command(command=x max_results=1 timeout_seconds=2)";
        var line = AgentClass.FormatToolStatusLine(summary, false, TimeSpan.FromSeconds(1), 0);
        Assert.Contains(summary, line);
    }
}
