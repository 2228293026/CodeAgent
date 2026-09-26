using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 结果行里**用户输入的查询词**的宽度处理。
/// 回归点：`/find 「很长很长…」` 此前把查询词直接拼进行里再整体硬截尾部，
/// 得到「历史会话中没有匹配「很长很长很长的关…」—— 用户看到半截查询，
/// 根本没法确认自己搜的是什么，后面的「的内容。」也被一起切掉了。
/// </summary>
public class FindQueryLineTests
{
    private const string Before = "历史会话中没有匹配「";
    private const string After = "」的内容。";
    private const string Keyword = "一个很长的搜索关键词内容";

    [Fact]
    public void ShortKeywordIsUnchanged()
    {
        var line = Program.FitQueryLine(Before, "重构", After, 80);
        Assert.Equal($"{Before}重构{After}", line);
    }

    [Fact]
    public void LongKeywordIsBoundedAndMarked()
    {
        var line = Program.FitQueryLine(Before, Keyword, After, 40);
        Assert.NotNull(line);
        Assert.Contains("…", line);
    }

    [Fact]
    public void LongKeywordKeepsItsHead()
    {
        // 用户记得住自己输入的开头
        var line = Program.FitQueryLine(Before, Keyword, After, 40)!;
        var query = line.Substring(Before.Length, line.Length - Before.Length - After.Length);
        Assert.StartsWith("一个", query);
    }

    [Fact]
    public void SuffixIsPreservedSoTheSentenceStaysComplete()
    {
        var line = Program.FitQueryLine(Before, Keyword, After, 40)!;
        Assert.EndsWith(After, line);
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        foreach (var width in new[] { 16, 20, 30, 40, 60, 80, 120 })
        {
            var line = Program.FitQueryLine(Before, Keyword, After, width);
            if (line is null)
                continue;
            Assert.True(TextUtil.DisplayWidth(line) <= width,
                $"预算 {width} 实际 {TextUtil.DisplayWidth(line)}：{line}");
        }
    }

    [Fact]
    public void NoRoomForAQueryReturnsNull()
    {
        // 放不下任何查询内容时返回 null，交给普通截断——不造出半个查询词
        Assert.Null(Program.FitQueryLine(Before, Keyword, After, 10));
    }

    [Fact]
    public void UnknownWidthReturnsNull()
    {
        Assert.Null(Program.FitQueryLine(Before, Keyword, After, 0));
    }

    [Fact]
    public void EmptyKeywordReturnsNull()
    {
        Assert.Null(Program.FitQueryLine(Before, "", After, 40));
    }

    [Fact]
    public void AlreadyFittingKeywordIsNotMarked()
    {
        var line = Program.FitQueryLine(Before, "ab", After, 200);
        Assert.DoesNotContain("…", line);
    }

    [Fact]
    public void QueryColumnMaxRatioIsSane()
    {
        double ratio = Program.QueryColumnMaxRatio;
        Assert.True(ratio > 0.2 && ratio < 0.8, $"查询词占比应在 20%~80% 之间，实际 {ratio}");
    }

    [Fact]
    public void CjkKeywordIsMeasuredByDisplayWidth()
    {
        // 纯中文关键词：按字符数算会以为还有 2 倍空间
        var line = Program.FitQueryLine(Before, new string('关', 100), After, 36);
        Assert.NotNull(line);
        Assert.True(TextUtil.DisplayWidth(line) <= 36, line);
        // 查询部分只有 8 列 = 4 个汉字，按字符数算会塞进 8 个字（16 列）直接溢出
        var query = line!.Substring(Before.Length, line.Length - Before.Length - After.Length);
        Assert.True(TextUtil.DisplayWidth(query) <= 8, $"查询段 {TextUtil.DisplayWidth(query)} 列");
    }

    [Fact]
    public void BoilerplateAloneTooWideReturnsNull()
    {
        // 前后缀合计 28 列：30 列终端根本放不下查询词，如实返回 null 交给普通截断
        Assert.Null(Program.FitQueryLine(Before, new string('关', 100), After, 30));
    }
}
