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

    [Fact]
    public void NumberedModels_Filtered_KeepsFullListIndices()
    {
        // 回归：/models <过滤> 曾把过滤后的列表重编号为 1..N，
        // 而 /model <编号> 按完整列表解析——编号错位。编号必须保留完整列表下标。
        var models = new[] { "gpt-4o", "deepseek-chat", "gpt-4.1", "deepseek-reasoner", "claude-sonnet-4-5" };
        var rows = Program.NumberedModels(models, "deepseek");
        Assert.Equal([(2, "deepseek-chat"), (4, "deepseek-reasoner")], rows);
    }

    [Fact]
    public void NumberedModels_NoFilter_NumbersSequentially()
    {
        var models = new[] { "gpt-4o", "deepseek-chat", "claude-sonnet-4-5" };
        var rows = Program.NumberedModels(models, null);
        Assert.Equal([(1, "gpt-4o"), (2, "deepseek-chat"), (3, "claude-sonnet-4-5")], rows);
    }

    [Fact]
    public void NumberedModels_NoMatches_ReturnsEmpty()
    {
        var rows = Program.NumberedModels(["gpt-4o"], "gemini");
        Assert.Empty(rows);
    }

    [Fact]
    public void SuggestModels_FamilyPrefix_FindsCandidates()
    {
        var models = new[] { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "deepseek-chat", "deepseek-reasoner" };
        // 输入 gpt4o（拼错的家族名）：按首段 gpt4o 匹配 → 无；用 gpt 匹配的调用方语义
        Assert.Empty(Program.SuggestModels(models, "gpt4o"));
        // 正确家族段：gpt → 前 3 个 gpt 系
        var gpt = Program.SuggestModels(models, "gpt-4o-min");
        Assert.Equal(3, gpt.Count);
        Assert.All(gpt, m => Assert.StartsWith("gpt", m));
        // deepseek 家族
        var ds = Program.SuggestModels(models, "deepseek-cht"); // 拼错 chat → cht
        Assert.Equal(2, ds.Count);
    }

    [Fact]
    public void SuggestModels_EmptyFamily_ReturnsEmpty()
    {
        Assert.Empty(Program.SuggestModels(["a", "b"], "-x"));
        Assert.Empty(Program.SuggestModels(["a"], ".y"));
    }
    [Fact]
    public void ComposeTaskWithStdin_AppendsWhenPiped()
    {
        // type bug.log | codeagent "分析"：stdin 内容附在任务后；空 stdin 原样返回
        var composed = Program.ComposeTaskWithStdin("分析日志", "ERROR at line 3");
        Assert.StartsWith("分析日志", composed);
        Assert.Contains("[stdin 输入]", composed);
        Assert.Contains("ERROR at line 3", composed);

        Assert.Equal("分析日志", Program.ComposeTaskWithStdin("分析日志", ""));
        Assert.Equal("分析日志", Program.ComposeTaskWithStdin("分析日志", "  \n"));
    }

}
