using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 多行粘贴折叠提示：附带末行预览，并把折叠阈值收敛为单一常量。
/// 回归点：折叠后中间行不可见且提示行只有「共 N 行」，用户无法判断末尾内容。
/// </summary>
public class InputLineFoldTests
{
    [Fact]
    public void FoldText_ShowsLastLinePreview()
    {
        var folded = InputLine.FoldText("a\nb\nc\nd\ne");
        var hint = folded.Split('\n')[2];
        Assert.Contains("共 5 行", hint);
        Assert.Contains("末行: e", hint);
    }

    [Fact]
    public void FoldText_PreviewIsTruncatedToWidthBudget()
    {
        var longTail = new string('x', 200);
        var folded = InputLine.FoldText("a\nb\nc\n" + longTail);
        var hint = folded.Split('\n')[2];
        // 提示行 = 前缀 + 预览，预览部分被截到 40 列并带省略号
        var preview = hint[(hint.IndexOf("末行: ", StringComparison.Ordinal) + 4)..];
        Assert.Equal(InputLine.FoldTailPreviewWidth, TextUtil.DisplayWidth(preview));
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldText_PreviewCountsCjkAsTwoColumns()
    {
        var folded = InputLine.FoldText("a\nb\nc\n" + new string('中', 60));
        var hint = folded.Split('\n')[2];
        var preview = hint[(hint.IndexOf("末行: ", StringComparison.Ordinal) + 4)..];
        // 省略号占 1 列且不回退半个汉字：全角预览可能比预算少 1 列，但不得溢出
        Assert.InRange(TextUtil.DisplayWidth(preview), InputLine.FoldTailPreviewWidth - 1, InputLine.FoldTailPreviewWidth);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldText_BlankLastLineFallsBackToPreviousLine()
    {
        // 末尾空行时不显示空的「末行: 」，回看一行给出有效预览
        var folded = InputLine.FoldText("a\nb\nc\nd\n");
        Assert.Contains("末行: d", folded);
    }

    [Fact]
    public void FoldText_AllBlankTail_OmitsPreview()
    {
        // 末尾连续空行：回看一行仍是空，不显示空的「末行: 」
        var folded = InputLine.FoldText("a\nb\nc\n\n\n");
        var hint = folded.Split('\n')[2];
        Assert.Contains("共 5 行", hint);
        Assert.DoesNotContain("末行", hint);
    }

    [Fact]
    public void FoldThreshold_IsSingleSourceOfTruth()
    {
        Assert.Equal(3, InputLine.FoldThreshold);
        // 折叠判定与显示换行数必须用同一阈值：超过阈值时显示行恒为 FoldThreshold
        var text = string.Join('\n', Enumerable.Range(0, 10).Select(i => $"line{i}"));
        Assert.Equal(InputLine.FoldThreshold - 1, InputLine.DisplayedNewlines(false, text));
        Assert.Equal(InputLine.FoldThreshold, InputLine.FoldText(text).Split('\n').Length);
    }
}
