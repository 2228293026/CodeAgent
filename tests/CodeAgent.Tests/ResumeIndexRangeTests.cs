using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>--resume &lt;编号&gt;</c> 越界时的提示。
///
/// 取用逻辑本身是对的：只取前 N 条，再取第 N 条。
/// 但越界提示里的「可用 1-{logs.Count}」是从**截断后**的列表算的——
/// 传进去的 max 就是用户给的编号，所以 logs.Count 至多等于那个编号。
///
/// 于是 `--resume 99` 在只有 3 个日志的机器上会提示「可用 1-99」：
/// 用户照着再敲一次 99，再来一遍，永远出不来。
/// 提示里的上限必须是**真实总数**。
/// </summary>
public class ResumeIndexRangeTests
{
    [Fact]
    public void OutOfRangeMessageMustNotDeriveLimitFromTheTruncatedList()
    {
        var src = ReadProgram();
        var at = src.IndexOf("--resume 编号超出范围", StringComparison.Ordinal);
        Assert.True(at > 0, "没找到越界提示");
        // 取提示行前后 300 字符作为上下文
        var from = Math.Max(0, at - 400);
        var block = src[from..Math.Min(src.Length, at + 200)];
        // 提示里的数字不能直接来自 logs.Count（被 max 截断过）
        Assert.DoesNotContain("可用 1-{logs.Count}", block);
    }

    [Fact]
    public void ToChoosesTheEntryByIndex()
    {
        // 取用逻辑：logs[resumeIndex - 1]。这条是保证「修提示」不会顺手把取用改坏。
        var src = ReadProgram();
        Assert.Contains("target = logs[resumeIndex - 1];", src);
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
