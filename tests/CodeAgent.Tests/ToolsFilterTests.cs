using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/tools [关键字]</c>。
///
/// 此前只有一份 **709 行**的裸列表：没有表头、没有总数、不能过滤。
/// 231 个工具里想找一个，只能从顶上翻到底。而同类的 <c>/models [关键字]</c>
/// 早就有过滤与计数——两个命令列的都是"可用的东西"，口径不该差这么多。
/// </summary>
public sealed class ToolsFilterTests
{
    private static List<CodeAgent.Providers.ToolSpec> Sample() =>
    [
        new() { Name = "read_file", Description = "读取工作区文件", Parameters = new System.Text.Json.Nodes.JsonObject() },
        new() { Name = "write_file", Description = "创建新文件或整体覆盖", Parameters = new System.Text.Json.Nodes.JsonObject() },
        new() { Name = "gitignore_pattern_report", Description = "只读发现 .gitignore 文件，统计有效模式", Parameters = new System.Text.Json.Nodes.JsonObject() },
        new() { Name = "git_status", Description = "查看仓库状态与分支", Parameters = new System.Text.Json.Nodes.JsonObject() },
        new() { Name = "dependency_lockfile_report", Description = "检查依赖版本是否锁死", Parameters = new System.Text.Json.Nodes.JsonObject() },
    ];

    [Fact]
    public void NoFilter_ReturnsEverythingSortedByName()
    {
        var rows = Program.FilterTools(Sample(), null, out var total);
        Assert.Equal(5, total);
        Assert.Equal(5, rows.Count);
        Assert.Equal(rows.Select(r => r.Command).OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            rows.Select(r => r.Command));
    }

    [Fact]
    public void EmptyFilterBehavesLikeNoFilter()
    {
        var rows = Program.FilterTools(Sample(), "   ", out var total);
        Assert.Equal(5, total);
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void FilterMatchesTheToolName()
    {
        var rows = Program.FilterTools(Sample(), "git", out var total);
        Assert.Equal(5, total); // 总数不变，仍是全部可用工具
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Command == "git_status");
    }

    [Fact]
    public void FilterAlsoMatchesTheDescription()
    {
        // 用户想找"能看依赖的"，多半不记得工具叫 dependency_lockfile_report
        var rows = Program.FilterTools(Sample(), "依赖", out _);
        Assert.Single(rows);
        Assert.Equal("dependency_lockfile_report", rows[0].Command);
    }

    [Fact]
    public void FilterIsCaseInsensitive()
    {
        Assert.Equal(2, Program.FilterTools(Sample(), "GIT", out _).Count);
    }

    [Fact]
    public void NoMatchYieldsAnEmptyListNotAnError()
    {
        var rows = Program.FilterTools(Sample(), "zzzz", out var total);
        Assert.Empty(rows);
        Assert.Equal(5, total); // 仍然告诉用户"一共有多少"
    }

    [Fact]
    public void Header_StatesTotalShownAndFilter()
    {
        Assert.Equal("可用工具（当前模式 code，共 231 个，显示 4 条，过滤 “git”）:",
            Program.FormatToolsHeader(231, 4, "git", "code"));
    }

    [Fact]
    public void Header_WithoutFilterOmitsTheFilterClause()
    {
        Assert.Equal("可用工具（当前模式 code，共 231 个，显示 231 条）:",
            Program.FormatToolsHeader(231, 231, null, "code"));
    }

    [Fact]
    public void Header_IsWidthBounded()
    {
        // 模式名来自配置文件、过滤词是用户任意输入，两者都不受控
        foreach (var w in new[] { 30, 40, 60, 80, 120 })
        {
            var line = Program.FormatToolsHeader(231, 4, new string('很', 200), new string('x', 60), w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"宽度 {w} 超宽");
        }
    }

    [Fact]
    public void BothHelpListsAdvertiseTheArgument()
    {
        // 命令清单与 Tab 补全菜单必须同步（CommandListSyncTests 守着两处相等）
        var program = ReadSource(FindProgram());
        var input = ReadSource(FindInputLine());
        Assert.Contains("/tools\", \"列出可用工具（可带关键字过滤）", program);
        Assert.Contains("/tools\", \"列出可用工具（可带关键字过滤）", input);
    }

    [Fact]
    public void TheCommandRoutesThroughTheNewHelper()
    {
        // 只测纯函数会漏掉"/tools 压根没用它"——那正是原来 709 行裸列表的原因
        var src = ReadSource(FindProgram());
        Assert.Contains("PrintTools(agent.ToolsForMode(), agent.CurrentMode.Name, rest);", src);
        Assert.Contains("internal static IReadOnlyList<HelpEntry> FilterTools(", src);
    }

    private static string ReadSource(string p) => File.ReadAllText(p).Replace("\r\n", "\n");

    private static string FindProgram() => Find("Program.cs");
    private static string FindInputLine() => Find("InputLine.cs");

    private static string Find(string fileName)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(d, "src", "CodeAgent", fileName);
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/{fileName}");
    }
}
