using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 流式结束与后续输出的衔接。
/// 回归点：流式路径此前**无条件**补一个换行——模型最后一段本来就带换行时会多出空行；
/// 而没带换行时又必须补，否则工具状态行/回合摘要会粘在正文同一行。
/// </summary>
[Collection("ConsoleOutput")]
public class StreamLineBoundaryTests
{
    private static string Capture(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { action(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }

    [Fact]
    public void PrintResult_StreamedMidLine_EndsWithNewline()
    {
        var output = Capture(() => PrintResultVia("答复正文", true, true, streamedOnLineBoundary: false));
        Assert.Equal(Environment.NewLine, output);
    }

    [Fact]
    public void PrintResult_StreamedOnLineBoundary_AddsNoBlankLine()
    {
        // 模型最后一段已带换行：再补一个就会多出一个空行
        var output = Capture(() => PrintResultVia("答复正文\n", true, true, streamedOnLineBoundary: true));
        Assert.Equal("", output);
    }

    [Fact]
    public void PrintResult_StreamedEmptyResult_WritesNothing()
    {
        var output = Capture(() => PrintResultVia("", true, true, streamedOnLineBoundary: false));
        Assert.Equal("", output);
    }

    [Fact]
    public void PrintResult_NotStreamed_PrintsWholeText()
    {
        var output = Capture(() => PrintResultVia("答复正文", false, prefixNewline: true, streamedOnLineBoundary: false));
        Assert.Contains("答复正文", output);
        // prefixNewline 用的是字面 LF（与既有行为一致），不是平台的 Environment.NewLine
        Assert.StartsWith("\n", output);
    }

    [Fact]
    public void PrintResult_NotStreamedWithoutPrefix_StartsAtText()
    {
        var output = Capture(() => PrintResultVia("答复正文", false, prefixNewline: false, streamedOnLineBoundary: false));
        Assert.StartsWith("答复正文", output);
    }

    [Fact]
    public void Renderer_ReportsLineBoundaryAfterCompleteLine()
    {
        var renderer = new ConsoleRenderer(true);
        var output = Capture(() => renderer.Append("第一行\n"));
        Assert.Contains("第一行", output);
        Assert.True(renderer.EndsOnLineBoundary, "整行输出后应停在行边界");
    }

    [Fact]
    public void Renderer_ReportsPartialLineAsNotOnBoundary()
    {
        var renderer = new ConsoleRenderer(true);
        Capture(() => renderer.Append("半行"));
        Assert.False(renderer.EndsOnLineBoundary, "半行未结束，必须补换行");
    }

    [Fact]
    public void Renderer_FlushLeavesNothingPending()
    {
        var renderer = new ConsoleRenderer(true);
        Capture(() =>
        {
            renderer.Append("半行");
            renderer.Flush();
        });
        Assert.True(renderer.EndsOnLineBoundary, "Flush 后不应再有挂起的半行");
    }

    [Fact]
    public void Renderer_DisabledRendererHasNoPendingState()
    {
        var renderer = new ConsoleRenderer(false);
        Capture(() => renderer.Append("任意内容"));
        Assert.True(renderer.EndsOnLineBoundary);
    }

    private static void PrintResultVia(string result, bool streamed, bool prefixNewline, bool streamedOnLineBoundary) =>
        Program.PrintResultForTest(result, streamed, prefixNewline, streamedOnLineBoundary);
}
