using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>COLUMNS</c> 回退 + 重定向下的输入框收尾。
///
/// 这两条是配套的：没有 <c>COLUMNS</c> 就没有办法在重定向下指定宽度，
/// 而重定向下输入框下边框又会**粘在提示符后面**，把框撑成双倍宽。
/// </summary>
public sealed class ColumnsEnvAndInputFrameTests
{
    [Theory]
    [InlineData("80", 80)]
    [InlineData("60", 60)]
    [InlineData(" 120 ", 120)]
    [InlineData("300", 300)]
    public void ValidWidthIsHonored(string raw, int expected)
    {
        Assert.Equal(expected, Program.ColumnsFromEnv(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12.5")]
    public void UnparseableMeansUnknown(string? raw)
    {
        // 不猜：宁可"宽度未知、不截断"，也不要一个随手设错的值把每行压扁
        Assert.Equal(0, Program.ColumnsFromEnv(raw));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("7")]
    public void ImplausiblyNarrowMeansUnknown(string raw)
    {
        // 与 ResolveColumns 同一口径：终端最小化时会抖到 1~2 列
        Assert.Equal(0, Program.ColumnsFromEnv(raw));
    }

    [Fact]
    public void MinimumPlausibleWidthIsAccepted()
    {
        Assert.Equal(Program.MinPlausibleColumns, Program.ColumnsFromEnv(Program.MinPlausibleColumns.ToString()));
    }

    [Fact]
    public void AbsurdWidthIsClampedNotTrusted()
    {
        Assert.Equal(300, Program.ColumnsFromEnv("100000"));
    }

    [Fact]
    public void TheFallbackIsWiredIntoBothWidthReaders()
    {
        // Program 与 Agent 各有一份 ConsoleColumns；只改一处会让 spinner 帧超宽
        var program = File.ReadAllText(FindSource("src", "CodeAgent", "Program.cs"));
        var agent = File.ReadAllText(FindSource("src", "CodeAgent", "Agent", "Agent.cs"));
        Assert.Contains("if (Console.IsOutputRedirected)\n            return ColumnsFromEnv();", program.Replace("\r\n", "\n"));
        Assert.Contains("if (Console.IsOutputRedirected)\n            // 与 Program.ConsoleColumns 同一回退", agent.Replace("\r\n", "\n"));
        Assert.Contains("return Program.ColumnsFromEnv();", agent);
    }

    [Fact]
    public void RedirectedInputTerminatesTheLine()
    {
        // 回归：重定向输入路径此前**不**收尾换行，调用方紧接着写的下边框会粘在提示符后面
        var src = File.ReadAllText(FindSource("src", "CodeAgent", "InputLine.cs"));
        var at = src.IndexOf("if (Console.IsInputRedirected)", StringComparison.Ordinal);
        Assert.True(at > 0, "应存在重定向输入分支");
        var window = src.Substring(at, Math.Min(900, src.Length - at));
        Assert.Contains("Console.WriteLine();", window);
        // 换行必须在 return 之前
        Assert.True(window.IndexOf("Console.WriteLine();", StringComparison.Ordinal)
                    < window.IndexOf("return line;", StringComparison.Ordinal));
    }

    [Fact]
    public void EofStillReturnsWithoutTheFrameClosing()
    {
        // EOF 时返回 null，调用方**不会**画下边框；此时提示符悬空是可接受的，
        // 不能顺手补一行换行把后续输出推低一行
        var src = File.ReadAllText(FindSource("src", "CodeAgent", "InputLine.cs"));
        var at = src.IndexOf("if (Console.IsInputRedirected)", StringComparison.Ordinal);
        var window = src.Substring(at, Math.Min(900, src.Length - at));
        var eof = window.IndexOf("if (line is null)", StringComparison.Ordinal);
        var nl = window.IndexOf("Console.WriteLine();", StringComparison.Ordinal);
        Assert.True(eof > 0 && nl > eof, "换行应位于 EOF 判断之后");
    }

    [Fact]
    public void FrameBottomIsExactlyTheRequestedWidth()
    {
        Assert.Equal(60, Program.BuildInputFrameBottom(60).Length);
        Assert.Equal(1, Program.BuildInputFrameBottom(0).Length == 0 ? 1 : 0);
    }

    private static string FindSource(params string[] parts)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(new[] { dir }.Concat(parts).ToArray());
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 " + Path.Combine(parts));
    }
}
