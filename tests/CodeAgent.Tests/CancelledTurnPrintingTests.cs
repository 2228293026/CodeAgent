using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 取消哨兵 <c>ESC CANCELLED_TURN</c> 绝不能原样打到屏幕上。
///
/// 一次性模式与 /compact 各自把哨兵映射成了显示文本，唯独**交互式回合**没映射：
/// 它撤回了这一轮、把草稿还给用户，然后把哨兵交给 <c>PrintResult</c>。
/// 本轮还没吐过任何内容就按 Esc 时 streamed=false，于是屏幕上真的会出现
/// <c>CANCELLED_TURN</c> —— 内部实现细节直接怼到用户脸上。
/// </summary>
public sealed class CancelledTurnPrintingTests
{
    private static string Capture(Action act)
    {
        var original = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { act(); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }

    private const string Sentinel = "\u001bCANCELLED_TURN";

    [Fact]
    public void SentinelIsRecognized()
    {
        Assert.True(Program.IsCancelledTurn(Sentinel));
        Assert.False(Program.IsCancelledTurn("已取消。"));
        Assert.False(Program.IsCancelledTurn(null));
    }

    [Fact]
    public void ModelTextMentioningCancelIsNotMistakenForIt()
    {
        // 哨兵存在的意义：模型自己说"已取消"不能被当成取消
        Assert.False(Program.IsCancelledTurn("好的，我已取消这个操作"));
        Assert.False(Program.IsCancelledTurn("CANCELLED_TURN"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrintResultNeverEmitsTheRawSentinel(bool streamed)
    {
        // 核心回归：未流式（还没吐过内容就按 Esc）会把哨兵原样打出去
        var outp = Capture(() => Program.PrintResultForTest(Sentinel, streamed, prefixNewline: false, streamedOnLineBoundary: false));
        Assert.DoesNotContain("CANCELLED_TURN", outp);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SentinelProducesTheStandardCancelLine(bool streamed, bool prefixNewline)
    {
        var outp = Capture(() => Program.PrintResultForTest(Sentinel, streamed, prefixNewline, streamedOnLineBoundary: false));
        Assert.Contains("已取消", outp);
    }

    [Fact]
    public void OrdinaryTextStillPrintsNormally()
    {
        // 收口不能误伤正常回复
        var outp = Capture(() => Program.PrintResultForTest("正常回复", streamed: false, prefixNewline: false, streamedOnLineBoundary: false));
        Assert.Contains("正常回复", outp);
    }

    [Fact]
    public void EveryCallSiteGoesThroughPrintResult()
    {
        // 三个调用点（一次性、/compact、交互式回合）都应把哨兵交给 PrintResult
        // 或自己映射，绝不能有一处把哨兵直接丢给 Console.WriteLine
        var src = File.ReadAllText(FindSource());
        Assert.Contains("if (IsCancelledTurn(result))\n        {\n            Console.WriteLine((prefixNewline", src.Replace("\r\n", "\n"));
        // 一次性模式那处已收敛到 PrintResult，不再各写一份措辞
        Assert.DoesNotContain("result = \"\\n\" + FormatCancelLine", src);
    }

    private static string FindSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
