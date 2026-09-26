using System;
using System.IO;
using System.Text.RegularExpressions;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 常驻提示行：当前文件访问权限 + 切换方式。
///
/// 回归点：此前权限模式**只在切换的那一刻**打一行。切完 20 轮之后屏幕底部早就被
/// 输出冲走了，用户想确认「我现在是不是 full 模式」只能翻历史，或者干脆不敢确认——
/// 而这恰恰是最需要随时可查的那个设置。
/// </summary>
[Collection("ConsoleOutput")]
public class HintLineTests
{
    [Theory]
    [InlineData("strict", "仅工作区可读写")]
    [InlineData("whitelist", "工作区读写 + 只读白名单")]
    [InlineData("full", "所有文件可读可写")]
    public void EveryAccessLevelIsExplained(string mode, string desc)
    {
        var line = Program.BuildHintLine(mode, 120);
        Assert.Contains(mode, line);
        Assert.Contains(desc, line);
        Assert.Contains("Shift+Tab", line);
    }

    [Fact]
    public void UnknownModeIsShownVerbatimNotDropped()
    {
        // 不认识的级别要**原样显示**：悄悄换成一句说明会让用户以为模式生效了
        var line = Program.BuildHintLine("paranoid", 120);
        Assert.Contains("paranoid", line);
        Assert.Contains("Shift+Tab", line);
    }

    [Fact]
    public void TheDescriptionIsCaseInsensitive()
    {
        // 模式名**原样显示**（配置里是什么就显示什么，不替用户改写）；
        // 但说明文字必须跟着实际级别走——早先把大小写敏感的匹配写错了会让
        // STRICT 落到 default 分支，显示成一句没有意义的模式名。
        Assert.Contains("仅工作区可读写", Program.BuildHintLine("STRICT", 120));
        Assert.Contains("所有文件可读可写", Program.BuildHintLine("Full", 120));
        Assert.Contains("工作区读写 + 只读白名单", Program.BuildHintLine("Whitelist", 120));
    }

    [Fact]
    public void EmptyModePrintsNothing()
    {
        // 空行比"⏵⏵  · Shift+Tab 切换"更难扫读——没有信息的一行是纯噪声
        Assert.Equal(string.Empty, Program.BuildHintLine("", 120));
        Assert.Equal(string.Empty, Program.BuildHintLine(null!, 120));
    }

    [Fact]
    public void StartsWithTwoMarks()
    {
        // 两个标记而不是一个：和状态栏的单个 ⏵ 区分开，一眼知道这行不是数据
        var line = Program.BuildHintLine("strict", 120);
        Assert.StartsWith("⏵⏵ ", line);
    }

    [Fact]
    public void NeverOverflowsAtAnyWidth()
    {
        foreach (var w in new[] { 1, 3, 8, 14, 20, 30, 60, 120 })
        {
            var line = Program.BuildHintLine("whitelist", w);
            Assert.False(string.IsNullOrEmpty(line), $"宽度 {w} 输出为空");
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"宽度 {w} 溢出: {TextUtil.DisplayWidth(line)}");
        }
    }

    [Fact]
    public void UnknownWidthOmitsTruncation()
    {
        var line = Program.BuildHintLine("full", 0);
        Assert.Contains("所有文件可读可写", line);
    }

    [Fact]
    public void AsciiModeUsesTheAsciiMark()
    {
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
            Assert.StartsWith(">> strict", Program.BuildHintLine("strict", 120));
        }
        finally { SafeColor.ReadEnv = saved; }
    }

    [Fact]
    public void TheTurnSummaryActuallyPrintsIt()
    {
        // 源码守卫：只测 builder 会漏掉「没接进每轮结束」这一步
        var source = ReadSource();
        Assert.Matches(@"(?s)BuildFooterBar\(.*BuildHintLine\(agent\.Context\.Config\.FileAccess", source);
    }

    private static string ReadSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return File.ReadAllText(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
