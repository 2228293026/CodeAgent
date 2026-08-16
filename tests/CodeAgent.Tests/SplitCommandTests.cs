using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class SplitCommandTests
{
    [Fact]
    public void CommandWithArgs_SplitsAtFirstSpace()
    {
        var (cmd, rest) = Program.SplitCommand("/model gpt-4o");
        Assert.Equal("/model", cmd);
        Assert.Equal("gpt-4o", rest);
    }

    [Theory]
    [InlineData("\u001bCANCELLED_TURN", true)]                        // 取消哨兵
    [InlineData("已取消。", false)]                                    // 回归：模型回复含"已取消"字样不应误判为取消
    [InlineData("该操作已取消，请重试。", false)]                       // 含"已取消"子串的普通回复
    [InlineData("正常回复文本", false)]
    [InlineData(null, false)]
    public void IsCancelledTurn_ExactMatchOnly(string? result, bool expected) =>
        Assert.Equal(expected, Program.IsCancelledTurn(result));

    [Fact]
    public void CommandWithoutArgs_EmptyRest()
    {
        var (cmd, rest) = Program.SplitCommand("/clear");
        Assert.Equal("/clear", cmd);
        Assert.Equal("", rest);
    }

    [Fact]
    public void MultipleSpaces_KeepsRestIntact()
    {
        // 只按第一个空格拆分，rest 保留原样（含多余空格）
        var (cmd, rest) = Program.SplitCommand("/save  my session");
        Assert.Equal("/save", cmd);
        Assert.Equal(" my session", rest);
    }

    [Fact]
    public void LeadingSpace_EmptyCommand()
    {
        var (cmd, rest) = Program.SplitCommand("  /model");
        Assert.Equal("", cmd); // 前导空格导致首个空格在 0 位
        Assert.Equal(" /model", rest);
    }

    [Fact]
    public void EmptyLine_BothEmpty()
    {
        var (cmd, rest) = Program.SplitCommand("");
        Assert.Equal("", cmd);
        Assert.Equal("", rest);
    }

    [Fact]
    public void TabSeparated_IsTreatedAsSingleToken()
    {
        // 只按空格拆分：Tab 分隔的命令不会被拆开
        var (cmd, rest) = Program.SplitCommand("/model\tgpt-4o");
        Assert.Equal("/model\tgpt-4o", cmd);
        Assert.Equal("", rest);
    }

    [Fact]
    public void NextArg_ReturnsFollowingTokenAndAdvancesIndex()
    {
        var args = new[] { "-c", "path.json" };
        var i = 0;
        Assert.Equal("path.json", Program.NextArg(args, ref i, "--config"));
        Assert.Equal(1, i);
    }

    [Fact]
    public void NextArg_MissingValue_Throws()
    {
        var args = new[] { "-c" };
        var i = 0;
        var ex = Assert.Throws<ArgumentException>(() => Program.NextArg(args, ref i, "--config"));
        Assert.Contains("--config", ex.Message);
    }

    [Fact]
    public void NextArg_ConsumesNextFlagAsValue()
    {
        // 语义：`-c -p x` 中 -c 会吞掉 -p 作为路径（调用方需自行保证参数顺序正确）
        var args = new[] { "-c", "-p" };
        var i = 0;
        Assert.Equal("-p", Program.NextArg(args, ref i, "--config"));
        Assert.Equal(1, i);
    }
}
