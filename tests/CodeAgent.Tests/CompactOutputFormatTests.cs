using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /compact 三条结局的输出格式。
///
/// 取消走 <c>FormatCancelLine</c>，但 SHORT 与成功两条用的是裸字符串拼接：
/// 窄终端下不折行、标记与正文间距也不统一——紧挨着「已取消压缩」那一行摆在一起时
/// 明显不是一套。用户看不出这三行是同一条命令的不同结局。
///
/// 顺带说明为什么不索性全改成同一个函数：三条结局的**语义**不同
/// （成功/无事可做/被取消），标记和颜色本来就该不同，只是排版规则要一致。
/// </summary>
public class CompactOutputFormatTests
{
    [Fact]
    public void AllThreeOutcomesUseFormattingHelpers()
    {
        var src = ReadProgram();
        var at = src.IndexOf("case \"/compact\":", StringComparison.Ordinal);
        Assert.True(at > 0, "没找到 /compact 分支");
        var next = src.IndexOf("case \"/cls\":", at, StringComparison.Ordinal);
        Assert.True(next > at, "没找到 /compact 的结束边界");
        var block = src[at..next];
        Assert.Contains("FormatCancelLine(", block);
        Assert.Contains("FormatNoticeLine(", block);
        Assert.Contains("FormatConfirmLine(", block);
    }

    [Fact]
    public void ShortOutcomeDoesNotBareInterpolateTheMarker()
    {
        // 裸拼接的特征是 `$"{Warn} 当前对话过短`：标记直接进字符串，不经格式函数。
        // 这条测试就是钉住它不再回来。
        var src = ReadProgram();
        Assert.DoesNotContain("$\"{SafeColor.Glyphs.Warn} 当前对话过短", src);
        Assert.DoesNotContain("$\"{SafeColor.Glyphs.Ok} 历史已压缩", src);
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
