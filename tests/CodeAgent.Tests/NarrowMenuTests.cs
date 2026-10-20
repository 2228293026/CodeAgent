using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 菜单（命令补全 / @ 文件 / 模式选择）在窄终端下不许越界。
///
/// 菜单比正文更危险：它是**原地重绘**的，靠精确的行数计算上移。
/// 一行越界折成两行，上移量就少算一行，菜单会一帧帧往下爬。
///
/// 裁剪入口是 <c>FitMenuLine</c>（FitMenu 在 fitBudget &gt; 0 时走它）。
/// 我第一版测的是 FormatMenuLine——那个函数根本没有宽度参数，
/// 测它等于测「没有裁剪时会不会超宽」，答案永远是会。
/// </summary>
public class NarrowMenuTests
{
    private static readonly (string Name, string Desc)[] Items =
    [
        ("/help", "显示帮助"),
        ("/model", "切换模型"),
        ("/dependency-check", "检查依赖锁文件是否与清单一致并报告缺失或过期的条目"),
        ("x", ""),
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
    [InlineData(80)]
    public void MenuLineNeverExceedsTheBudget(int width)
    {
        foreach (var (name, desc) in Items)
        {
            var line = InputLine.FitMenuLine(name, desc, width, modePicker: false, visibleRow: 0, nameWidth: 10);
            if (line.Length == 0)
                continue; // 预算不足以放下编号+名称：调用方画空行，块高保持稳定
            var w = TextUtil.DisplayWidth(line);
            Assert.True(w <= width, $"width={width} 菜单行有 {w} 列：{line}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MenuLineReturnsEmptyWhenNothingFits(int width)
    {
        // 这么窄连「1) 」都放不下：必须返回空串，让调用方画空行。
        // 返回半截内容会让块高计算与实际行数不符，菜单逐帧往上爬。
        var line = InputLine.FitMenuLine("/help", "显示帮助", width, modePicker: false, visibleRow: 0, nameWidth: 10);
        Assert.Equal(string.Empty, line);
    }

    [Fact]
    public void ModePickerLineHasNoNumber()
    {
        // 模式菜单里数字键是普通输入：行首不能出现「1) 」，否则用户以为能按 1 选
        var line = InputLine.FitMenuLine("high", "高", 40, modePicker: true, visibleRow: 0, nameWidth: 10);
        Assert.DoesNotContain("1)", line);
        Assert.Contains("high", line);
    }

    [Fact]
    public void HeaderIsNeverEmpty()
    {
        // 表头不参与宽度裁剪（它在最上方、不参与原地重绘的行数计算），
        // 但绝不能为空：空的表头让用户不知道这个列表是什么、怎么操作。
        Assert.NotEmpty(InputLine.BuildMenuHeader(modePicker: false, mentionActive: false));
        Assert.NotEmpty(InputLine.BuildMenuHeader(modePicker: true, mentionActive: false));
        Assert.NotEmpty(InputLine.BuildMenuHeader(modePicker: false, mentionActive: true, mentionTotal: 5, mentionShown: 5));
    }

    [Fact]
    public void MentionCountNoteReportsHonestNumbers()
    {
        // 「列出 M 个」只在**真的丢了**时出现：M == N 时报这个是纯噪音
        Assert.DoesNotContain("列出", InputLine.MentionCountNote(5, 5));
        Assert.Contains("列出 200 个", InputLine.MentionCountNote(500, 200));
        Assert.Equal("无匹配文件", InputLine.MentionCountNote(0, 0));
    }
}
