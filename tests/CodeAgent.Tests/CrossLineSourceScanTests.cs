using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 跨行源码断言的换行守卫。
///
/// <para>
/// 结构守卫（"这段源码里不许出现 X"）要靠 <c>IndexOf("\n 某行")</c> 之类的模式定位。
/// 仓库里存的是 LF，但 Windows 上 <c>core.autocrlf=true</c> 的工作区检出的�� <b>CRLF</b>：
/// 模式永远匹配不上 → <c>IndexOf</c> 返回 -1 → 要么断言莫名其妙地失败，
/// 要么 <c>body[..-1]</c> 直接抛异常。
/// </para>
///
/// <para>
/// 实际踩中过一次：某轮本地（LF）4798 项全绿，推上去 Windows CI 红了，
/// 失败信息是"某功能仍嵌在另一个 if 里"——完全指错了方向。
/// 那条测试当时只是<em>碰巧</em>在本机活着，因为本机是 LF。
/// </para>
///
/// <para>
/// 本守卫把"读生产源码 + 跨行匹配"这个组合列出来，要求同文件里做换行归一化。
/// 它是<em>形状</em>检查而非完备判定：写成 <c>var s = ...; File.ReadAllText(s)</c> 会扫不到。
/// 但这类测试本来就少，逐个盯住比指望扫描更现实。
/// </para>
/// </summary>
public sealed class CrossLineSourceScanTests
{
    /// <summary>读生产源码的典型写法（Find*/Source* 助手、显式 src 路径）。</summary>
    private static readonly Regex ReadsSource = new(
        @"ReadAllText\(\s*(Find|Source|FindProgram|FindSource|Path\.Combine\([^)]*src)",
        RegexOptions.Compiled);

    /// <summary>跨行匹配：按 \n 切分或按 \n 定位。
    ///
    /// 注意是**四个反斜杠的 <c>\\n</c>**：正则里的 <c>\n</c> 匹配的是换行符本身，
    /// 而源码里写的是 <c>'\n'</c> 这样的**字面反斜杠加 n**。写成 <c>\n</c> 的话规则恒不命中——
    /// 守卫会安静地什么都扫不到（下面 TheGuardItself 正是防这个的）。
    /// </summary>
    private static readonly Regex CrossLine = new(
        @"IndexOf\('\\n'|IndexOf\(""\\n|Split\('\\n'\)|Split\(""\\n|ReadAllLines",
        RegexOptions.Compiled);

    /// <summary>归一化调用的字面文本。注意反斜杠是**真实字符**：用非逐字字符串时`\r` 会变成回车，
    /// 拿去和逐字字符串里的源码文本比就永远不相等。</summary>
    private const string NormalizeNeedle = @"Replace(\""\r\n"", ""\n"")";

    private static string TestDir()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var d = Path.Combine(dir, "tests", "CodeAgent.Tests");
                if (Directory.Exists(d) && new FileInfo(Path.Combine(d, "HelpListRenderTests.cs")).Length > 1000)
                    return d;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new DirectoryNotFoundException("找不到 tests/CodeAgent.Tests");
    }

    [Fact]
    public void EveryCrossLineSourceScan_NormalizesLineEndings()
    {
        var offenders = new System.Collections.Generic.List<string>();
        foreach (var file in Directory.EnumerateFiles(TestDir(), "*.cs"))
        {
            var raw = File.ReadAllText(file);
            if (!ReadsSource.IsMatch(raw) || !CrossLine.IsMatch(raw))
                continue;
            if (raw.Contains("Replace(\"\\r\\n\", \"\\n\")", StringComparison.Ordinal))
                continue;
            offenders.Add(Path.GetFileName(file));
        }
        Assert.True(offenders.Count == 0,
            "这些测试读生产源码并跨行匹配，却没有归一化换行（Windows CRLF 工作区上会误判或抛异常）："
            + string.Join(" | ", offenders.OrderBy(x => x)));
    }

    [Fact]
    public void TheGuardItself_FindsSomethingToCheck()
    {
        // 反向保护：匹配规则失效时会安静地扫到 0 个文件、永远通过。
        // 那比没有守卫更危险——它占着"这里有人管"的位置。
        var candidates = 0;
        foreach (var file in Directory.EnumerateFiles(TestDir(), "*.cs"))
        {
            var raw = File.ReadAllText(file);
            if (ReadsSource.IsMatch(raw) && CrossLine.IsMatch(raw))
                candidates++;
        }
        Assert.True(candidates > 0, "一个候选都没扫到：ReadsSource / CrossLine 规则已跟不上代码");
    }

    [Fact]
    public void TheRule_ActuallyFlagsAnUnnormalizedSnippet()
    {
        // 证明规则不是恒假：把一段真实的坏例子喂进去，它必须命中
        const string bad = """
            var source = File.ReadAllText(FindProgram());
            var end = source.IndexOf('\n', 0);
            """;
        Assert.True(ReadsSource.IsMatch(bad) && CrossLine.IsMatch(bad));
        Assert.False(bad.Contains("Replace(\"\\r\\n\", \"\\n\")", StringComparison.Ordinal));
    }

    [Fact]
    public void ANormalizedSnippetPasses()
    {
        const string good = """
            var source = File.ReadAllText(FindProgram()).Replace("\r\n", "\n");
            var end = source.IndexOf('\n', 0);
            """;
        Assert.True(ReadsSource.IsMatch(good) && CrossLine.IsMatch(good));
        Assert.True(good.Contains("Replace(\"\\r\\n\", \"\\n\")", StringComparison.Ordinal));
    }
}
