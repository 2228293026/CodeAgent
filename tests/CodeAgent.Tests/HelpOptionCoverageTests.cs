using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// `--help` 的内容一致性。
///
/// 帮助表与解析器脱节，比「少写一条说明」严重得多：
/// 它让「有这个选项吗」这个问题在界面上**无解**——用户只能去翻源码。
/// 修之前 `--mode` / `--resume` / `--no-session` 三个参数解析器全都认，
/// 表里一个都没有。
///
/// 这类不一致靠人眼每次补是不行的：新增一个参数就会复发。
/// </summary>
public class HelpOptionCoverageTests
{
    /// <summary>从 Program.cs 里抓 `case "--xxx":` 形式的参数名。</summary>
    private static string[] ParsedOptions()
    {
        var src = ReadSource("Program.cs");
        return System.Text.RegularExpressions.Regex
            .Matches(src, "case \"(--[a-z-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void EveryParsedOptionAppearsInTheHelpTable()
    {
        var rows = CodeAgent.Program.HelpOptionRows();
        var documented = rows.Select(r => r.Key).ToArray();
        // --help 自身不必列在表里（这是元选项），但 --resume / --mode / --no-session 都该有
        var missing = ParsedOptions()
            .Where(o => o != "--help")
            .Where(o => !documented.Any(d => d.Contains(o, StringComparison.Ordinal)))
            .ToArray();
        Assert.True(missing.Length == 0,
            "这些参数解析器认、帮助表里却没有：" + string.Join(", ", missing));
    }

    [Fact]
    public void TheDuplicateUsageSectionsAreGone()
    {
        // `--help` 曾经把「用法」「参数」各打两遍：PrintHelp 自己打一次，
        // 它调用的 PrintReplHelp 又打一次。两段内容还不一样（一个用「选项」，
        // 一个用「参数」），读者会以为是两个不同的东西。
        var src = ReadSource("Program.cs");
        var at = src.IndexOf("private static void PrintReplHelp()", StringComparison.Ordinal);
        Assert.True(at > 0);
        // 边界用下一个方法声明，不能用「说明列至少要」那句文档注释：
        // 它的位置在 PrintReplOnlyHelp **之后**，会把新加的包装方法一起圈进来，
        // 而那个方法本来就该提到 HelpUsageRows/HelpOptionRows。
        var next = src.IndexOf("private static void PrintReplOnlyHelp()", at, StringComparison.Ordinal);
        Assert.True(next > at);
        var body = src[at..next];
        Assert.DoesNotContain("HelpUsageRows", body);
        Assert.DoesNotContain("HelpOptionRows", body);
    }

    [Fact]
    public void ReplHelpStillShowsCommandsAndShortcuts()
    {
        // 拆开之后 /help 不能把命令列表和快捷键也丢了
        var src = ReadSource("Program.cs");
        var at = src.IndexOf("private static void PrintReplHelp()", StringComparison.Ordinal);
        var next = src.IndexOf("private static void PrintReplOnlyHelp()", at, StringComparison.Ordinal);
        var body = src[at..next];
        Assert.Contains("ReplCommands", body);
        Assert.Contains("HelpShortcutRows", body);
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
