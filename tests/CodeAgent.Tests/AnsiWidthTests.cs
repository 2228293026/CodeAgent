using System;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 带 ANSI 颜色码的字符串，显示宽度该是多少。
///
/// <c>TextUtil.DisplayWidth</c> 逐字符累加、不识别转义序列。这对**纯文本**输入是对的，
/// 但传进来若是已经上好色的串（<c>SafeColor.Scope</c> 包出来的），
/// <c>\x1b[36m…\x1b[0m</c> 会被逐字符算成列数——7 字符的颜色前缀等于凭空多出 7 列。
///
/// 目前**没有**真实调用点这么传（我查过 223 处），所以这不是在修一个正在发生的 bug，
/// 而是在拆一颗雷：任何一处将来把着色串递给它，对齐就静默偏几列，
/// 截断还可能把转义序列劈成两半、终端吃到半个颜色码。
/// 这类问题不会报错，只会「看起来不太对」，排查成本极高。
/// </summary>
public class AnsiWidthTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void PlainTextWidthIsUnchanged()
    {
        Assert.Equal(3, TextUtil.DisplayWidth("abc"));
        Assert.Equal(4, TextUtil.DisplayWidth("中文"));
    }

    [Fact]
    public void ColourCodesDoNotCountAsColumns()
    {
        Assert.Equal(3, TextUtil.DisplayWidth($"{Esc}[36mabc{Esc}[0m"));
        Assert.Equal(4, TextUtil.DisplayWidth($"{Esc}[32m中文{Esc}[0m"));
    }

    [Fact]
    public void MultipleNestedCodesAreAllSkipped()
    {
        Assert.Equal(2, TextUtil.DisplayWidth($"{Esc}[1m{Esc}[31mab{Esc}[0m{Esc}[0m"));
    }

    [Fact]
    public void CodesOnlyMeansZeroWidth()
    {
        Assert.Equal(0, TextUtil.DisplayWidth($"{Esc}[36m{Esc}[0m"));
    }

    [Fact]
    public void TruncationNeverSplitsAnEscapeSequence()
    {
        // 截断必须整段保留或整段丢弃，不能留下半个 ESC[36m
        var colored = $"{Esc}[36mabcdef{Esc}[0m";
        var fit = InputLine.FitToWidth(colored, 6);
        Assert.Contains("abcdef", fit);
        // 里边不能出现孤零零的 ESC 后面没跟完的序列
        foreach (var idx in AllIndexesOf(fit, Esc))
            Assert.True(idx + 1 < fit.Length && fit[idx + 1] == '[', $"转义序列被劈开了: {fit}");
    }

    [Fact]
    public void TakeDisplayWidthKeepsThePreambleWithTheText()
    {
        // 取前 3 列：「颜色前缀 + abc」应该整段出来，不能只剩颜色码或只剩字
        var colored = $"{Esc}[36mabcdef{Esc}[0m";
        var taken = TextUtil.TakeDisplayWidth(colored, 3);
        Assert.Contains("abc", taken);
        Assert.DoesNotContain("abcd", taken);
    }

    private static System.Collections.Generic.IEnumerable<int> AllIndexesOf(string haystack, string needle)
    {
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            yield return i;
    }
}
