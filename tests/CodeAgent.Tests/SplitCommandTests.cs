using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class SplitCommandTests
{
    [Fact]
    public void SplitCommand_UpperCaseCommand_IsNormalized_RestKeepsCase()
    {
        // 回归：/MODEL 曾因大小写不匹配落入 default 分支被当成聊天消息发给模型
        var (cmd, rest) = Program.SplitCommand("/MODEL GPT-4o");
        Assert.Equal("/model", cmd);
        Assert.Equal("GPT-4o", rest); // rest 不动：模型名/会话名区分大小写

        var (c2, rest2) = Program.SplitCommand("/HELP");
        Assert.Equal("/help", c2);
    }
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
    public void TabSeparated_SplitsCommand()
    {
        // Tab 也作为分隔符（粘贴的命令行常用 Tab 缩进）；曾只按空格拆导致无法识别
        var (cmd, rest) = Program.SplitCommand("/model\tgpt-4o");
        Assert.Equal("/model", cmd);
        Assert.Equal("gpt-4o", rest);
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

    [Theory]
    [InlineData("/model\tgpt-4o", "/model", "gpt-4o")]       // Tab 分隔
    [InlineData("/save　名称", "/save", "名称")]               // 全角空格分隔（CJK 输入法）
    [InlineData("/mode\t next", "/mode", " next")]           // 分隔符后保留原样（与空格语义一致）
    public void SplitCommand_TabAndFullWidthSpace_Separate(string line, string cmd, string rest)
    {
        // 回归：只按半角空格拆分时，Tab/全角空格分隔的命令无法识别
        var (c, r) = Program.SplitCommand(line);
        Assert.Equal(cmd, c);
        Assert.Equal(rest, r);
    }
}
