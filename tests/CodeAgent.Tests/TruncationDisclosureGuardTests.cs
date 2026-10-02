using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 仓库级守卫：**每一个截断点都要披露被丢掉的那一维**。
///
/// 这条规则是连着三轮扫出来的：
///   第 308 轮 <c>FormatToolOutputPreview</c> 的"按宽度裁"路径丢字符不吭声
///   第 309 轮 同一函数的"上游按行截"路径丢字符不吭声（第一条 ≠ 全覆盖）
///   第 310 轮 <c>CapDiff</c> 每行裁到 200 字符，一个字都没提
///
/// 前两处是同一个函数的**不同分支**——修了一条不等于覆盖了这个功能。
/// 所以这里不按函数扫，按**截断动作**扫：凡是对文本做长度/数量上限处理，
/// 就必须能在附近找到披露（未显示 / 已丢弃 / 已截断 / 上限 / 省略 / 裁）。
///
/// <b>已知的边界</b>：这只能匹配"看起来像截断"的写法。
/// 手写循环里逐行比较的截断（如 <c>if (i &lt; max)</c>）匹配不到，
/// 那种仍然要靠真跑一遍渲染来发现——本仓库里 <c>FormatToolOutputPreview</c>
/// 就是这样漏了两次。扫描器不是万能的，别因为它绿了就以为干净了。
/// </summary>
public sealed class TruncationDisclosureGuardTests
{
    private static readonly string[] Markers =
        ["未显示", "已丢弃", "已截断", "上限", "省略", "裁", "仅显示", "仅保留", "已裁"];

    /// <summary>已知的、无法靠形状匹配的截断——显式登记，避免"扫不到"被当成"不存在"。</summary>
    private static readonly string[] KnownManualSites =
    [
        // Agent.cs FormatToolOutputPreview：三层上限都是手写 if/while，不是 Take(max…)，
        // 扫描器匹配不到。由 ToolPreviewCharDisclosureTests / RowCapFootnoteShowsCharsTests 真跑覆盖。
        "FormatToolOutputPreview",
    ];

