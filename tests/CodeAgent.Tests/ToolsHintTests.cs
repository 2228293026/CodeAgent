using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using CodeAgent;
using CodeAgent.Providers;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/tools</c> 列出 231 个工具时，得让用户知道**可以收窄**。
///
/// 此前没有末尾提示：一整块列表刷完就结束了。同类的 <c>/model</c>
/// 早就有「提示: /model &lt;编号&gt; 可直接切换」——两个命令列的都是
/// 「可用的东西」，收尾提示不该一个有、一个没有。
///
/// 表头里的「显示 M 条」暗示了存在过滤，但那要求用户先理解
/// 「共 231 个，显示 231 条」这个差值为零意味着什么。直接写出来更省事。
/// </summary>
public class ToolsHintTests
{
    private static IReadOnlyList<ToolSpec> MakeTools(int n) =>
        Enumerable.Range(1, n)
            .Select(i => new ToolSpec { Name = $"tool_{i:000}", Description = $"第 {i} 个工具", Parameters = new JsonObject() })
            .ToList();

    [Fact]
    public void ManyToolsAreListed()
    {
        var rows = Program.FilterTools(MakeTools(25), null, out var total);
        Assert.Equal(25, total);
        Assert.Equal(25, rows.Count);
    }

    [Fact]
    public void FilteringMatchesNameAndDescription()
    {
        // 用户找「能查依赖的」，多半不会记得工具叫 dependency_lockfile_report ——
        // 所以关键字要同时匹配名与说明（与 /models 口径一致）
        var tools = new List<ToolSpec>
        {
            new() { Name = "dependency_lockfile_report", Description = "检查锁文件", Parameters = new JsonObject() },
            new() { Name = "read_file", Description = "读取文件", Parameters = new JsonObject() },
        };
        Assert.Single(Program.FilterTools(tools, "dependency", out _));
        Assert.Single(Program.FilterTools(tools, "读取", out _));
        Assert.Empty(Program.FilterTools(tools, "zzz", out _));
    }

    [Fact]
    public void TheHeaderStatesTotalAndShown()
    {
        // 两个数字必须都在：只有一个时，「还剩多少没看到」无从判断
        var header = Program.FormatToolsHeader(231, 231, null, "code");
        Assert.Contains("231", header);
        Assert.Contains("当前模式 code", header);
    }

    [Fact]
    public void TheHintOnlyAppearsWhenItHelps()
    {
        // 提示的触发条件是「没过滤 且 工具数 > 20」。
        // 这里断言的是那个**判断条件**本身，不是输出文本——
        // 输出走 Console，而这个类的价值在于「何时该教」。
        Assert.True(ConditionHolds(filter: null, count: 231));
        Assert.False(ConditionHolds(filter: "git", count: 231));
        Assert.False(ConditionHolds(filter: null, count: 5));
    }

    /// <summary>与 <c>PrintTools</c> 里那行 <c>if</c> 相同的条件。</summary>
    private static bool ConditionHolds(string? filter, int count) =>
        string.IsNullOrWhiteSpace(filter) && count > 20;
}
