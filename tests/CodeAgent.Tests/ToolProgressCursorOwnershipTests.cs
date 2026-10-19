using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 工具进度动画与渲染器的光标行所有权。
///
/// 动画是一行 <c>\r</c> 原地重写的文本，和 spinner 完全同类，因此必须走**同一条约定**：
/// 先 <c>SetCursorLineBusy(true)</c> 让渲染器停掉逐字预览并擦干净，
/// 收尾时 <c>SetCursorLineBusy(false)</c> 还回去。
///
/// 少了开始那一步：模型最后一个字没带换行时，渲染器仍会往同一行写预览，
/// 两边都用 <c>\r</c> 抢同一行，屏幕上会闪出两段内容的碎片。
/// 少了收尾那一步：之后**所有**逐字预览都被永久挡在门外，模型回复不再逐字长出来。
/// </summary>
public class ToolProgressCursorOwnershipTests
{
    [Fact]
    public void OwnershipIsTakenAndReturned()
    {
        var src = ReadSource("Agent.cs");
        var start = MethodBody(src, "private void StartToolProgress");
        var stop = MethodBody(src, "private void StopToolProgress");
        Assert.Contains("SetCursorLineBusy(true)", start);
        Assert.Contains("SetCursorLineBusy(false)", stop);
    }

    [Fact]
    public void OwnershipIsTakenWhileHoldingTheConsoleLock()
    {
        // 与 spinner 一致：先持锁再宣告归属，避免与渲染器的一帧交错
        var start = MethodBody(ReadSource("Agent.cs"), "private void StartToolProgress");
        var lockAt = start.IndexOf("lock (ConsoleLock)", StringComparison.Ordinal);
        var busyAt = start.IndexOf("SetCursorLineBusy(true)", StringComparison.Ordinal);
        Assert.True(lockAt > 0 && busyAt > lockAt, "必须在 ConsoleLock 内宣告光标行归属");
    }

    [Fact]
    public void ItStartsANewLineBeforeTheFirstFrame()
    {
        // 动画用 \r 回到行首重写，而模型最后一段常常**不带换行**（流式输出的正常形态）。
        // 不起新行的话，第一帧的 \r 会回到模型正文的行首、把它从头覆盖掉--
        // 用户看到的是自己的回答被一个字一个字吃掉。ShowSpinner 一开始就写了这一行，
        // 这边漏了；这是本轮找到的最严重的一条。
        var start = MethodBody(ReadSource("Agent.cs"), "private void StartToolProgress");
        var nlAt = start.IndexOf("Console.WriteLine()", StringComparison.Ordinal);
        var writeAt = start.IndexOf("Console.Write(\"\\r\"", StringComparison.Ordinal);
        Assert.True(nlAt > 0, "动画起帧前必须先换行，否则会覆盖模型正文");
        Assert.True(nlAt < writeAt, "换行必须排在第一次 \\r 写入之前");
    }

    [Fact]
    public void TheNewLineHappensAfterTheDelayNotAtStartup()
    {
        // 放到启动处的话，700ms 内就结束的短工具会平白多出一个空行--
        // 而那正是延迟想避免的噪音。短工具占绝大多数。
        var start = MethodBody(ReadSource("Agent.cs"), "private void StartToolProgress");
        var delayAt = start.IndexOf("ToolProgressDelay", StringComparison.Ordinal);
        var nlAt = start.IndexOf("Console.WriteLine()", StringComparison.Ordinal);
        Assert.True(delayAt > 0 && nlAt > delayAt, "换行必须等延迟之后");
    }

    [Fact]
    public void ShortToolsNeverClaimTheCursorLine()
    {
        // 没过延迟的短工具从没碰过光标行，收尾时就不能交还--
        // SetCursorLineBusy(false) 会把 **spinner 的** 占用标记顺手抹掉。
        var src = ReadSource("Agent.cs");
        var stop = MethodBody(src, "private void StopToolProgress");
        Assert.Contains("_toolProgressStarted", stop);
        Assert.Contains("if (!started)", stop);
    }

    [Fact]
    public void ItStopsBeforeTheResultLineIsPrinted()
    {
        // 结果行在 ExecuteToolCallAsync 内部打印。若等调用方的 finally 才停动画，
        // 动画会继续往结果行**下方**画帧然后被清掉，屏幕上留下「结果行 + 残帧」。
        var src = ReadSource("Agent.cs");
        var exec = MethodBody(src, "private async Task<ProviderMessage> ExecuteToolCallAsync");
        var stopAt = exec.IndexOf("StopToolProgress()", StringComparison.Ordinal);
        Assert.True(stopAt > 0, "工具返回后必须立刻收掉动画");
        // 必须排在打印结果行之前：找状态行的写入点
        var printAt = exec.IndexOf("Console.WriteLine(status)", StringComparison.Ordinal);
        Assert.True(printAt > 0);
        Assert.True(stopAt < printAt, "动画必须在本函数打印结果行之前收掉");
    }

    [Fact]
    public void TheCallSiteStillHasItsFinallyAsABackstop()
    {
        // 取消 / 异常路径不会走到那个「立刻停」：外层 finally 是最后的兜底。
        // 两处都在才安全——只留外层则结果行下方挂残帧，只留内层则异常时动画永不停止。
        var src = ReadSource("Agent.cs");
        var at = src.IndexOf("StartToolProgress(SummarizeCall", StringComparison.Ordinal);
        Assert.True(at > 0);
        var window = src.Substring(at, Math.Min(600, src.Length - at));
        Assert.Contains("finally", window);
        Assert.Contains("StopToolProgress()", window);
    }

    private static string MethodBody(string src, string signature)
    {
        var at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"找不到方法 {signature}");
        // 窗口要够大：ExecuteToolCallAsync 有 200 多行，进场打印、diff 预览、
        // 执行、收尾都在里面。实测 StopToolProgress 落在 +4365 处——
        // 3000 的窗口会让「必须立刻收掉动画」这条断言永远找不到目标而误报。
        return src.Substring(at, Math.Min(9000, src.Length - at));
    }

    private static string ReadSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "Agent", file);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return File.ReadAllText(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/Agent/{file}");
    }
}
