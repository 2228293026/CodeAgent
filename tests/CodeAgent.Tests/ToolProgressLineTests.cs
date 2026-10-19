using System;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具执行期间的进度动画：<c>⠸ Run build … 12s</c>。
///
/// 此前工具开始行打印完之后屏幕就**完全静默**了：模型思考时有 spinner，
/// 等工具执行时反而什么都没有。一次 <c>dotnet build</c> 跑五分钟，
/// 用户看到的是一个不动的终端——分不清是在跑还是卡死了，也分不清等了多久。
///
/// 两个容易写歪的地方，各有测试钉住：
/// · 降级顺序必须是「先丢名字、再丢秒表、最后只剩帧」。反过来先丢动画帧，
///   得到的是一行静止文字——而那看起来正像卡死。
/// · 短工具不该闪动画：700ms 的延迟就是为此。
/// </summary>
/// <remarks>
/// 在 <c>ConsoleOutput</c> 集合里：字形来自 <c>SafeColor.Glyphs</c>（读进程级可替换的
/// <c>SafeColor.ReadEnv</c>）。
/// </remarks>
[Collection("ConsoleOutput")]
public class ToolProgressLineTests
{
    private static string Frame => SafeColor.Glyphs.SpinnerFrame(0);

    [Fact]
    public void ShowsFrameLabelAndElapsedWhenThereIsRoom()
    {
        var line = AgentClass.FormatToolProgressLine(Frame, "Run build", TimeSpan.FromSeconds(12), 120);
        Assert.Contains("Run build", line);
        Assert.Contains(TextUtil.FormatSessionTime(TimeSpan.FromSeconds(12)), line);
        Assert.StartsWith(Frame, line, StringComparison.Ordinal);
    }

    [Fact]
    public void DropsTheLabelBeforeTheTimer()
    {
        // 窄一点时先丢工具名：动画与秒表还在，用户仍知道「在跑、跑了多久」
        var line = AgentClass.FormatToolProgressLine(Frame, "Run a very long command", TimeSpan.FromSeconds(125), 12);
        Assert.DoesNotContain("command", line);
        Assert.Contains("2m", line);
    }

    [Fact]
    public void FallsBackToTheBareFrameAtTheTiniestWidths()
    {
        // 连秒表都放不下时只留动画帧——它本身 1 列，永远不会越界
        var line = AgentClass.FormatToolProgressLine(Frame, "Run build", TimeSpan.FromSeconds(125), 1);
        Assert.Equal(Frame, line);
    }

    [Fact]
    public void NeverExceedsTheGivenWidth()
    {
        // 1..120 全扫：越界一次就会折行，而折行会把输入框基线算错
        for (var w = 1; w <= 120; w++)
        {
            var line = AgentClass.FormatToolProgressLine(Frame, "Run a very long command name here", TimeSpan.FromSeconds(3721), w);
            Assert.True(TextUtil.DisplayWidth(line) <= w,
                $"宽度 {w}：进度行 {TextUtil.DisplayWidth(line)} 列越界 → [{line}]");
        }
    }

    [Fact]
    public void UnknownWidthKeepsEverything()
    {
        // 宽度未知（0）不猜：三段全给，由调用方决定
        var line = AgentClass.FormatToolProgressLine(Frame, "Run build", TimeSpan.FromSeconds(5), 0);
        Assert.Contains("Run build", line);
    }

    [Fact]
    public void TheDelayKeepsShortToolsFromFlickering()
    {
        // 读文件、列目录这类短工具占绝大多数：一闪而过的动画只是闪屏噪音
        Assert.True(AgentClass.ToolProgressDelay >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void TheAnimationIsActuallyStartedAndStoppedAroundExecution()
    {
        // 纯函数测得再全，执行路径没起停它也没用。
        // 起停必须**成对**：只起不停会留下一行永远在转的残影。
        var src = ReadSource("Agent.cs");
        Assert.Contains("StartToolProgress(", src);
        Assert.Contains("StopToolProgress()", src);
        // 顺序路径的 finally 里收：异常路径也不能漏
        var at = src.IndexOf("StartToolProgress(SummarizeCall", StringComparison.Ordinal);
        Assert.True(at > 0, "执行路径里没有启动进度动画");
        var window = src.Substring(at, Math.Min(500, src.Length - at));
        Assert.Contains("finally", window);
        Assert.Contains("StopToolProgress()", window);
    }

    [Fact]
    public void ToolTimeIsNotCountedAsThinkingTime()
    {
        // 用独立的取消源与秒表，不碰 spinner 那套——
        // 那套的语义是「思考时长」，被清空时会累加进 TurnThinkingSeconds。
        // 把工具耗时混进去，回合摘要里的「思考 Xs」就成了假的。
        var src = ReadSource("Agent.cs");
        var at = src.IndexOf("private void StartToolProgress", StringComparison.Ordinal);
        Assert.True(at > 0);
        var body = src.Substring(at, Math.Min(1200, src.Length - at));
        Assert.DoesNotContain("TurnThinkingSeconds", body);
        Assert.DoesNotContain("_spinnerSw", body);
    }

    private static string ReadSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = System.IO.Path.Combine(dir, "src", "CodeAgent", "Agent", file);
                if (System.IO.File.Exists(candidate) && new System.IO.FileInfo(candidate).Length > 1000)
                    return System.IO.File.ReadAllText(candidate);
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException($"找不到 src/CodeAgent/Agent/{file}");
    }
}
