using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 自动压缩**没能降低占用**时的兜底提示。
///
/// 触发自动压缩有三条不成功的出路：网络失败（抛异常）、用户 ESC（返回取消哨兵）、
/// 历史太短（返回 SHORT）。三者共同点：上下文占用**一点没变**。
///
/// 此前这三条路走完之后没有任何提示——因为 90% 警告写在 `else if` 里，
/// 触发过自动压缩就再也不会执行。用户卡在 95%、自动压缩又因网络失败，
/// 屏幕上却一句警告都没有，他会一路撞到硬上限才发现。
/// </summary>
public class AutoCompactFallbackNoticeTests
{
    [Fact]
    public void FailedCompactionStillWarnsAtNinetyPercent()
    {
        var src = ReadProgram();
        var at = src.IndexOf("config.AutoCompactPercent > 0 && pct >= config.AutoCompactPercent", StringComparison.Ordinal);
        Assert.True(at > 0, "没找到自动压缩触发点");
        // 取触发点到 else-if 之间那段：兜底提示必须**在里面**，
        // 否则它又跑回 else 分支、失败路径依旧沉默。
        var end = src.IndexOf("else if (pct >= 90)", at, StringComparison.Ordinal);
        Assert.True(end > at);
        var block = src[at..end];
        Assert.Contains("compacted", block);
        Assert.Contains("!compacted && pct >= 90", block);
    }

    [Fact]
    public void SuccessPathIsMarkedExplicitly()
    {
        // 必须有一个显式的「成功了」标记，而不是靠 r != "COMPACTED" 反推：
        // 取消哨兵和 SHORT 都不等于 COMPACTED，反推容易漏。
        var src = ReadProgram();
        Assert.Contains("compacted = true;", src);
    }

    [Fact]
    public void TheNoticeTellsTheUserWhatToDo()
    {
        // 只说「没能降低占用」等于把问题甩回给用户；得给出下一步（/compact）
        var src = ReadProgram();
        Assert.Contains("自动压缩未能降低占用，建议手动 /compact", src);
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
