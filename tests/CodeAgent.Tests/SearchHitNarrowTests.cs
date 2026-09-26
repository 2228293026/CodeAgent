using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 搜索命中行在极窄终端下的降级。
/// 回归点：角色标记比终端还宽时，旧实现直接返回**只有标记**的一行 —— 摘要被整段丢掉，
/// 屏幕上是一列长得一模一样的 `[用户]`，完全看不出命中了什么。
/// 搜索结果与 /history 现在共用 <c>FitRoleTag</c>，同一角色在两处长得一样。
/// </summary>
public class SearchHitNarrowTests
{
    private const string Snippet = "这是命中内容，包含关键词";

    [Fact]
    public void WideTerminalIsUnchanged()
    {
        Assert.Equal($"  [用户] {Snippet}", Program.FormatSearchHitLine("用户", Snippet, 80));
    }

    [Fact]
    public void UnknownWidthKeepsTheLegacyAppearance()
    {
        // 宽度未知时回退到 110 字符，观测与此前一致
        Assert.Equal($"  [用户] {Snippet}", Program.FormatSearchHitLine("用户", Snippet, 0));
    }

    [Fact]
    public void VeryNarrowKeepsPartOfTheSnippet()
    {
        // 核心保证：窄屏下摘要仍然出现（哪怕只剩几个字），不能整段消失成一列角色标记。
        // 不断言具体哪几个字：6 列预算本来就只放得下 2 个汉字，断言"命中"必然落空。
        var line = Program.FormatSearchHitLine("用户", Snippet, 16);
        var body = line[(line.IndexOf(']') + 1)..].Trim();
        Assert.NotEqual("", body);
        Assert.Contains("…", body);
    }

    [Fact]
    public void BudgetExhaustedByTheTag_StillShowsSnippet()
    {
        // 12 列：标记 9 列 + 摘要 3 列 → 「这…」。宽度再小就没有余量了（这是物理限制，不是降级策略）
        var line = Program.FormatSearchHitLine("用户", Snippet, 12);
        Assert.Contains("这", line);
    }

    [Fact]
    public void LongRoleIsAbbreviatedRatherThanOverflowing()
    {
        // 长角色在窄屏下必须缩写：既不能撑爆，也不能原样保留让整行溢出
        const string role = "一个很长的角色名称";
        var line = Program.FormatSearchHitLine(role, Snippet, 20);
        Assert.DoesNotContain(role, line);
        Assert.True(TextUtil.DisplayWidth(line) <= 20);
    }

    [Fact]
    public void AbbreviatedTagStillLooksLikeATag()
    {
        var line = Program.FormatSearchHitLine("用户", Snippet, 14);
        Assert.Contains("[", line);
        Assert.Contains("]", line);
    }

    [Fact]
    public void SearchTagMatchesHistoryTag()
    {
        // 同一角色在 /history 与搜索结果里必须长得一样（共用 FitRoleTag）
        const string role = "一个很长的角色名称";
        var trimmed = Program.FormatSearchHitLine(role, Snippet, 18).TrimStart();
        var close = trimmed.IndexOf(']');
        Assert.True(close > 0, $"未找到角色标记：{trimmed}");
        var tag = trimmed[..(close + 1)];
        Assert.StartsWith(tag, Program.FitRoleTag(role, false, 16));
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 8, 10, 12, 14, 16, 20, 30, 60 })
        {
            var line = Program.FormatSearchHitLine("工具", Snippet, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width,
                $"预算 {width} 实际 {TextUtil.DisplayWidth(line)}：{line}");
        }
    }

    [Fact]
    public void ExtremeNarrownessStillShowsTheRole()
    {
        // 极窄到放不下摘要时，角色标识必须还在
        foreach (var width in new[] { 6, 8, 10 })
        {
            var line = Program.FormatSearchHitLine("用户", Snippet, width);
            Assert.True(line.Contains('[') || line.Contains('·') || line.Contains('⚠'),
                $"宽度 {width} 丢失角色标识：{line}");
        }
    }

    [Fact]
    public void TruncatedSnippetIsMarked()
    {
        var line = Program.FormatSearchHitLine("用户", Snippet, 20);
        Assert.Contains("…", line);
    }
}
