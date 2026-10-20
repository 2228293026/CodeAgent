using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /models 与 /tools 在各种终端宽度下都不许越界。
///
/// 这两个列表是用户找东西的主要入口，内容长度不受控：
/// 模型名可以 30+ 字符，工具名最长 25 字符（dependency_lockfile_report），
/// 说明更是随便长短。它们都走 <c>FormatHelpList</c>——
/// 而那个函数的窄终端缺陷上一轮刚修过（续行预算算成 0、缩进写死 6 列）。
///
/// 所以这里不只是重跑一遍：要确认**接上真实内容之后**仍然不越界，
/// 因为 FormatHelpList 的缺陷恰恰是靠长内容才暴露的。
/// </summary>
public class NarrowModelToolListTests
{
    private static readonly string[] Models =
    [
        "claude-sonnet-4-5-20250929",
        "claude-opus-4-1-20250805",
        "gpt-5-codex",
        "deepseek-v4.1-flash",
        "短",
    ];

    private static readonly Program.HelpEntry[] ToolRows =
    [
        new("/help", "显示帮助"),
        new("dependency_lockfile_report", "检查依赖锁文件是否与清单一致，报告缺失或过期的条目"),
        new("read_file", "读取文件内容"),
        new("x", "y"),
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
    public void ModelListNeverExceedsTheWidth(int width)
    {
        var numbered = Program.NumberedModels(Models, null);
        var entries = numbered.Select(m => new Program.HelpEntry($"{m.Num})", m.Model)).ToList();
        var text = Program.FormatHelpList(entries, width);
        foreach (var line in text.Split('\n'))
        {
            var bare = line.TrimEnd('\r');
            if (bare.Length == 0)
                continue;
            if (bare.Contains("装不下一整个字符", StringComparison.Ordinal))
                continue;
            var w = TextUtil.DisplayWidth(bare);
            Assert.True(w <= width, $"width={width} 超宽（{w} 列）：{bare}");
        }
    }

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
    public void ToolListNeverExceedsTheWidth(int width)
    {
        var text = Program.FormatHelpList(ToolRows, width);
        foreach (var line in text.Split('\n'))
        {
            var bare = line.TrimEnd('\r');
            if (bare.Length == 0)
                continue;
            if (bare.Contains("装不下一整个字符", StringComparison.Ordinal))
                continue;
            var w = TextUtil.DisplayWidth(bare);
            Assert.True(w <= width, $"width={width} 超宽（{w} 列）：{bare}");
        }
    }

    [Fact]
    public void ToolsHeaderIsAlsoWidthBound()
    {
        // 表头也是输出的一部分：它同样会越界折行，而折行会把列表整体推歪
        foreach (var width in new[] { 1, 2, 3, 5, 10, 20, 40 })
        {
            var header = Program.FormatToolsHeader(231, 231, null, "debug", width);
            Assert.True(TextUtil.DisplayWidth(header) <= width,
                $"width={width} 表头超宽（{TextUtil.DisplayWidth(header)} 列）：{header}");
        }
    }

    [Fact]
    public void NumberingFollowsTheFullListNotTheFilteredOne()
    {
        // 编号必须按**完整列表**算：/model <编号> 是按完整列表解析的。
        // 过滤只影响显示哪些行；若编号跟着过滤重排，用户选了 3 号却切到另一个模型。
        var filtered = Program.NumberedModels(Models, "gpt");
        Assert.Single(filtered);
        Assert.Equal(3, filtered[0].Num); // gpt-5-codex 在完整列表里是第 3 个
        Assert.Equal("gpt-5-codex", filtered[0].Model);
    }
}
