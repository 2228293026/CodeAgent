using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>type bug.log | codeagent "分析"</c> —— 管道进来的日志被**单次截断**，
/// 保留的只有**开头**，而日志最关键的内容几乎总在**末尾**。
///
/// 实测 318,893 字符的日志：
///
///   <c>… line 1\n…(共 318893 字符，已截断)</c>
///
/// 出来的 100,000 字符以 <c>line 1</c> 收尾——第 2 行到第 30,000 行**全部消失**，
/// 而那里正是堆栈、报错、复现步骤所在。模型会拿着"只有开头"的日志做诊断，
/// 然后给出一个基于残缺输入的结论。219,859 个字符就这么没了。
///
/// 修法两条：
///   1. **头尾都保留**（<c>TruncateHeadTail</c>）——末尾的错误信息必须活着
///   2. **说明放在开头**——放末尾的话，模型很容易把 <c>…(共 N 字符，已截断)</c>
///      当成日志的最后一行内容
/// </summary>
public sealed class PipedStdinKeepsTheTailTests
{
    private static string BigLog(int lines) =>
        string.Join('\n', Enumerable.Range(1, lines).Select(i => $"line {i}"));

    [Fact]
    public void TheEndOfTheLogSurvives()
    {
        var log = BigLog(30_000);
        var composed = Program.ComposeTaskWithStdin("分析", log);
        // 旧实现在这里是 "line 1" 收尾——第 2~30000 行全丢
        Assert.Contains("line 30000", composed);
        Assert.DoesNotContain("\nline 1\n…(共", composed);
    }

    [Fact]
    public void TheStartOfTheLogSurvives()
    {
        Assert.Contains("line 1\n", Program.ComposeTaskWithStdin("分析", BigLog(30_000)));
    }

    [Fact]
    public void TheDisclosureSaysHowMuchWasKept()
    {
        var log = BigLog(30_000);
        var composed = Program.ComposeTaskWithStdin("分析", log);
        // 说明必须在**开头**（第二行附近），否则会被当成日志内容的最后一行
        var head = composed[..Math.Min(400, composed.Length)];
        Assert.Contains("中间省略", head);
        Assert.Contains("原始", head);
        Assert.DoesNotContain("…(共", composed);   // 旧的那种末尾标记不再出现
    }

    [Fact]
    public void ShortInputIsPassedThroughUntouched()
    {
        var log = "line 1\nline 2";
        var composed = Program.ComposeTaskWithStdin("分析", log);
        Assert.Equal("分析\n\n[stdin 输入]\n" + log, composed);
        Assert.DoesNotContain("省略", composed);
    }

    [Fact]
    public void EmptyStdinLeavesTheTaskAlone()
    {
        Assert.Equal("分析", Program.ComposeTaskWithStdin("分析", ""));
        Assert.Equal("分析", Program.ComposeTaskWithStdin("分析", "   \n  "));
    }

    [Fact]
    public void TheKeptSizeIsReal()
    {
        // 说明里报的数字必须是**真实保留量**，不能是标称上限
        var log = BigLog(30_000);
        var composed = Program.ComposeTaskWithStdin("分析", log);
        var m = System.Text.RegularExpressions.Regex.Match(composed, @"已保留头部与尾部共 ([\d,]+) 字符");
        Assert.True(m.Success, $"说明里没有保留量：{composed[..Math.Min(300, composed.Length)]}");
        var claimed = long.Parse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal));
        Assert.True(claimed <= 100_000 + 200, $"保留量 {claimed} 超过上限，说明在说谎");
        // 至少保住了两端，说明这个数不是随口写的
        Assert.True(claimed > 90_000, $"只保留了 {claimed} 字符，头尾策略可能没生效");
    }

    [Fact]
    public void TrailingWhitespaceIsTrimmedFirst()
    {
        // 与旧行为保持一致：先 TrimEnd 再算长度，避免把结尾空行算进"原始"
        var composed = Program.ComposeTaskWithStdin("t", "line 1\n\n\n");
        Assert.EndsWith("[stdin 输入]\nline 1", composed);
    }
}
