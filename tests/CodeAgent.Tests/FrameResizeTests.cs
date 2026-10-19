using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入框几何随窗口尺寸刷新。
///
/// <c>winW</c> / <c>fitBudget</c> / <c>frameTarget</c> 此前只在进入输入行时算一次，
/// 之后再也不变。而用户在输入过程中拖窗口、最大化、切分屏是**常态**：
/// · 窗口变宽 → 右边框停在旧列，右侧缺一块
/// · 窗口变窄 → 补出来的空格把整行顶到折行（而折行正是「敲一个字多出一行」的根因）
///
/// 光标定位是从行尾往左数算的，一并跟着错位——表现为「光标不在输入的字上」。
/// </summary>
public class FrameResizeTests
{
    [Fact]
    public void GeometryFollowsTheWindowWidth()
    {
        var wide = InputLine.ComputeFrameGeometry(120);
        Assert.True(wide.FrameRight);
        Assert.Equal(119, wide.FrameTarget);   // 留 1 列：部分终端写满最后一列会提前换行
        Assert.Equal(InputLine.FitBudget(120), wide.FitBudget);

        var narrow = InputLine.ComputeFrameGeometry(40);
        Assert.True(narrow.FrameRight);
        Assert.Equal(39, narrow.FrameTarget);
        Assert.Equal(InputLine.FitBudget(40), narrow.FitBudget);
    }

    [Fact]
    public void GeometryShrinksWhenTheWindowShrinks()
    {
        // 变窄是最容易出事的方向：边框目标列若还停在旧值，
        // 补出来的空格会超过新宽度，必然折行。
        var before = InputLine.ComputeFrameGeometry(120).FrameTarget;
        var after = InputLine.ComputeFrameGeometry(80).FrameTarget;
        Assert.True(after < before, $"变窄后目标列没有跟着变小：{before} → {after}");
    }

    [Fact]
    public void UnknownWidthNeverClosesTheFrame()
    {
        // 宽度未知时宁可少一条边，也不拿猜出来的宽度去补框
        foreach (var w in new[] { 0, -1, -80 })
        {
            var g = InputLine.ComputeFrameGeometry(w);
            Assert.False(g.FrameRight, $"宽度 {w} 不该闭合右边框");
            Assert.Equal(0, g.FitBudget);
        }
    }

    [Fact]
    public void FitBudgetNeverDropsBelowItsFloor()
    {
        // 极窄终端下预算有下限：跌破 10 列后提示符与输入内容会互相挤压
        Assert.Equal(10, InputLine.ComputeFrameGeometry(4).FitBudget);
        Assert.Equal(10, InputLine.ComputeFrameGeometry(12).FitBudget);
    }

    [Fact]
    public void TheFrameIsMeasuredOneColumnInsideTheWindow()
    {
        // 竖线必须落在最后一列**之前**：写满最后一列会触发终端自动换行，
        // 于是框看起来「胖了一行」，而行数错位会一路累积。
        for (var w = 1; w <= 200; w++)
            Assert.Equal(w - 1, InputLine.ComputeFrameGeometry(w).FrameTarget);
    }

    [Fact]
    public void TheRedrawPathActuallyRefreshesTheWidth()
    {
        // 纯函数测得再全，重绘时没调用也是白搭。
        // 必须确认 ScrollInput 在动手画面**之前**调了 RefreshWidth。
        var src = ReadSource("InputLine.cs");
        var scrollAt = src.IndexOf("void ScrollInput()", StringComparison.Ordinal);
        Assert.True(scrollAt > 0, "找不到 ScrollInput");
        // 窗口放宽到 2000：ScrollInput 里 RefreshWidth 之后隔着折叠判断、CloseFrame
        // 等好几行才是 BuildRedrawAnsi，400 的窗口里它落在范围外，
        // IndexOf 返回 -1，断言会以「顺序反了」的名义误报。
        var body = src.Substring(scrollAt, Math.Min(2000, src.Length - scrollAt));
        Assert.Contains("RefreshWidth()", body);
        var refreshAt = body.IndexOf("RefreshWidth()", StringComparison.Ordinal);
        var drawAt = body.IndexOf("BuildRedrawAnsi", StringComparison.Ordinal);
        Assert.True(drawAt > 0, "ScrollInput 里找不到 BuildRedrawAnsi");
        Assert.True(refreshAt < drawAt, "RefreshWidth 必须在画之前调用，否则这一次重绘仍用旧宽度");
    }

    [Fact]
    public void RefreshRebuildsEveryDerivedValueNotJustTheWidth()
    {
        // 只更新 winW 是不够的：frameTarget 与 fitBudget 是分开存的，
        // 漏掉任何一个都会出现「宽度对了、边框还错着」这种半新半旧的状态。
        var src = ReadSource("InputLine.cs");
        var at = src.IndexOf("void RefreshWidth()", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到 RefreshWidth");
        var body = src.Substring(at, Math.Min(600, src.Length - at));
        Assert.Contains("frameTarget = geo.FrameTarget", body);
        Assert.Contains("fitBudget = geo.FitBudget", body);
        Assert.Contains("frameRight = geo.FrameRight", body);
    }

    private static string ReadSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", file);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return File.ReadAllText(candidate);
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/{file}");
    }
}
