using System;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具状态行的动词形态（<c>Read(path=…)</c> 而不是 <c>read_file {"path":…}</c>）。
///
/// 回归点：snake_case 的**内部命名**混在面向用户的行里，一眼看不出这是在读文件
/// 还是在跑报告；而且工具名白白占掉 9 列，窄屏下同一终端能多显示的路径更短。
/// </summary>
public class ToolVerbTests
{
    [Theory]
    [InlineData("read_file", "Read")]
    [InlineData("edit_file", "Edit")]
    [InlineData("write_file", "Write")]
    [InlineData("multi_edit", "Edit")]
    [InlineData("grep", "Grep")]
    [InlineData("list_files", "Glob")]
    [InlineData("bash", "Bash")]
    [InlineData("web_fetch", "Fetch")]
    public void KnownToolsGetAVerb(string tool, string expected) =>
        Assert.Equal(expected, AgentClass.ToolVerb(tool));

    [Fact]
    public void MappingIsCaseInsensitive()
    {
        Assert.Equal("Read", AgentClass.ToolVerb("READ_FILE"));
        Assert.Equal("Edit", AgentClass.ToolVerb("Edit_File"));
    }

    [Fact]
    public void UnknownToolsAreNotGuessed()
    {
        // 猜错动词比显示原始名更糟——那会让人以为在执行**另一个**操作。
        Assert.Equal("Widget_count", AgentClass.ToolVerb("widget_count"));
        Assert.Equal("Zzz", AgentClass.ToolVerb("zzz"));
    }

    [Fact]
    public void EmptyNameIsReturnedUnchanged()
    {
        Assert.Equal("", AgentClass.ToolVerb(""));
    }

    [Fact]
    public void EveryMappedVerbIsShorterThanItsLongestTool()
    {
        // 省宽度是这个改动的一半价值：动词必须**不长于**它替代的工具名
        foreach (var (from, to) in AgentClass.VerbMap)
            Assert.True(to.Length <= from.Length, $"{to} 比 {from} 还长，白占宽度");
    }

    [Fact]
    public void SummaryUsesTheVerb()
    {
        var summary = AgentClass.SummarizeCall("read_file", """{"path":"a.cs"}""");
        Assert.Equal("Read(path=a.cs)", summary);
        Assert.DoesNotContain("read_file", summary);
    }

    [Fact]
    public void SummaryWithoutArgsIsJustTheVerb()
    {
        Assert.Equal("Grep", AgentClass.SummarizeCall("grep", "{}"));
        Assert.Equal("Grep", AgentClass.SummarizeCall("grep", "not json"));
    }

    [Fact]
    public void VerbSavesWidth()
    {
        // 省宽度是这个改动的一半价值：同一份内容，动词形态必须严格窄于原始名形态
        var args = """{"path":"src/CodeAgent/Program.cs"}""";
        var verb = AgentClass.SummarizeCall("read_file", args);
        var raw = "read_file(" + verb["Read(".Length..]; // 同一个参数体，只换回原始名
        Assert.True(TextUtil.DisplayWidth(verb) < TextUtil.DisplayWidth(raw),
            $"动词形态没有变窄: {TextUtil.DisplayWidth(verb)} vs {TextUtil.DisplayWidth(raw)}");
    }

    [Fact]
    public void StatusLineRendersTheVerbEndToEnd()
    {
        var summary = AgentClass.SummarizeCall("edit_file", """{"path":"a.cs"}""");
        var line = AgentClass.FormatToolStatusLine(summary, false, TimeSpan.FromMilliseconds(1200));
        Assert.StartsWith("  ✔ Edit(path=a.cs)", line);
        Assert.Contains("1.2s", line);
    }

    [Fact]
    public void ContentAndEnvAreStillOmitted()
    {
        // 换动词不能顺带放宽既有的脱敏/省略规则
        Assert.DoesNotContain("SECRET", AgentClass.SummarizeCall("write_file", """{"path":"a","content":"SECRET"}"""));
        Assert.DoesNotContain("SECRET", AgentClass.SummarizeCall("bash", """{"command":"x","env":{"K":"SECRET"}}"""));
    }

    [Fact]
    public void ErrorLinesUseTheVerbToo()
    {
        var line = AgentClass.FormatToolStatusLine("Bash(command=rm)", true, TimeSpan.FromMilliseconds(5));
        Assert.StartsWith("  ⚠ Bash(", line);
    }
}
