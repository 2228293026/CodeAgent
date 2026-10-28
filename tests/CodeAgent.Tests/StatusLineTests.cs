using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 状态栏外部命令（/statusline）：用户自己的脚本输出并进状态栏末段。
///
/// 这里守的是三件容易悄悄坏掉的事：脚本输出必须能安全上屏（ANSI/换行/超宽会撕裂整行）、
/// 失败必须安静地退化成空段（不能让状态栏抛异常），
/// 以及窄终端下它必须**先于**内置信息被裁掉（ctx 和路径比装饰重要）。
/// </summary>
public sealed class StatusLineTests
{
    [Fact]
    public void FormatSegmentKeepsOnlyTheFirstNonEmptyLine()
    {
        Assert.Equal("node 20.11", StatusLine.FormatSegment("\n\nnode 20.11\nsecond line"));
    }

    [Fact]
    public void FormatSegmentStripsAnsiAndControlCharacters()
    {
        // 脚本里带颜色是常态；原样上屏会把后续内容吞掉（颜色状态泄漏到整行）。
        Assert.Equal("ok 3", StatusLine.FormatSegment("\u001b[32mok\u001b[0m\t3"));
    }

    [Fact]
    public void FormatSegmentCollapsesWhitespace()
    {
        Assert.Equal("a b c", StatusLine.FormatSegment("  a   b\t\tc  "));
    }

    [Fact]
    public void FormatSegmentReturnsEmptyForBlankInput()
    {
        Assert.Equal(string.Empty, StatusLine.FormatSegment(null));
        Assert.Equal(string.Empty, StatusLine.FormatSegment("   \n\t "));
    }

    [Fact]
    public void FormatSegmentTruncatesVeryLongOutput()
    {
        var segment = StatusLine.FormatSegment(new string('x', 500));
        Assert.True(segment.Length <= StatusLine.MaxSegmentChars + 1, $"实际 {segment.Length}");
        Assert.EndsWith("…", segment);
    }

    [Fact]
    public void BuildPayloadCarriesSessionFacts()
    {
        var payload = StatusLine.BuildPayload("code", "gpt-5", "/repo", "main");
        Assert.Contains("gpt-5", payload, StringComparison.Ordinal);
        Assert.Contains("main", payload, StringComparison.Ordinal);
        Assert.Contains("code", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncReturnsTheScriptsStdout()
    {
        var segment = await StatusLine.RunAsync("echo ok", Shell(), ".", 5, "{}", CancellationToken.None);
        Assert.Equal("ok", segment);
    }

    [Fact]
    public async Task RunAsyncReturnsEmptyOnNonZeroExitWithoutOutput()
    {
        Assert.Equal(string.Empty, await StatusLine.RunAsync("exit 3", Shell(), ".", 5, "{}", CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncReturnsEmptyOnUnknownCommandInsteadOfThrowing()
    {
        // 命令写错是最常见的配置失误：状态栏必须安静退化成空段，不能整行崩掉。
        var segment = await StatusLine.RunAsync("definitely-not-a-real-command-xyz", Shell(), ".", 5, "{}", CancellationToken.None);
        Assert.Equal(string.Empty, segment);
    }

    [Fact]
    public void ExtraSegmentRidesAtTheEndOfTheStatusBar()
    {
        var bar = Program.BuildStatusBar("code", "m", "D:/P", "main", "0", "0", "ctx 1%", "think", 120, "", "", "node 20");
        Assert.EndsWith("node 20", bar);
    }

    [Fact]
    public void ExtraSegmentIsDroppedBeforeBuiltinInformation()
    {
        const int width = 40;
        var without = Program.BuildStatusBar("code", "m", "D:/P", "main", "0", "0", "ctx 1%", "think", width);
        var with = Program.BuildStatusBar("code", "m", "D:/P", "main", "0", "0", "ctx 1%", "think", width, "", "", "a-very-long-custom-segment");
        Assert.DoesNotContain("a-very-long-custom-segment", with);
        Assert.Contains("m", with);
        Assert.True(CodeAgent.TextUtil.DisplayWidth(with) <= width, $"实际 {CodeAgent.TextUtil.DisplayWidth(with)}");
        Assert.NotEmpty(without);
    }

    [Fact]
    public void FormatStatusLineReportSaysWhatToCheckWhenThereIsNoOutput()
    {
        var lines = Program.FormatStatusLineReport("my-status.sh", string.Empty, 100);
        Assert.Contains(lines, l => l.Contains("没有输出"));
        Assert.Contains(lines, l => l.Contains("手动执行"));
    }

    [Fact]
    public void FormatStatusLineReportShowsTheLiveSegment()
    {
        var lines = Program.FormatStatusLineReport("my-status.sh", "node 20", 100);
        Assert.Contains(lines, l => l.Contains("node 20"));
    }

    [Fact]
    public void FormatStatusLineReportExplainsHowToTurnItOn()
    {
        var lines = Program.FormatStatusLineReport(string.Empty, string.Empty, 100);
        Assert.Contains(lines, l => l.Contains("/statusline"));
    }

    [Fact]
    public async Task SegmentAsyncFollowsTheConfiguration()
    {
        // 守的是「配置了到底有没有显示」：PrintStatusBar 只负责把它塞进状态栏。
        var off = new AgentConfig { StatusLine = "" };
        Assert.Equal(string.Empty, await StatusLine.SegmentAsync(off, "code", "m", ".", null));

        var on = new AgentConfig { StatusLine = "echo ok", Shell = Shell(), StatusLineTimeoutSeconds = 5 };
        Assert.Equal("ok", await StatusLine.SegmentAsync(on, "code", "m", ".", null));
    }

    private static string Shell() =>
        OperatingSystem.IsWindows() ? "cmd" : "bash";
}
