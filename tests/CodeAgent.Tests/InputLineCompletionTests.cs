using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// Tab 补全：候选公共前缀填充（bash 式）。
/// 回归点：旧实现在多匹配时只高亮第 1 项、输入文本原地不动，用户看不出还能补到哪。
/// </summary>
public class InputLineCompletionTests
{
    [Fact]
    public void CommonPrefix_UsesSharedCharacters()
    {
        Assert.Equal("/mode", InputLine.CommonPrefix(new[] { "/mode", "/model" }));
        Assert.Equal("/c", InputLine.CommonPrefix(new[] { "/clear", "/cls", "/compact" }));
    }

    [Fact]
    public void CommonPrefix_NoOverlap_ReturnsEmpty()
    {
        Assert.Equal("", InputLine.CommonPrefix(new[] { "/abc", "xyz" }));
    }

    [Fact]
    public void CommonPrefix_SharesOnlySlash()
    {
        // 都以 / 开头：公共前缀是 "/"，比已输入的 "/m" 还短，补全不应推进
        Assert.Equal("/", InputLine.CommonPrefix(new[] { "/abc", "/xyz" }));
    }

    [Fact]
    public void CommonPrefix_EmptyCandidates_ReturnsEmpty()
    {
        Assert.Equal("", InputLine.CommonPrefix(Array.Empty<string>()));
    }

    [Fact]
    public void CommonPrefix_SingleCandidate_IsItself()
    {
        Assert.Equal("/help", InputLine.CommonPrefix(new[] { "/help" }));
    }

    [Fact]
    public void CommonPrefix_StopsAtShortestCandidate()
    {
        Assert.Equal("/m", InputLine.CommonPrefix(new[] { "/mode", "/m" }));
    }

    [Fact]
    public void TabCompletion_UniqueMatch_FillsWholeCommand()
    {
        Assert.Equal("/help", InputLine.TabCompletion(new[] { "/help" }, "/he"));
    }

    [Fact]
    public void TabCompletion_MultipleMatches_AdvancesToCommonPrefix()
    {
        var candidates = new[] { "/mode", "/model" };
        Assert.Equal("/mode", InputLine.TabCompletion(candidates, "/m"));
    }

    [Fact]
    public void TabCompletion_NoFurtherProgress_ReturnsNull()
    {
        // 已经补到公共前缀，再按 Tab 应转为循环高亮而不是空推进
        Assert.Null(InputLine.TabCompletion(new[] { "/mode", "/model" }, "/mode"));
    }

    [Fact]
    public void TabCompletion_NoCandidates_ReturnsNull()
    {
        Assert.Null(InputLine.TabCompletion(Array.Empty<string>(), "/m"));
    }

    [Fact]
    public void TabCompletion_RealCommandSet_FillsProgressivelyThenCompletes()
    {
        var all = InputLine.Commands.Select(c => c.Name).ToArray();
        // 菜单候选已按前缀过滤（与 RefreshMenu 口径一致）
        var candidates = all.Where(n => n.StartsWith("/m", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(candidates.Length > 1);
        var step1 = InputLine.TabCompletion(candidates, "/m");
        Assert.NotNull(step1);
        Assert.StartsWith("/m", step1);
        Assert.True(step1!.Length > 2);
        // 补到公共前缀后无法再推进：返回 null，交回循环高亮
        Assert.Null(InputLine.TabCompletion(candidates, step1));
    }
}
