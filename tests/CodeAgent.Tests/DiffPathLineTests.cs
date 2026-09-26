using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// diff 文件标题/文件头行的路径缩短。
/// 回归点：此前这些行直接按显示宽度硬截**尾部**，得到
/// `== src/CodeAgent/Some/very/lon…` —— 看起来像是另一个文件。
/// 路径最有信息量的是末段文件名，必须按尾部保留缩短（与状态栏目录同一套规则）。
/// </summary>
public class DiffPathLineTests
{
    private const string LongPath = "src/CodeAgent/Agent/Provider/Session/Logger.cs";

    [Fact]
    public void FileTitleKeepsTheFileName()
    {
        var line = Program.FormatDiffLine($"== {LongPath} ==", 40);
        Assert.EndsWith("Logger.cs ==", line);
    }

    [Fact]
    public void FileHeaderKeepsTheFileName()
    {
        foreach (var marker in new[] { "---", "+++" })
        {
            var line = Program.FormatDiffLine($"{marker} {LongPath}", 40);
            Assert.EndsWith("Logger.cs", line);
            Assert.StartsWith(marker, line);
        }
    }

    [Fact]
    public void ShortenedPathKeepsTheTailNotTheHead()
    {
        // 核心不变式：丢的是**前导目录**，文件名必须完整——反过来会让人以为是另一个文件
        var line = Program.FormatDiffLine($"== {LongPath} ==", 40);
        Assert.DoesNotContain("src/CodeAgent/Agent/Provider", line);
        Assert.Contains("Session", line);
    }

    [Fact]
    public void FilenameTooLongForTheBudget_IsStillMarked()
    {
        // 连文件名都放不下时是物理限制，但**必须**标省略号，不能看起来像完整路径
        const string huge = "a/b/SessionFileLoggerImplementation.cs";
        var line = Program.FormatDiffLine($"== {huge} ==", 20);
        Assert.Contains("…", line);
        Assert.True(TextUtil.DisplayWidth(line) <= 20);
    }

    [Fact]
    public void ShortenedPathIsMarkedWithEllipsis()
    {
        var line = Program.FormatDiffLine($"== {LongPath} ==", 40);
        Assert.Contains("…", line);
    }

    [Fact]
    public void ShortDiffLineIsUnchanged()
    {
        const string line = "== src/A.cs ==";
        Assert.Equal(line, Program.FormatDiffLine(line, 80));
    }

    [Fact]
    public void UnknownFormFallsBackToPlainTruncation()
    {
        // 不识别的形态返回 null，调用方按普通行硬截（不能凭空造出路径结构）
        Assert.Null(Program.ShortenDiffPathLine("+ 这是一行新增内容", 20));
        Assert.Null(Program.ShortenDiffPathLine("普通文本行", 20));
    }

    [Fact]
    public void ZeroBudgetReturnsNull()
    {
        Assert.Null(Program.ShortenDiffPathLine($"== {LongPath} ==", 3));
        Assert.Null(Program.ShortenDiffPathLine($"--- {LongPath}", 3));
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 16, 20, 30, 40, 60, 80 })
        {
            foreach (var line in new[] { $"== {LongPath} ==", $"--- {LongPath}", $"+++ {LongPath}" })
            {
                var formatted = Program.FormatDiffLine(line, width);
                Assert.True(TextUtil.DisplayWidth(formatted) <= width,
                    $"预算 {width} 实际 {TextUtil.DisplayWidth(formatted)}：{formatted}");
            }
        }
    }

    [Fact]
    public void HunkHeaderStillDropsTheContextTitle()
    {
        // 既有行为不变：@@ 的上下文标题是装饰性的，行号范围必须保住
        var line = Program.FormatDiffLine("@@ -12,7 +12,9 @@ public class VeryLongClassName : Base", 24);
        Assert.StartsWith("@@ -12,7 +12,9 @@", line);
        Assert.DoesNotContain("VeryLongClassName", line);
    }

    [Fact]
    public void ContentLinesStillGetTheEllipsis()
    {
        var line = Program.FormatDiffLine("+" + new string('x', 200), 30);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        var line = $"== {LongPath} ==";
        Assert.Equal(line, Program.FormatDiffLine(line, 0));
    }
}
