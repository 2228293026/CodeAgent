using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 流式渲染的跨边界状态：反引号连续计数（围栏/行内代码的判定依据）不能跨行、跨消息。
/// 回归点：行尾单个 ` 与下一行/下一条消息开头的 ` 会凑成 2 或 3，于是
/// 「``` 围栏」被误开成代码块（后续正文整段被当代码输出），或普通反引号被误判成行内代码。
/// </summary>
[Collection("ConsoleOutput")]
public class StreamingBoundaryTests
{
    [Fact]
    public void BacktickRun_DoesNotSpanLines()
    {
        // 行尾一个 ` 会把下一行行首的 `` 凑成 3 个 → 误开围栏，整行内容被当代码原样输出（含反引号）。
        // 计数按行重置后，``内联`` 是正常的行内代码，反引号成对剥离，只剩「内联」。
        var output = Render("行尾`\n``内联``\n");
        var lines = output.Replace("\r\n", "\n").Split('\n');
        Assert.Contains("内联", lines);
    }

    [Fact]
    public void BacktickRun_DoesNotSpanMessages()
    {
        var original = Console.Out;
        var writer = new System.IO.StringWriter();
        Console.SetOut(writer);
        try
        {
            var renderer = new ConsoleRenderer(enabled: true);
            renderer.Append("上一条结尾`");
            renderer.Flush();
            renderer.Append("``下一条开头\n");
            renderer.Flush();
        }
        finally
        {
            Console.SetOut(original);
        }
        var output = writer.ToString();
        // 计数按消息重置后：不会被上一条结尾的 ` 凑成围栏（那样整行会变成代码块），
        // 而是正常的行内代码——反引号成对剥离，只剩正文。
        var lines = output.Replace("\r\n", "\n").Split('\n');
        Assert.Contains("下一条开头", lines);
    }

    [Fact]
    public void RealFenceSplitAcrossChunks_StillDetected()
    {
        var original = Console.Out;
        var writer = new System.IO.StringWriter();
        Console.SetOut(writer);
        try
        {
            var renderer = new ConsoleRenderer(enabled: true);
            // 围栏分两个 chunk 到达：`` + ` —— 同一行内连续，仍应识别为围栏
            renderer.Append("``");
            renderer.Append("`cs\nvar x = 1;\n```\n");
            renderer.Flush();
        }
        finally
        {
            Console.SetOut(original);
        }
        var output = writer.ToString();
        Assert.Contains("var x = 1;", output);
        // 语言标注单独成徽标行（UI Round 61 起），但**不能混进代码正文**：
        // 断言的是「代码那一行里没有 cs」，不是「整个输出里没有 cs」。
        var codeLine = System.Linq.Enumerable.First(
            output.Split('\n'), l => l.Contains("var x = 1;", StringComparison.Ordinal));
        Assert.DoesNotContain("cs", codeLine);
    }

    [Fact]
    public void InlineCodeOnSingleLine_StillDetected()
    {
        // 固定在「颜色开启」：关掉颜色时反引号会**保留**（语义不丢），
        // 那条路径由 InlineStyleWithoutColorTests 单独覆盖。
        using var colour = ColourTestScope.On();
        var output = Render("用 `code` 标记\n");
        Assert.Contains("用 code 标记", output);
    }

    private static string Render(string text)
    {
        var original = Console.Out;
        var writer = new System.IO.StringWriter();
        Console.SetOut(writer);
        try
        {
            var renderer = new ConsoleRenderer(enabled: true);
            renderer.Append(text);
            renderer.Flush();
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }
}
