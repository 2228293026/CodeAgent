using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入行的终端宽度读数。
/// 回归点：<c>TryWindowWidth()</c> 此前直接返回 <c>Math.Clamp(Console.WindowWidth, 0, 300)</c>，
/// **没有任何可信度校验、没有上一次好值**。终端最小化、拖拽过程中的抖动会让它
/// 短暂返回 1~2 列，而输入行会据此决定是否走 ANSI 原地重绘、按多宽折行 ——
/// 拿到 1~2 列会让整行渲染全面退化。
/// Program.ConsoleColumns 与 ConsoleRenderer.TableBudget 都已经修过这个问题，
/// 输入行是最后一处漏网的。
/// </summary>
public class InputLineWidthStabilityTests
{
    [Fact]
    public void ImplausibleReadingIsRejected()
    {
        Assert.Equal(80, InputLine.ResolveWindowWidth(1, 80));
        Assert.Equal(80, InputLine.ResolveWindowWidth(2, 80));
        Assert.Equal(80, InputLine.ResolveWindowWidth(7, 80));
    }

    [Fact]
    public void PlausibleReadingIsUsedAsIs()
    {
        Assert.Equal(120, InputLine.ResolveWindowWidth(120, 80));
        Assert.Equal(InputLine.MinPlausibleWidth, InputLine.ResolveWindowWidth(InputLine.MinPlausibleWidth, 80));
    }

    [Fact]
    public void UnknownStaysUnknown()
    {
        // 没有好值可沿用时返回 0 = 未知（下游约定：0 表示不裁剪）
        Assert.Equal(0, InputLine.ResolveWindowWidth(0, 0));
        Assert.Equal(0, InputLine.ResolveWindowWidth(1, 0));
    }

    [Fact]
    public void OutliersAreStillClamped()
    {
        // 上限照旧：窗口宽度被伪造/超宽时不无限放大预算
        Assert.Equal(300, InputLine.ResolveWindowWidth(100000, 80));
    }

    [Fact]
    public void ThresholdIsSane()
    {
        Assert.InRange(InputLine.MinPlausibleWidth, 4, 20);
    }

    [Fact]
    public void MatchesTheSharedRuleInProgram()
    {
        // 同一个项目里「可信宽度」只能有一套标准：两处阈值必须一致，
        // 否则会出现「状态栏敢信、输入行不敢信」的分裂行为。
        // InputLine.MinPlausibleWidth 现在是 Program.MinPlausibleColumns 的**别名**，
        // 这条断言钉住的是「别名关系不许被改回各自的字面量」。
        Assert.Equal(Program.MinPlausibleColumns, InputLine.MinPlausibleWidth);
    }

    [Fact]
    public void ReaderDoesNotReturnARawMeasurement()
    {
        // 源码级守卫：输入行不得再直接 return Clamp(Console.WindowWidth…)
        var source = File.ReadAllText(FindInputLine());
        var at = source.IndexOf("private static int TryWindowWidth()", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到 TryWindowWidth");
        var body = source[at..];
        body = body[..body.IndexOf("/// <summary>", at + 1, StringComparison.Ordinal)];
        Assert.Contains("ResolveWindowWidth", body);
        Assert.False(body.Contains("return Math.Clamp(Console.WindowWidth"), body);
    }

    [Fact]
    public void AnsiInPlaceDecisionIsUnchanged()
    {
        // 宽度可信度修复不应改变 ANSI 原地重绘的既有判定
        Assert.True(InputLine.ShouldUseAnsiInPlace(true, false, 80));
        Assert.False(InputLine.ShouldUseAnsiInPlace(true, false, InputLine.AnsiMinWidth - 1));
        Assert.False(InputLine.ShouldUseAnsiInPlace(true, true, 80));
    }

    private static string FindInputLine()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "InputLine.cs");
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return candidate;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/InputLine.cs");
    }
}
