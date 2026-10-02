using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 仓库级守卫：**凡是按上限截断的工具输出，都必须披露被丢掉多少**。
///
/// 这条规则是连着三轮扫出来的：
///   第 291 轮 <c>session_search</c> 的 max_files 少报命中数
///   第 294 轮 @ 文件菜单的 200 条上限不吭声
///   第 295 轮 <c>gitattributes_report</c> / <c>gitignore_pattern_report</c> 两条上限都没提示
///   第 296 轮 工具输出预览的脚注报"共 8 行"——真实值被上游截断层吃掉了
///
/// 前两处是手写的界面代码，一次一个；这两处在 163 个 <c>*_report</c> / 工具文件里，
/// 手工排查不可能每次都覆盖到——所以固化成扫描。
///
/// 第 296 轮那处不在 Tools 目录里（在 <c>Agent/Agent.cs</c>），而且它的"上限"
/// 不是一行 <c>.Take(max…)</c>，是一个循环里的计数器——**扫描规则匹配不到**。
/// 所以单独用一条真跑**渲染结果**的测试来守：源码扫描能覆盖形状，
/// 覆盖不了"两层截断叠加"这种要真跑才知道的问题。
///
/// 判据：文件里只要出现 <c>.Take(max…)</c> 这类按上限截断，
/// 就必须同时出现披露措辞（未显示 / 上限 / 不完整 / 另有 / 已截断）。
/// 没有披露 = 用户以为看到的就是全部。工具输出是**模型**读到的，
/// 模型会照着"共 100 个文件"得出"已全部检查过"的结论。
/// </summary>
public sealed class CappedOutputDisclosureGuardTests
{
    private static readonly string[] DisclosureMarkers =
        ["未显示", "上限", "不完整", "另有", "已截断", "≥"];

    private static IEnumerable<string> ToolSources()
    {
        var dir = ToolsDir();
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs"))
            yield return f;
    }

    [Fact]
    public void EveryCappedToolDisclosesTheCap()
    {
        var offenders = new List<string>();
        var checkedFiles = 0;
        var totalCaps = 0;
        foreach (var file in ToolSources())
        {
            var raw = File.ReadAllText(file).Replace("\r\n", "\n");
            var lines = StripComments(raw).Split('\n');
            var sites = new List<int>();
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(".Take(max", StringComparison.Ordinal))
                    sites.Add(i);
            if (sites.Count == 0)
                continue;
            checkedFiles++;
            totalCaps += sites.Count;
            foreach (var site in Undisclosed(lines, sites))
                offenders.Add($"{Path.GetFileName(file)}:{site + 1}  {lines[site].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "这些上限截断点**没有各自专属**的披露提示（模型会以为看到的是全部，"
            + "而工具输出正是模型读到的）："
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.OrderBy(x => x))
            + Environment.NewLine + "每个截断点加一行「…（另有 N 条未显示）」即可。");
        Assert.True(totalCaps >= 100, $"只扫到 {totalCaps} 个上限点（{checkedFiles} 个文件），规则退化了");
    }

    /// <summary>找出**没有专属披露**的截断点。
    ///
    /// 关键：**每个上限必须各有一条披露，不能共用**。
    /// 只按"文件里有没有披露过"查是不行的——GitExecutableBitReportTool 里有三处
    /// <c>.Take(maxResults)</c>，删掉中间那处的提示，文件里还剩两条 `未显示`，
    /// 文件级检查照样绿。而被删掉的正是"符号链接"那一段，模型会以为符号链接查全了。
    ///
    /// 所以做**一对一匹配**：每个截断点各占一条最近且未被占用的披露，配不上的就是漏报；
    /// 用完即消耗，避免一条提示被两个截断点共用。
    ///
    /// 窗口取 32 行：实测现有 168 个上限点的披露距离最大是 27 行
    /// （ChangelogReportTool 每个文件里的版本段），16 会误报。</summary>
    private const int _disclosureWindow = 32;

    private static IEnumerable<int> Undisclosed(string[] lines, List<int> sites)
    {
        var claimed = new HashSet<int>();
        var result = new List<int>();
        foreach (var site in sites)
        {
            var from = Math.Max(0, site - _disclosureWindow);
            var to = Math.Min(lines.Length - 1, site + _disclosureWindow);
            var best = -1;
            var bestDist = int.MaxValue;
            for (int j = from; j <= to; j++)
            {
                if (j == site || claimed.Contains(j))
                    continue;
                if (!DisclosureMarkers.Any(m => lines[j].Contains(m, StringComparison.Ordinal)))
                    continue;
                var d = Math.Abs(j - site);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = j;
                }
            }
            if (best < 0)
                result.Add(site);
            else
                claimed.Add(best);
        }
        return result;
    }

    [Fact]
    public void TheGuardRecognisesBothCapShapes()
    {
        // 反向保护：判定规则本身要有效，否则上面那条会永远绿
        Assert.True(Capped("foreach (var x in items.Take(maxResults))"));
        Assert.True(Capped(".Take(maxFiles)"));
        Assert.True(Capped("foreach (var r in rules.Take(maxRules))"));
        Assert.False(Capped("// foreach (var x in items.Take(maxResults))"));  // 注释不算
        Assert.False(Capped("var total = items.Count;"));
    }

    [Fact]
    public void TheTwoToolsFixedThisRoundDisclose()
    {
        // 具体点名：本轮修的就是这两个，别只靠"扫到 N 个文件"这种含糊的通过
        foreach (var name in new[] { "GitattributesReportTool.cs", "GitignorePatternReportTool.cs" })
        {
            var raw = File.ReadAllText(Path.Combine(ToolsDir(), name)).Replace("\r\n", "\n");
            Assert.Contains("达到 max_files", raw);
            Assert.Contains("未显示", raw);
            Assert.Contains("≥", raw);
        }
    }

    [Fact]
    public void CappedFileCountNeverReportsAFakeExactNumber()
    {
        // 「枚举到第 N+1 个就停」时，真实总数**未知**。写 `files.Count + 1` 是在编数字：
        // 150 个文件的仓库会报 101，模型据此认为只剩 1 个没查。必须是「≥」。
        foreach (var name in new[] { "GitattributesReportTool.cs", "GitignorePatternReportTool.cs" })
        {
            var raw = File.ReadAllText(Path.Combine(ToolsDir(), name)).Replace("\r\n", "\n");
            Assert.DoesNotContain(": {allFileCount", raw);
            Assert.DoesNotContain("allFileCount", raw);
            Assert.Contains("≥{files.Count + 1:N0}", raw);
        }
    }

    /// <summary>是否按上限截断。注释里的 <c>Take</c> 不算——
    /// 否则一条注释就能让整个守卫安静地失效。</summary>
    private static bool Capped(string code) =>
        StripComments(code).Contains(".Take(max", StringComparison.Ordinal);

    private static string StripComments(string code) =>
        string.Join('\n', code.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string ToolsDir()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(d, "src", "CodeAgent", "Tools", "ToolRegistry.cs");
                if (File.Exists(f))
                    return Path.Combine(d, "src", "CodeAgent", "Tools");
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new DirectoryNotFoundException("找不到 src/CodeAgent/Tools");
    }
}
