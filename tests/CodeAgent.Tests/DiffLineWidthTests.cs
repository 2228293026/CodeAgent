using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// diff 行的显示宽度裁剪。
/// 回归点：此前逐行原样输出，没有任何宽度预算——CJK 路径与长行会直接撑破终端。
/// </summary>
public class DiffLineWidthTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void FormatDiffLine_NeverExceedsWidth(int width)
    {
        foreach (var line in new[]
        {
            "@@ -12,7 +12,9 @@ public sealed class VeryLongTypeName : IDisposable, IEnumerable<T>",
            "--- a/src/CodeAgent/Some/Deeply/Nested/Directory/That/Goes/On/Forever/File.cs",
            "+    var 结果 = 某个非常长的中文表达式 + 另一个也很长的中文表达式 + 还有更多;",
            "-    removed line with a fairly long english sentence that will not fit in narrow terminals",
            "== src/CodeAgent/Program.cs → src/CodeAgent/Program.cs",
            " " + new string('x', 200),
        })
        {
            var formatted = Program.FormatDiffLine(line, width);
            Assert.True(TextUtil.DisplayWidth(formatted) <= width, $"宽度 {width} 溢出: {formatted}");
        }
    }

    [Fact]
    public void FormatDiffLine_HunkHeaderKeepsLineNumbers()
    {
        // 行号范围是 hunk 的结构信息，尾部的上下文标题（长方法签名）才是装饰
        var line = "@@ -12,7 +12,9 @@ public sealed class VeryLongTypeName : IDisposable, IEnumerable<T>";
        var formatted = Program.FormatDiffLine(line, 24);
        Assert.Equal("@@ -12,7 +12,9 @@", formatted);
    }

    [Fact]
    public void FormatDiffLine_HunkHeaderTooNarrowStillFits()
    {
        // 24 列都放不下时，hunk 头本身也必须被裁（否则仍然溢出）
        var formatted = Program.FormatDiffLine("@@ -120,70 +120,90 @@ SomeVeryLongContextHeading", 20);
        Assert.True(TextUtil.DisplayWidth(formatted) <= 20, $"溢出: {formatted}");
    }

    [Fact]
    public void FormatDiffLine_AddedLineIsMarkedAsTruncated()
    {
        // 被截掉的 +行必须带标记，否则看起来就是"新增了这么多内容"
        var formatted = Program.FormatDiffLine("+" + new string('x', 200), 40);
        Assert.StartsWith("+", formatted);
        Assert.Contains("…", formatted);
    }

    [Fact]
    public void FormatDiffLine_RemovedLineIsMarkedAsTruncated()
    {
        var formatted = Program.FormatDiffLine("-" + new string('中', 200), 40);
        Assert.StartsWith("-", formatted);
        Assert.Contains("…", formatted);
    }

    [Fact]
    public void FormatDiffLine_ShortLineUnchanged()
    {
        Assert.Equal("+    var x = 1;", Program.FormatDiffLine("+    var x = 1;", 80));
    }

    [Fact]
    public void FormatDiffLine_UnknownWidthKeepsLineIntact()
    {
        var line = "+" + new string('x', 500);
        Assert.Equal(line, Program.FormatDiffLine(line, 0));
    }
}
