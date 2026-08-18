using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class PathDisplayTests
{
    [Theory]
    [InlineData("/short/path")]
    [InlineData("")]
    [InlineData("D:/Projects/CodeAgent")]
    public void TruncatePathHead_ShortPaths_Unchanged(string path) =>
        Assert.Equal(path, Program.TruncatePathHead(path));

    [Fact]
    public void TruncatePathHead_LongPath_KeepsTailWithEllipsis()
    {
        // 深路径显示：保留尾部（工作区目录名永远可见），长度封顶
        var longPath = @"C:\Users\someone\Deeply\Nested\Project\Structure\CodeAgent";
        var shown = Program.TruncatePathHead(longPath);
        Assert.StartsWith("…", shown);
        Assert.EndsWith("CodeAgent", shown);
        Assert.True(shown.Length <= 42, $"截断后 {shown.Length} 仍超上限");
        // 恰好等于上限：原样返回
        Assert.Equal(longPath, Program.TruncatePathHead(longPath, longPath.Length));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void FilterModels_BlankFilter_ReturnsAll(string? filter)
    {
        var models = new[] { "gpt-4o", "deepseek-chat", "claude-sonnet-4-5" };
        Assert.Equal(3, Program.FilterModels(models, filter).Count);
    }

    [Fact]
    public void FilterModels_SubstringCaseInsensitive()
    {
        var models = new[] { "gpt-4o", "GPT-4.1-mini", "deepseek-chat", "openai/gpt-5" };
        var hit = Program.FilterModels(models, "gpt");
        Assert.Equal(3, hit.Count); // openai/gpt-5 也命中（子串）
        Assert.DoesNotContain("deepseek-chat", hit);
    }
}
