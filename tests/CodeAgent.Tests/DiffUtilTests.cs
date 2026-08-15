using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class DiffUtilTests
{
    [Fact]
    public void Unified_Identical_ReturnsEmpty() =>
        Assert.Equal("", DiffUtil.Unified("a\nb\n", "a\nb\n", "f.txt"));

    [Fact]
    public void Unified_ChangedLine_ShowsMinusAndPlus()
    {
        var d = DiffUtil.Unified("line1\nold\nline3\n", "line1\nnew\nline3\n", "f.txt");
        Assert.Contains("- old", d);
        Assert.Contains("+ new", d);
    }

    [Fact]
    public void Unified_AddedLines_ShowPlus()
    {
        var d = DiffUtil.Unified("a\n", "a\nb\n", "f.txt");
        Assert.Contains("+ b", d);
    }

    [Fact]
    public void Unified_RemovedLines_ShowMinus()
    {
        var d = DiffUtil.Unified("a\nb\n", "a\n", "f.txt");
        Assert.Contains("- b", d);
    }

    [Fact]
    public void Unified_IncludesFileHeaderAndHunk()
    {
        var d = DiffUtil.Unified("old\n", "new\n", "src/x.txt");
        Assert.Contains("--- a/src/x.txt", d);
        Assert.Contains("+++ b/src/x.txt", d);
        Assert.Contains("@@", d);
    }
}
