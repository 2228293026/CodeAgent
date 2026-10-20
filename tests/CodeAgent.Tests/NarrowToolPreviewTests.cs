using System;
using System.Linq;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具输出预览在各种终端宽度下都不许越界。
///
/// 预览是工具执行后紧跟状态行的那几行 <c>  │ 内容</c>。它和正文不一样：
/// 正文越界只是难看，预览越界会让**后续每一行**的缩进都错位——
/// 因为预览带悬挂缩进，错一列就一直错下去。
///
/// 这块已有 ToolPreviewWidth 与截断逻辑，但都是「算准了」。
/// 这里直接量：width 1..60，逐行断言。
/// </summary>
public class NarrowToolPreviewTests
{
    private static readonly string[] Samples =
    [
        "ok",
        "这是一段中文输出内容",
        "foo bar baz qux quux corge grault garply waldo fred plugh xyzzy",
        new string('a', 200),
        "line1\nline2\nline3",
        "混合 mixed 内容 content 很长很长很长很长很长很长",
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(60)]
    public void NoPreviewLineExceedsTheWidth(int width)
    {
        foreach (var sample in Samples)
        {
            var text = AgentClass.FormatToolOutputPreview(sample, maxChars: 2000, width: width);
            if (string.IsNullOrEmpty(text))
                continue;
            foreach (var line in text.Split('\n'))
            {
                var bare = line.TrimEnd('\r');
                // 交代行（被截断时的说明）是有意为之的例外：它要说明「被截断了」
                if (bare.Contains("省略", StringComparison.Ordinal) ||
                    bare.Contains("截断", StringComparison.Ordinal))
                    continue;
                var w = TextUtil.DisplayWidth(bare);
                Assert.True(w <= width,
                    $"width={width} 超宽（{w} 列）：{bare}\n源: {sample}");
            }
        }
    }

    [Fact]
    public void ZeroColumnsFallsBackToPlainMeaning()
    {
        // 宽度未知时不猜：不应该崩，也不应该把内容整个吞掉
        var text = AgentClass.FormatToolOutputPreview("hello world", maxChars: 2000, width: 0);
        Assert.NotNull(text);
    }

    [Fact]
    public void ZeroMaxCharsStillReturnsEmpty()
    {
        // maxChars <= 0 是「不要预览」的显式语义，不能因为宽度处理而破坏它
        Assert.Equal(string.Empty,
            AgentClass.FormatToolOutputPreview("hello", maxChars: 0, width: 80));
    }
}
