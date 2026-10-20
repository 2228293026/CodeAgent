using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /resume 列表的**显示**条数与**编号空间**是两件事。
///
/// 上一轮把编号空间从 10 放开到全部（否则第 11 条以前的会话编号不到、
/// 还会被误告「不存在」）。但随即出现另一半问题：
/// 循环直接 `i &lt; logs.Count`，一年下来几百条会话会一次性刷满屏幕，
/// 把命令历史整屏推走——用户还得往上翻着找自己刚才在看什么。
///
/// 所以：列表只列最近 10 条，**但编号仍覆盖全部**。
/// 关键是那句提示——不说清楚「只列了前 10 条」，用户会以为第 11 条不存在，
/// 于是又回到上一轮刚修掉的那个坑。
/// </summary>
public class ResumeListTruncationTests
{
    [Fact]
    public void ListLoopIsBoundedByADisplayLimit()
    {
        var src = ReadProgram();
        // 锚点要用 **case 标签**，不能用 `var logs = ResumableLogs`：
        // 那一行在别的方法里也出现过，IndexOf 会命中第一个、落在几千行之外，
        // 测试于是断言了一段无关代码里没有的东西（第一版就是这么挂的）。
        var at = src.IndexOf("case \"/resume\":", StringComparison.Ordinal);
        Assert.True(at > 0, "没找到 /resume 分支");
        var nearby = src[at..Math.Min(src.Length, at + 4000)];
        Assert.Contains("ResumeListLimit", nearby);
        Assert.Contains("Math.Min(logs.Count, ResumeListLimit)", nearby);
    }

    [Fact]
    public void UnlistedSessionsAreAnnouncedWithTheFullRange()
    {
        // 提示必须同时给出「还有几个」和「编号仍然有效」——
        // 只说前者等于告诉用户那几条够不着
        var src = ReadProgram();
        Assert.Contains("个更早的会话未列出（仍可用 /resume <编号> 恢复，编号 1-", src);
    }

    [Fact]
    public void ExtractionStillUsesTheFullList()
    {
        // 截断只能发生在渲染，不能发生在取用：
        // logs[ridx - 1] 必须照旧按全部列表索引，否则第 11 条恢复不了
        var src = ReadProgram();
        Assert.Contains("agent.LoadSessionLog(logs[ridx - 1])", src);
        Assert.Contains("ridx <= logs.Count", src);
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
