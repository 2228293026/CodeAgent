using System;
using System.Linq;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具输出预览的宽度约束。
/// 回归点：预览上限是 800 **字符**，而 800 个汉字占 1600 **列**——
/// 在 80 列终端上直接把屏幕冲烂；逐行硬折又会把源码行拦腰折断，
/// 看起来像语法错误。现在按**显示宽度**逐行裁，并注明有几行被裁。
/// </summary>
public class ToolOutputPreviewWidthTests
{
    private static string[] Lines(string s) => s.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        var text = "短的一行\n另一行";
        Assert.Equal(text, AgentClass.FormatToolOutputPreview(text, width: 0));
    }

    [Fact]
    public void CjkOutputIsBoundedByWidth()
    {
        var text = string.Concat(Enumerable.Repeat("编译错误：未找到符号 Foo", 60));
        var result = AgentClass.FormatToolOutputPreview(text, maxChars: AgentClass.ToolOutputPreviewChars, width: 40);
        foreach (var line in Lines(result))
            Assert.True(TextUtil.DisplayWidth(line) <= 40, $"超宽：{line}");
    }

    [Fact]
    public void EveryLineFitsForMixedContent()
    {
        var text = string.Join("\n", new[]
        {
            "short",
            "一行中文输出，用来占满宽度预算",
            "a mixed line with both 中文 and ASCII content that is long",
            "x",
        });
        foreach (var width in new[] { 10, 20, 40, 80 })
        {
            var result = AgentClass.FormatToolOutputPreview(text, maxChars: AgentClass.ToolOutputPreviewChars, width: width);
            foreach (var line in Lines(result))
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void ShortOutputIsNotAnnotated()
    {
        var text = "short";
        Assert.Equal(text, AgentClass.FormatToolOutputPreview(text, maxChars: 800, width: 80));
    }

    [Fact]
    public void WidthTruncationIsReported()
    {
        var text = string.Concat(Enumerable.Repeat("一行很长的中文输出内容", 40));
        var result = AgentClass.FormatToolOutputPreview(text, maxChars: AgentClass.ToolOutputPreviewChars, width: 30);
        // 窄屏上完整注记放不下，只保留最关键的事实：保留了多少行 / 一共多少行
        Assert.Contains("保留 1/1 行", result);
        // 被按宽度裁的行都带省略号
        Assert.EndsWith("…", Lines(result)[0]);
    }

    [Fact]
    public void CharCapStillReportsRealTotals()
    {
        // 提示里的「共 N 行」必须是**截断前**的真实行数
        var text = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"line {i}"));
        var result = AgentClass.FormatToolOutputPreview(text, maxChars: 50, width: 0);
        Assert.Contains("共 100 行", result);
        Assert.Contains("已保留", result);
    }

    [Fact]
    public void CharCapStillHappens()
    {
        var text = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"line {i}"));
        var result = AgentClass.FormatToolOutputPreview(text, maxChars: 50, width: 0);
        Assert.True(Lines(result).Length < 100, result);
    }

    [Fact]
    public void ZeroCharCapEmptiesEverything()
    {
        Assert.Equal(string.Empty, AgentClass.FormatToolOutputPreview("anything", maxChars: 0, width: 80));
    }

    [Fact]
    public void BothCapsApplyTogether()
    {
        var text = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"中文输出第 {i} 行很长很长"));
        var result = AgentClass.FormatToolOutputPreview(text, maxChars: 120, width: 30);
        foreach (var line in Lines(result))
            Assert.True(TextUtil.DisplayWidth(line) <= 30, $"超宽：{line}");
        // 注记独占一行：混进内容行会把那一行顶出宽度
        Assert.Contains("行)", result);
    }

    [Fact]
    public void PreviewWidthAccountsForIndent()
    {
        var columns = 80;
        Assert.Equal(columns - TextUtil.DisplayWidth(AgentClass.ToolPreviewIndent), AgentClass.ToolPreviewWidth(columns));
    }

    [Fact]
    public void PreviewWidthFollowsTheTerminal()
    {
        // 宽度真的取自终端，而不是写死的常数
        Assert.Equal(Program.Columns() - TextUtil.DisplayWidth(AgentClass.ToolPreviewIndent), AgentClass.ToolPreviewWidth(Program.Columns()));
    }
}
