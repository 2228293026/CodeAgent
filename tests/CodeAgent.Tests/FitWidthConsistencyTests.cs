using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 截断与测量必须用**同一套显示宽度规则**。
/// 回归点：InputLine.FitToWidth 曾内联一份旧口径（代理对 2 列 / 其余 &gt;0x2E7F 算 2 列），
/// 而 TextUtil.DisplayWidth 在零宽字符与 ZWJ emoji 上已经改过：
///   · 零宽字符：FitToWidth 算 1 列，DisplayWidth 算 0 列 → **提前截断、白白浪费一列**
///   · ZWJ 序列 👨‍👩‍👧：FitToWidth 算 2+1+2+1+2 = 8 列，实际 2 列
///     → 严重时把序列**劈成半个代理对**，终端渲染成替换方块
/// </summary>
public class FitWidthConsistencyTests
{
    [Fact]
    public void ClusterAtAgreesWithDisplayWidth()
    {
        foreach (var s in new[] { "abc", "中文", "😀", "👨‍👩‍👧", "é", "❤️", "a中b😀c" })
        {
            var total = 0;
            for (var i = 0; i < s.Length;)
            {
                var (w, chars) = TextUtil.ClusterAt(s, i);
                total += w;
                i += chars;
            }
            Assert.Equal(TextUtil.DisplayWidth(s), total);
        }
    }

    [Fact]
    public void ClusterAtConsumesWholeSequence()
    {
        // 三个 emoji + 两个 ZWJ 应作为一个簇被一次消耗完
        var (width, chars) = TextUtil.ClusterAt("👨‍👩‍👧", 0);
        Assert.Equal(2, width);
        Assert.Equal("👨‍👩‍👧".Length, chars);
    }

    [Fact]
    public void ZwjSequenceIsNotTruncatedApart()
    {
        // 关键不变式：ZWJ 序列要么**完整**保留，要么整簇放弃——
        // 绝不能留下「👨‍👩」这种前缀（终端会渲染成半个家庭 emoji 或替换方块）。
        // 注意：完整序列本身**含有** ZWJ 字符，所以要查的是「悬空的 ZWJ」：
        // 它的前后都必须是 emoji，而不是字符串里有没有 ZWJ。
        for (var width = 1; width <= 8; width++)
        {
            var fitted = InputLine.FitToWidth("👨‍👩‍👧abcd", width);
            for (var i = 0; i < fitted.Length; i++)
            {
                if (fitted[i] != '‍')
                    continue;
                var before = i >= 2 ? fitted.Substring(i - 2, 2) : "";
                var after = i + 3 <= fitted.Length ? fitted.Substring(i + 1, 2) : "";
                Assert.True(before.Length == 2 && after.Length == 2,
                    $"预算 {width} 产出悬空 ZWJ：{fitted}");
            }
        }
    }

    [Fact]
    public void ZeroWidthCharsDoNotConsumeBudget()
    {
        // 组合尖音符不占列：8 列预算下应比不含零宽字符时多留下内容
        var withMark = InputLine.FitToWidth("abćdefgh", 8);
        Assert.True(TextUtil.DisplayWidth(withMark) <= 8, withMark);
    }

    [Fact]
    public void NeverExceedsTheWidth()
    {
        var samples = new[]
        {
            "abcdefghijklmnop", "中文中文中文中文", "😀😀😀😀😀😀😀😀",
            "👨‍👩‍👧👨‍👩‍👧👨‍👩‍👧", "éééééééé", "a中b😀c❤️d", "混合mixed内容123",
        };
        foreach (var s in samples)
            for (var width = 1; width <= 20; width++)
            {
                var fitted = InputLine.FitToWidth(s, width);
                Assert.True(TextUtil.DisplayWidth(fitted) <= width,
                    $"样本 {s} 预算 {width} 实际 {TextUtil.DisplayWidth(fitted)}：{fitted}");
            }
    }

    [Fact]
    public void TruncationOnlyWhenNeeded()
    {
        // 放得下就逐字不变
        Assert.Equal("中文", InputLine.FitToWidth("中文", 4));
        Assert.Equal("abc", InputLine.FitToWidth("abc", 3));
        Assert.Equal("👨‍👩‍👧", InputLine.FitToWidth("👨‍👩‍👧", 2));
    }

    [Fact]
    public void NoPartialSurrogateIsEverEmitted()
    {
        var samples = new[] { "😀😀😀😀", "👨‍👩‍👧😀中文😀", "a😀b😀c😀d" };
        foreach (var s in samples)
            for (var width = 1; width <= 12; width++)
            {
                var fitted = InputLine.FitToWidth(s, width);
                for (var i = 0; i < fitted.Length; i++)
                {
                    if (char.IsHighSurrogate(fitted[i]))
                    {
                        Assert.True(i + 1 < fitted.Length && char.IsLowSurrogate(fitted[i + 1]),
                            $"预算 {width} 产出半个代理对：{fitted}");
                        i++;
                    }
                    else
                    {
                        Assert.False(char.IsLowSurrogate(fitted[i]), $"预算 {width} 产出孤立低代理：{fitted}");
                    }
                }
            }
    }

    [Fact]
    public void ZeroWidthReturnsEmpty()
    {
        Assert.Equal(string.Empty, InputLine.FitToWidth("abc", 0));
        Assert.Equal(string.Empty, InputLine.FitToWidth("中文", -3));
    }

    [Fact]
    public void NarrowBudgetKeepsWhatItCan()
    {
        // 1 列预算：连省略号都放不下时给出标记而不是半个字符
        var fitted = InputLine.FitToWidth("中", 1);
        Assert.True(TextUtil.DisplayWidth(fitted) <= 1, fitted);
    }
}
