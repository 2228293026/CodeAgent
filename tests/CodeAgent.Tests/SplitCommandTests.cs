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
}
