using System.Linq;
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

    [Fact]
    public void Unified_LargeInput_FallsBackToSummary()
    {
        // 回归：LCS dp 矩阵是 O(n*m) 内存，超大输入曾直接 OOM；现应退化为行数摘要
        var big = string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"line{i}"));
        var d = DiffUtil.Unified(big, big + "\nextra", "big.txt");
        Assert.Contains("差异过大", d);
        Assert.Contains("3000 行", d);
        Assert.DoesNotContain("line2999", d); // 不应输出逐行内容
    }

    [Fact]
    public void Unified_NewFile_ShowsAllAdded()
    {
        // 新建文件：空原文 → 全部 + 行（回归：曾因 SplitLines("")=[""] 输出多余的 - 空行）
        var d = DiffUtil.Unified("", "hello\nworld\n", "new.txt");
        Assert.Contains("@@ -0,0 +1,2 @@", d);
        Assert.Contains("+ hello", d);
        Assert.Contains("+ world", d);
        Assert.DoesNotContain("- hello", d); // 内容行不是删除行（头行 --- a/ 里含 "- " 属正常）
    }

    [Fact]
    public void Unified_DeletedFile_ShowsAllRemoved()
    {
        // 文件被清空/删除：空新文 → 全部 - 行
        var d = DiffUtil.Unified("a\nb\n", "", "gone.txt");
        Assert.Contains("@@ -1,2 +0,0 @@", d);
        Assert.Contains("- a", d);
        Assert.Contains("- b", d);
        Assert.DoesNotContain("+ a", d); // 内容行不是新增行
    }
}
