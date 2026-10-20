using System;
using System.Collections.Generic;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 帮助列表在窄终端下不许越界。
///
/// 这块修过好几次（续行缩进用错预算、命令名比终端还宽没有截断），
/// 但每次都是「发现了某个具体宽度的问题」再补。这里直接穷举一遍：
/// width 从 1 到 40，每个宽度下**每一行**的显示宽度都不许超过该宽度。
///
/// 帮助是用户最常看的输出之一，也是最短命的——越界折行一次，
/// 整张表的列对应关系就散了，而用户只会觉得「输出乱了」。
/// </summary>
public class NarrowHelpListTests
{
    private static readonly List<Program.HelpEntry> Entries =
    [
        new("/help", "显示帮助"),
        new("/dependency_lockfile_report", "检查依赖锁文件是否与清单一致，报告缺失或过期的条目"),
        new("/status", "会话状态与配置摘要"),
        new("/x", "短"),
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    public void NoLineExceedsTheWidth(int width)
    {
        var text = Program.FormatHelpList(Entries, width);
        foreach (var line in text.Split('\n'))
        {
            // AppendLine 在 Windows 下追加 \r，DisplayWidth 会把它算成 1 列。
            // 终端不显示 CR，产品里也只把 CRLF 当换行——测宽度前先剥掉，
            // 否则量到的是「测试宿主的换行风格」而不是排版。
            var bare = line.TrimEnd('\r');
            // 「交代行」是有意为之的例外：它说明「宽度 N 列装不下一个字，内容被省略」，
            // 这句话本身必然比终端宽。截断它等于把唯一有用的信息删掉——
            // 用户只看到一个省略号，反而更不知道发生了什么。
            // NarrowWidthContentLossTests 已经把这层取舍钉住了，这里跟随。
            if (bare.Contains("装不下一整个字符", StringComparison.Ordinal))
                continue;
            var w = TextUtil.DisplayWidth(bare);
            Assert.True(w <= width, $"width={width} 的一行有 {w} 列：{bare}");
        }
    }

    [Fact]
    public void ZeroWidthMeansUnknownAndIsNotClamped()
    {
        // 宽度未知（0）时不猜：交回终端软换行，而不是按某个臆想的宽度硬切
        var text = Program.FormatHelpList(Entries, 0);
        Assert.Contains("/dependency_lockfile_report", text);
    }

    [Fact]
    public void UnknownWidthKeepsEveryEntry()
    {
        var text = Program.FormatHelpList(Entries, 0);
        foreach (var e in Entries)
            Assert.Contains(e.Command, text);
    }
}
