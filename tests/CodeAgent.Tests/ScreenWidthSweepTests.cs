using System;
using System.Collections.Generic;
using System.Linq;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 整屏宽度守卫：把各界面的渲染在**一串宽度**上扫一遍，任何一行越界都算失败。
///
/// 为什么值得单独写：每个渲染函数自己都号称「按宽度收口」，但收口是逐段算的——
/// 提示符 + 状态栏 + 工具行 + 面板 + 边框，各段单独看都安全，拼起来却可能超。
/// 而超一列就会让终端自动折行，折行会把整块基线算错：光标落到错误的行、
/// 边框断成两截、下一次重绘在屏幕上堆垃圾。
///
/// 这类缺陷在 120 列的开发机上永远看不出来，只在用户的 80 列（甚至 40 列分屏）里出现。
/// </summary>
[Collection("ConsoleOutput")]
public class ScreenWidthSweepTests
{
    /// <summary>常见终端宽度：40 分屏、80 默认、120 宽屏、200 超大屏，外加若干边界。</summary>
    private static readonly int[] Widths =
        [20, 24, 30, 32, 40, 48, 60, 72, 80, 100, 120, 160, 200];

    [Fact]
    public void StatusBarNeverOverflows()
    {
        foreach (var w in Widths)
        {
            var line = Program.BuildStatusBar(
                "code", "deepseek-v4.1-flash", @"D:\Projects\CodeAgent\src\CodeAgent\Agent",
                "main", "12345", "6789", "ctx 42.1k/128.0k (33%)", "high", w, "12.3k", "▁▂▄▆█ 42.1 t/s");
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"状态栏在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
        }
    }

    [Fact]
    public void PromptNeverOverflows()
    {
        foreach (var w in Widths)
        {
            var text = Program.BuildPromptText("code", "deepseek-v4.1-flash", @"D:\Projects\CodeAgent", w);
            foreach (var line in text.Split('\n'))
            {
                Assert.True(TextUtil.DisplayWidth(line) <= w,
                    $"提示符在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
            }
        }
    }

    [Fact]
    public void InputFrameNeverOverflows()
    {
        foreach (var w in Widths)
        {
            var top = Program.BuildInputFrameTop("> ", "high", w);
            var bottom = Program.BuildInputFrameBottom(w);
            foreach (var line in top.Split('\n').Concat([bottom]))
            {
                Assert.True(TextUtil.DisplayWidth(line) <= w,
                    $"输入框在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
            }
        }
    }

    [Fact]
    public void TurnSummaryNeverOverflows()
    {
        foreach (var w in Widths)
        {
            var line = Program.BuildTurnSummary(
                rounds: 12, toolCalls: 34, elapsed: "1h2m5s",
                tokenText: "↑ 128.4k", thinkText: "思考 42s", cacheText: "缓存 88%",
                costText: "≈$1.23", width: w);
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"回合摘要在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
        }
    }

    [Fact]
    public void TaskPanelNeverOverflows()
    {
        var tasks = new List<AgentTask>
        {
            new("重构渲染层", TaskStatus.Completed),
            new("补上窄终端下的宽度收口与折叠提示", TaskStatus.InProgress),
            new("跑全量测试", TaskStatus.Pending),
        };
        foreach (var w in Widths)
        {
            var panel = Program.FormatTaskPanel(tasks, w);
            foreach (var line in panel.Split('\n'))
            {
                Assert.True(TextUtil.DisplayWidth(line) <= w,
                    $"任务面板在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
            }
        }
    }

    [Fact]
    public void GoalLineNeverOverflows()
    {
        foreach (var w in Widths)
        {
            var line = Program.FormatTaskProgressLine(3, 5, w, "重构渲染层的宽度收口与折叠提示", TimeSpan.FromSeconds(120));
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"目标行在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
        }
    }

    [Fact]
    public void ToolStatusLineNeverOverflows()
    {
        foreach (var w in Widths)
        {
            var line = CodeAgent.Agent.Agent.FormatToolStatusLine(
                "Run(dotnet build -c Release --no-incremental)", isError: false,
                TimeSpan.FromSeconds(12.5), w);
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"工具状态行在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
        }
    }

    [Fact]
    public void SpinnerLineNeverOverflows()
    {
        foreach (var w in Widths)
        {
            var line = CodeAgent.Agent.Agent.BuildSpinnerLine(
                SafeColor.Glyphs.SpinnerFrame(0), TimeSpan.FromSeconds(42), 12345,
                CodeAgent.Agent.Agent.SpinnerHint(w), w);
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"spinner 行在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
        }
    }

    [Fact]
    public void ToolProgressLineNeverOverflows()
    {
        foreach (var w in Widths)
        {
            var line = CodeAgent.Agent.Agent.FormatToolProgressLine(
                SafeColor.Glyphs.SpinnerFrame(0), "Run(dotnet test)", TimeSpan.FromSeconds(125), w);
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"工具进度行在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
        }
    }

    [Fact]
    public void FoldHintNeverOverflows()
    {
        foreach (var w in Widths)
        {
            var text = InputLine.FoldText("a\nb\nc\nd\ne\nf\ng\nh", windowWidth: w);
            foreach (var line in text.Split('\n'))
            {
                Assert.True(TextUtil.DisplayWidth(line) <= Math.Max(w, 1),
                    $"折叠提示在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
            }
        }
    }

    [Fact]
    public void ToolOutputPreviewNeverOverflows()
    {
        var output = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line {i}: {new string('x', 200)}"));
        foreach (var w in Widths)
        {
            var text = CodeAgent.Agent.Agent.FormatToolOutputPreview(output, width: w);
            foreach (var line in text.Split('\n'))
            {
                Assert.True(TextUtil.DisplayWidth(line) <= w,
                    $"工具输出预览在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
            }
        }
    }

    [Fact]
    public void ChangedFilesLineNeverOverflows()
    {
        var files = Enumerable.Range(0, 8).Select(i => $"src/CodeAgent/SomeVeryLongDirectoryName/File{i}.cs").ToList();
        foreach (var w in Widths)
        {
            var line = Program.FormatChangedFilesLine(files, w);
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"改动文件行在 {w} 列下渲染出 {TextUtil.DisplayWidth(line)} 列：{line}");
        }
    }
}