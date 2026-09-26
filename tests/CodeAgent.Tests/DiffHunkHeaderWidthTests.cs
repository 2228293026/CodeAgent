using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// diff hunk 头（`@@ -a,b +c,d @@ 上下文标题`）在极窄终端上的降级。
/// 回归点：此前只做一步——丢掉上下文标题。**连 bare 形式都放不下时**
/// 直接交给 FitToWidth 硬截尾部，行号范围被切成 `-12,7…`：
/// 行号是 hunk 唯一的结构信息，丢了它这段 diff 就不知道在改哪几行。
/// 现在按 bare → 去掉收尾 @@ → 只留 @@ 逐级退让。
/// </summary>
public class DiffHunkHeaderWidthTests
{
    private const string Hunk = "@@ -12,7 +12,9 @@ private static string FormatDiffLine(string line, int width)";

    [Fact]
    public void WideLineIsUnchanged()
    {
        var line = Program.FormatDiffLine(Hunk, 200);
        Assert.Equal(Hunk, line);
    }

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        Assert.Equal(Hunk, Program.FormatDiffLine(Hunk, 0));
    }

    [Fact]
    public void SectionTitleGoesFirst()
    {
        // 优先丢上下文标题，保住行号范围
        var line = Program.FormatDiffLine(Hunk, TextUtil.DisplayWidth("@@ -12,7 +12,9 @@"));
        Assert.Equal("@@ -12,7 +12,9 @@", line);
    }

    [Fact]
    public void TailAtAtIsDroppedBeforeTheRange()
    {
        var line = Program.FormatDiffLine(Hunk, TextUtil.DisplayWidth("@@ -12,7 +12,9"));
        Assert.Equal("@@ -12,7 +12,9", line);
        Assert.Contains("-12,7", line);
        Assert.Contains("+12,9", line);
    }

    [Fact]
    public void NoLineEndsWithWhitespace()
    {
        // 退让形态不能留下行尾空格：复制 diff 时会带进无意义空白
        foreach (var width in new[] { 2, 4, 8, 12, 14, 16, 17, 18, 20, 40 })
        {
            var line = Program.FormatDiffLine(Hunk, width);
            Assert.Equal(line.TrimEnd(), line);
        }
    }

    [Fact]
    public void MarkerSurvivesAtEveryWidth()
    {
        foreach (var width in new[] { 2, 3, 4, 6, 8, 10, 12, 14, 16, 20, 40 })
        {
            var line = Program.FormatDiffLine(Hunk, width);
            Assert.StartsWith("@@", line);
            Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void RangeIsNeverHalfCut()
    {
        // 退化时要么完整行号、要么整段丢掉，绝不出现 `-12,7…` 这种半个数字
        foreach (var width in new[] { 2, 3, 4, 6, 8, 10, 12, 14, 16 })
        {
            var line = Program.FormatDiffLine(Hunk, width);
            Assert.DoesNotContain("…", line);
            if (line.Length > 2)
                Assert.Equal("@@ -12,7 +12,9", line);
        }
    }

    [Fact]
    public void ContentLinesStillGetAnEllipsis()
    {
        // hunk 头可以整段退让，内容行不行：被截掉的 +行必须留痕
        var line = Program.FormatDiffLine("+这是一行很长的新增内容，用于验证截断标记", 10);
        Assert.EndsWith("…", line);
        Assert.StartsWith("+", line);
    }

    [Fact]
    public void RemovedLinesStillGetAnEllipsis()
    {
        var line = Program.FormatDiffLine("-这是一行很长的删除内容，用于验证截断标记", 10);
        Assert.EndsWith("…", line);
        Assert.StartsWith("-", line);
    }

    [Fact]
    public void FileHeaderPathsKeepTheirTail()
    {
        var line = Program.FormatDiffLine("--- a/very/long/path/To/Some/File.cs", 24);
        Assert.StartsWith("--- ", line);
        Assert.Contains("File.cs", line);
    }

    [Fact]
    public void HunkWithoutSectionTitleSurvivesWhenItFits()
    {
        const string line = "@@ -1,2 +1,2 @@";
        // 宽度按**实际渲染结果**算，不要手数——`@@ -1,2 +1,2 @@` 是 15 列不是 14
        var exact = TextUtil.DisplayWidth(line);
        foreach (var width in new[] { exact, exact + 3, exact + 20 })
            Assert.Equal(line, Program.FormatDiffLine(line, width));
    }

    [Fact]
    public void HunkWithoutSectionTitleDegradesToMarker()
    {
        // 窄到放不下行号范围时只留标记，绝不半截数字
        foreach (var width in new[] { 4, 8, 12 })
        {
            var line = Program.FormatDiffLine("@@ -1,2 +1,2 @@", width);
            Assert.True(line is "@@" or "@@ -1,2 +1,2", line);
            Assert.DoesNotContain("…", line);
        }
    }

    [Fact]
    public void DiffBodyLinesAreUnaffected()
    {
        foreach (var line in new[] { " context", "+x", "-y", "  ", "" })
            Assert.Equal(line, Program.FormatDiffLine(line, 200));
    }
}