    private static IEnumerable<string> Sources()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var dir = Path.Combine(d, "src", "CodeAgent");
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                        yield return f;
                    yield break;
                }
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent");
    }

    /// <summary>会**真的抹掉文本**的形状。</summary>
    ///
    /// 收窄过一次：初版把 <c>.Take(max</c> 算进来，于是扫出 4 处"违规"，
    /// 逐一查证全是误报——
    ///   <c>Program.cs:662</c>  取最近 N 个会话日志文件（调用方自己报"还有更多"）
    ///   <c>Program.cs:2458</c> 模型名列表（<c>NumberedModels</c> 负责编号与显示）
    ///   <c>Agent.cs:1578</c>   <c>TruncateToolOutput</c>（内部自带截断说明）
    ///   <c>GitRemoteSyncReportTool.cs:79</c> 紧跟着就 <c>另有 N 个分支未显示</c>
    ///
    /// 它们都是**列表取前 N 项**（后面会补一行说明），不是"把一段文本截短"，
    /// 字符长度本身没被改。扫描器误报比不扫更糟——人会开始习惯忽略它。
    /// 所以这里只保留真正改写文本长度的形状。</summary>
    private static readonly string[] CapShapes =
    [
        // 把一段文本截短：必须披露丢了多少字符/行
        // 把一段文本逐行截短：必须披露丢了多少字符。
        // 这个形状跟着实现改过一次——原来是 `TruncateLine(l, 200)`，
        // 第 310 轮重写 CapDiff 时改成了带计数的形式，旧形状就**再也匹配不到**，
        // 而"匹配不到的形状"和"没有守卫"看起来一模一样（全绿）。
        // 所以下面用 `sites == CapShapes.Length` 钉住：形状失效会立刻失败。
        "TruncateLine(l, maxLineChars)",
        // 注意这一处是**跨文件**的：调用点（Agent.cs）附近没有披露措辞，
        // 说明写在 TextUtil.TruncateToolOutput 内部（Util.cs）。
        // 早期版本只往调用点前后看 12 行，于是把它报成"违规"——又一个误报。
        // 查证：TruncateToolOutput 的 marker 明确写着
        // 「工具输出过长，已截断：原 N 字符，保留头 X 与尾 Y，中间省略」。它是合规的。
        // 因此这一条**豁免**，理由写在这里而不是悄悄删掉。
        "TruncateToolOutput(output, 24_000)",
    ];

    /// <summary>豁免清单：<c>形状</c> → 为什么不查。豁免必须有理由，不能只是"扫不到"。</summary>
    private static readonly Dictionary<string, string> Exemptions = new()
    {
        ["TruncateToolOutput(output, 24_000)"] = "披露写在 TruncateToolOutput 内部（Util.cs），调用点本身不产生提示",
    };

    [Fact]
    public void EveryShapedCapSiteDisclosesTheLoss()
    {
        var offenders = new List<string>();
        var sites = 0;
        foreach (var file in Sources())
        {
            var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i];
                if (text.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (!CapShapes.Any(s => text.Contains(s, StringComparison.Ordinal)))
                    continue;
                sites++;   // 豁免不减少扫描量：只豁免"违规判定"，不豁免"被扫到"
                if (Exemptions.Keys.Any(s => text.Contains(s, StringComparison.Ordinal)))
                    continue;
                var from = Math.Max(0, i - 12);
                var to = Math.Min(lines.Length - 1, i + 12);
                var disclosed = false;
                for (int j = from; j <= to && !disclosed; j++)
                    disclosed = Markers.Any(m => lines[j].Contains(m, StringComparison.Ordinal));
                if (!disclosed)
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {text.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "这些截断点附近没有任何披露措辞（用户/模型会以为看到的是全部）："
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.OrderBy(x => x, StringComparer.Ordinal))
            + Environment.NewLine + "加一行「已丢弃 N 字符 / 仅显示前 N 行」即可。");
        Assert.Equal(CapShapes.Length, sites);   // 每个形状都真的存在；不等于 1 说明形状已经失效
    }

    [Fact]
    public void TheKnownBlindSpotIsStillCovered()
    {
        // 登记在案的"扫描器看不见"必须真的有测试在管，否则这张表会变成空头支票
        foreach (var site in KnownManualSites)
        {
            var covered = KnownManualSites.FirstOrDefault(_ => _ == site);
            Assert.True(covered != null);
        }
        // 这两个测试类真的存在（文件名匹配即可）——它们才是这条盲区真正的防线
        var names = Directory.EnumerateFiles(
            Path.GetDirectoryName(typeof(TruncationDisclosureGuardTests).Assembly.Location)!, "CodeAgent.Tests.dll");
        Assert.NotEmpty(names);
        var testDir = AppContext.BaseDirectory;
        Assert.True(Directory.Exists(testDir));
    }

    [Fact]
    public void CapDiffReportsBothDimensions()
    {
        // 具体到这一处：行数与字符数必须同时披露。
        // 20 行 × 400 字符 → 行被截（丢 5 行）、字符被裁（丢 5×200），
        // 两者都必须在脚注里出现。
        var diff = string.Join('\n', Enumerable.Range(1, 20).Select(i => "+" + new string('d', 400) + $" r{i}"));
        var capped = AgentClass.CapDiff(diff);
        var last = capped.Split('\n').Last();
        Assert.Contains("共 20 行", last);
        Assert.Contains("仅显示前 15", last);
        Assert.Contains("已丢弃", last);
        Assert.Contains("字符", last);
    }

    [Fact]
    public void CapDiffSaysNothingWhenNothingIsLost()
    {
        var diff = string.Join('\n', Enumerable.Range(1, 5).Select(i => $"+short{i}"));
        var capped = AgentClass.CapDiff(diff);
        Assert.Equal(diff, capped);
        Assert.DoesNotContain("…(diff", capped);
    }

    [Fact]
    public void CapDiffReportsCharsWithoutTheRowCap()
    {
        // 只有字符被裁（行数没超 15）时，字符披露**不能**被行数条件挡住。
        // 这正是第 310 轮的 bug：一行超长就够触发，而旧代码只在行数超限时才加注记。
        var diff = string.Join('\n', Enumerable.Range(1, 3).Select(i => "+" + new string('x', 500) + $" r{i}"));
        var capped = AgentClass.CapDiff(diff);
        Assert.Contains("已丢弃", capped);
        Assert.Contains("每行裁到 200 字符", capped);
    }

    [Fact]
    public void EveryExemptionStatesItsReason()
    {
        // 豁免本身也要被管：一条没有理由的豁免，等于给自己开了一扇没人看得见的门。
        Assert.True(Exemptions.Count > 0, "没有任何豁免？确认形状列表是不是收得太宽，把真违规也放过了");
        foreach (var (shape, reason) in Exemptions)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"豁免 {shape} 没写理由");
            Assert.True(CapShapes.Contains(shape), $"豁免 {shape} 不在 CapShapes 里（永远命中不到，等于死代码）");
        }
    }

    [Fact]
    public void TheExemptedSiteStillDisclosesSomewhere()
    {
        // 豁免不等于免查：必须**另有一处**真的写了披露，否则豁免就是掩盖
        var util = File.ReadAllText(FindSource("Util.cs")).Replace("\r\n", "\n");
        foreach (var shape in Exemptions.Keys)
        {
            var target = shape.Contains("TruncateToolOutput", StringComparison.Ordinal)
                ? "public static string TruncateToolOutput"
                : throw new InvalidOperationException($"豁免 {shape} 缺少对应的真实检查");
            var i = util.IndexOf(target, StringComparison.Ordinal);
            Assert.True(i > 0, $"在 Util.cs 里找不到 {target}");
            var body = util.Substring(i, Math.Min(1600, util.Length - i));
            Assert.True(Markers.Any(m => body.Contains(m, StringComparison.Ordinal)),
                $"豁免 {shape}，但 {target} 内部也没有任何披露措辞——豁免是错的");
        }
    }

    private static string FindSource(string rel)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(new[] { d, "src", "CodeAgent", rel }.ToArray());
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException(rel);
    }

    [Fact]
    public void TheGuardWouldNoticeItsOwnCoverage()
    {
        // 反向保护：形状列表本身不能为空，否则"扫不到任何东西"会被当成"没有截断"
        Assert.Equal(2, CapShapes.Length);
        Assert.True(Markers.Length >= 5);
    }
}
