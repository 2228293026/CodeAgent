using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 会话列表的「展示上限」与「编号空间」必须分开。
///
/// <c>RecentSessionLogs</c> 默认只取 10 条——那是给启动时列个大概用的展示上限。
/// 但 <c>ResumableLogs</c> 曾经直接沿用它，于是 10 变成了硬墙：
/// 第 11 条以前的会话既列不出来也编号不到，`/resume 11` 还会报
/// 「可用 1-10」——告诉用户一个他明明有的会话不存在。
///
/// 展示条数归渲染处管，编号空间归这里管，两者不该共用一个数字。
/// </summary>
public class ResumableLogsRangeTests
{
    [Fact]
    public void DefaultTakesEverythingNotJustTen()
    {
        // 默认参数必须是 int.MaxValue：漏写 max 就是漏掉 10 条以外的所有会话。
        var src = ReadProgram();
        var at = src.IndexOf("internal static List<string> ResumableLogs", StringComparison.Ordinal);
        Assert.True(at > 0, "没找到 ResumableLogs");
        var head = src[at..(at + 200)];
        Assert.Contains("int max = int.MaxValue", head);
    }

    [Fact]
    public void MaxIsPassedThroughToTheUnderlyingScan()
    {
        // 光有参数不传下去等于没写：RecentSessionLogs(config) 会自己回到默认的 10
        var src = ReadProgram();
        var at = src.IndexOf("internal static List<string> ResumableLogs", StringComparison.Ordinal);
        var end = src.IndexOf("\n    }", at, StringComparison.Ordinal);
        Assert.True(end > at);
        var body = src[at..end];
        Assert.Contains("RecentSessionLogs(config, max)", body);
    }

    [Fact]
    public void OutOfRangeMessageUsesTheRealTotal()
    {
        // 两处 /resume、/export 的越界提示都用 logs.Count——
        // 现在 logs 是全部，这个数字才等于真实总数
        var src = ReadProgram();
        Assert.Contains("编号超出范围（可用 1-{logs.Count}）", src);
    }

    private static string ReadProgram()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return System.IO.File.ReadAllText(f);
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 Program.cs");
    }
}
