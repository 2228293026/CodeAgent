using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 控制台输出编码的设置。
/// 回归点：此前 Main 的**第一行**是裸的 `Console.OutputEncoding = UTF8;`。
/// 设置编码在若干环境下会抛（输出被重定向到已关闭的句柄、精简控制台宿主、
/// 部分 CI 的管道）——而它发生在第一行，抛出去就是**启动即崩**：
/// 屏幕上什么都看不到，也没有任何提示。
/// 项目里其余的平台接触都有兜底（颜色 SafeColor、窗口标题 try/catch、
/// 读终端宽度沿用上一次好值），唯独这里没有。
/// </summary>
public class OutputEncodingSetupTests
{
    [Fact]
    public void ApplyingTheEncodingNeverThrows()
    {
        // 反复调用都必须安静返回：这是启动路径，不允许有失败模式
        for (var i = 0; i < 3; i++)
            Program.ApplyOutputEncoding();
    }

    [Fact]
    public void UiGlyphsAreEncodableAfterSetup()
    {
        Program.ApplyOutputEncoding();
        // 界面用到的这些字符在默认 UTF-8 编码下都能表示
        foreach (var glyph in new[] { "─", "✓", "⚠", "⏵", "…", "↻", "🔧", "⏱", "▌", "…" })
            Assert.Equal(glyph, Console.OutputEncoding.GetString(Console.OutputEncoding.GetBytes(glyph)));
    }

    [Fact]
    public void MainUsesTheGuardedHelper()
    {
        // 守卫：Main 里不得再出现裸的编码赋值，否则兜底被绕过。
        // 只检查 Main 的函数体——ApplyOutputEncoding 内部**必然**有一处赋值，
        // 那正是被 try 包住的那处。
        var body = MethodBody("private static async Task<int> Main");
        Assert.Contains("ApplyOutputEncoding();", body);
        Assert.False(
            Regex.IsMatch(body, @"Console\.(Output|Input)Encoding\s*="),
            "Main 里仍有裸的编码赋值，兜底会被绕过");
    }

    [Fact]
    public void TheHelperSwallowsFailures()
    {
        var body = MethodBody("internal static void ApplyOutputEncoding");
        Assert.Contains("try", body);
        Assert.Contains("catch", body);
    }

    /// <summary>取出 Program.cs 里某个成员声明到下一个同缩进成员声明之间的文本。
    /// 注意参数是**成员签名**，不是源码：之前误把文件路径当源码传进来，
    /// 于是在 46 个字符的路径里找签名，失败信息还看不出错在哪。</summary>
    private static string MethodBody(string signature)
    {
        var source = File.ReadAllText(FindProgram());
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"源码里找不到 {signature}（Program.cs 共 {source.Length} 字符）");
        var lineEnd = source.IndexOf('\n', at);
        var indent = source[at..lineEnd].Length - source[at..lineEnd].TrimStart().Length;
        var rest = source[lineEnd..];
        for (var i = 1; i < rest.Length; i++)
        {
            if (rest[i] != '\n')
                continue;
            var next = rest[(i + 1)..].Split('\n')[0];
            if (next.Length - next.TrimStart().Length <= indent && next.Trim().Length > 0)
                return rest[..i];
        }
        return rest;
    }

    /// <summary>定位真实的 <c>src/CodeAgent/Program.cs</c>。
    /// 光靠「路径存在」不够：向上走几层可能撞上别处的小 Program.cs
    /// （本项目就有测试会往临时目录写 Program.cs），拿到几十字节的文件、
    /// 断言失败还看不出错在哪。所以这里逐个候选**校验内容**并把走过的路径报出来。</summary>
    private static string FindProgram()
    {
        var tried = new System.Collections.Generic.List<string>();
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(candidate))
                {
                    tried.Add(candidate);
                    var text = File.ReadAllText(candidate);
                    // 真源码必然有命名空间声明；几十字节的同名文件不是它
                    if (text.Length > 1000 && text.Contains("namespace CodeAgent", StringComparison.Ordinal))
                        return candidate;
                }
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到真实的 src/CodeAgent/Program.cs。走过的路径: " + string.Join(" | ", tried));
    }
}
