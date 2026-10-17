using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// /config 的 Access 行。这一行此前有两类毛病，各自都能让用户对**安全边界**判断错：
///
/// <para><b>① 语义错（更危险）</b>：<c>readOnlyDirs</c> 只在 <c>fileAccess=whitelist</c>
/// 时才生效（<c>Workspace</c> 的 <c>_whitelistEnabled</c> 只在这一档置位），
/// 而旧代码无条件写成「full（只读白名单: A, B, C）」。本机配置恰好是
/// <c>full</c> + 三个白名单目录——读起来就像那三个目录正受只读保护，
/// 实际上 full 放行一切。**把没生效的护栏画在屏幕上，比不画更危险。**</para>
///
/// <para><b>② 沉默截断</b>：整条值走通用截断，屏幕上只剩前两项半路径
/// （<c>D:/Projects/adofai-libs, D:/Projects/JipperKeyViewer, D:/Pr…</c>），
/// 既不说后面还有多少，也看不见最后一项。安全相关的一行被切成这样，
/// 用户没有任何办法判断权限是否合适。</para>
///
/// 修法：语义上分档（生效才列目录，未生效只说状态 + 另一行备查），
/// 宽度上分层（完整 → 尾部 N 项 + 计数 → 模式名 + 见下行 → 只剩模式名），
/// 并且**任何一档都不许沉默**：丢了内容就必须交代还剩多少。
/// </summary>
public sealed class ConfigAccessRowTests
{
    private static readonly List<string> Dirs =
    [
        "D:/Projects/adofai-libs", "D:/Projects/JipperKeyViewer",
        "D:/Projects/JipperOverlayer", "D:/Projects/some-very-long-directory-name",
    ];

    // ── ① 语义：未生效的护栏不许画成生效 ──────────────────────────────────

    [Fact]
    public void NonWhitelistModes_SayTheWhitelistIsNotActive()
    {
        // 回归：旧代码在 full 档照列目录，读起来像"这三个目录只读保护中"
        foreach (var mode in new[] { "full", "strict" })
        {
            var (value, hint) = Program.FormatAccessRow(mode, Dirs, 200, 200);
            Assert.Contains("未生效", value);
            Assert.DoesNotContain(Dirs[0], value, StringComparison.Ordinal);
            // 目录挪到备查行，并且**那一行也标明未生效**——否则读者仍以为它在管
            Assert.NotNull(hint);
            Assert.Contains("未生效", hint);
            Assert.Contains(Dirs[0], hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WhitelistMode_ListsTheDirsInTheValueWithoutANoticeWord()
    {
        // 生效时目录就该在这一行里（那才是护栏），且不该出现"未生效"的噪声
        var (value, hint) = Program.FormatAccessRow("whitelist", Dirs, 500, 200);
        Assert.Contains("whitelist", value);
        Assert.Contains(Dirs[0], value, StringComparison.Ordinal);
        Assert.Contains(Dirs[^1], value, StringComparison.Ordinal);
        Assert.DoesNotContain("未生效", value);
        Assert.Null(hint); // 全在值里了，不需要第二行
    }

    [Fact]
    public void NoDirsConfigured_IsJustTheModeName()
    {
        // 没配白名单时不许凭空冒出"未生效"或空括号：那会让用户去找一个不存在的配置
        foreach (var mode in new[] { "strict", "whitelist", "full" })
        {
            var (value, hint) = Program.FormatAccessRow(mode, [], 80, 80);
            Assert.Equal(mode, value);
            Assert.Null(hint);
        }
    }

    [Fact]
    public void TheWhitelistGateMatchesTheRealOne()
    {
        // 这条测试守的是**语义与实现同源**：生效判定必须和 Workspace 一致
        // （只有 whitelist 档才启用白名单）。若哪天 Workspace 改成 full 也启用，
        // 这里会失败并提醒同步——而不是让 /config 继续撒谎。
        var src = File.ReadAllText(FindSource("ToolRegistry.cs")).Replace("\r\n", "\n");
        Assert.Contains("_whitelistEnabled = string.Equals(fileAccess, \"whitelist\", StringComparison.OrdinalIgnoreCase);", src);
        Assert.Contains("allowReadOnly && _whitelistEnabled && IsInReadOnly(real)", src);
    }

    // ── ② 宽度：任何一档都不许沉默 ────────────────────────────────────────

    [Fact]
    public void EveryWidthKeepsTheModeName()
    {
        // 模式名是这一行首先回答的问题（"我现在是哪一档"），
        // 路径推不出来，任何宽度下都不能丢。
        foreach (var width in new[] { 120, 100, 80, 60, 50, 40, 30, 24, 18 })
        {
            var budget = Program.KeyValueValueBudget(width, 6);
            foreach (var mode in new[] { "strict", "whitelist", "full" })
            {
                var (value, _) = Program.FormatAccessRow(mode, Dirs, budget, width);
                Assert.Contains(mode, value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EveryWidthEitherShowsContentOrSaysHowManyAreHidden()
    {
        // 核心回归：不许出现"截了一半、什么都不说"。
        // 判定看**两行合计**：值那一格极窄时信息会挪到补充行，屏幕上仍然交代得清。
        // 但至少要有一行给出内容或数量——两行都沉默才是这一轮要修的形态。
        foreach (var width in new[] { 120, 100, 80, 60, 50, 40, 30, 24, 18 })
        {
            var budget = Program.KeyValueValueBudget(width, 6);
            foreach (var mode in new[] { "strict", "whitelist", "full" })
            {
                var (value, hint) = Program.FormatAccessRow(mode, Dirs, budget, width);
                bool Discloses(string s) =>
                    Dirs.Any(d => s.Contains(d, StringComparison.Ordinal))
                    || s.Contains("未显示", StringComparison.Ordinal)
                    || s.Contains("见下行", StringComparison.Ordinal)
                    || s.Contains("未展开", StringComparison.Ordinal)
                    || s.Contains("未生效", StringComparison.Ordinal)
                    || s.Contains($"共 {Dirs.Count}", StringComparison.Ordinal);
                Assert.True(Discloses(value) || (hint is not null && Discloses(hint)),
                    $"width={width} mode={mode} 两行都没给出目录、也没交代还剩多少：值=[{value}] 补充行=[{hint}]");
                // 说了"见下行"就必须真的有下行
                if (value.Contains("见下行", StringComparison.Ordinal))
                    Assert.NotNull(hint);
            }
        }
    }

    [Fact]
    public void NeitherRowOverflowsTheTerminal()
    {
        // 两条行都必须收在自己的预算里。回归：早先的收口函数在"一项都放不下"时
        // 原样返回整条（144 列塞进 19 列），函数存在的意义被自己的兜底破坏。
        foreach (var width in new[] { 120, 100, 80, 60, 50, 40, 30, 24 })
        {
            var budget = Program.KeyValueValueBudget(width, 6);
            foreach (var mode in new[] { "strict", "whitelist", "full" })
            {
                var (value, hint) = Program.FormatAccessRow(mode, Dirs, budget, width);
                Assert.True(TextUtil.DisplayWidth(value) <= Math.Max(1, budget),
                    $"width={width} mode={mode} 值溢出：{TextUtil.DisplayWidth(value)} > {budget}：[{value}]");
                if (hint is not null)
                {
                    var line = Program.FormatHintLine(hint, width);
                    Assert.True(TextUtil.DisplayWidth(line) <= width,
                        $"width={width} mode={mode} 补充行溢出：{TextUtil.DisplayWidth(line)} > {width}：[{line}]");
                }
            }
        }
    }

    [Fact]
    public void WhenTheTailDoesNotFit_TheValueSaysHowManyAndPointsDownstairs()
    {
        // 变异验证发现的第二个缺口（变异：这一档只返回模式名，本文件其余全绿）。
        // 中间这一档的价值就是**保住数量**：窄屏上目录挪到了下一行，
        // 但"一共有几个"必须留在值里——否则用户看到 "whitelist" 会以为没有白名单。
        var budget = Program.KeyValueValueBudget(80, 6);
        var (value, hint) = Program.FormatAccessRow("whitelist", Dirs, budget, 80);
        Assert.Contains("whitelist", value);
        Assert.Contains($"{Dirs.Count} 个只读目录", value);
        Assert.Contains("见下行", value);
        Assert.NotNull(hint);
        // 下一行必须真的给出目录（不能只有一句"见下行"）
        Assert.Contains(Dirs[^1], hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyValueLineItselfFits()
    {
        // 只测 FormatAccessRow 不够：值要经过 FormatKeyValueLine 才上屏，
        // 键列 + 缩进 + 分隔符也会吃宽度。整行收得住才算真的收住了。
        foreach (var width in new[] { 120, 100, 80, 60, 50, 40, 30, 24 })
        {
            var keyWidth = 6;
            var budget = Program.KeyValueValueBudget(width, keyWidth);
            var (value, _) = Program.FormatAccessRow("whitelist", Dirs, budget, width);
            var line = Program.FormatKeyValueLine("Access", value, keyWidth, width);
            Assert.True(TextUtil.DisplayWidth(line) <= width,
                $"width={width} 整行溢出：{TextUtil.DisplayWidth(line)} > {width}：[{line}]");
        }
    }

    [Fact]
    public void UnknownWidthReturnsEverythingUntouched()
    {
        // 宽度未知（重定向输出）时不许猜折行位置，也不许截：
        // 这条语义在这个仓库里反复被栽（预算 ≤ 0 不等于"预算 0"）。
        var (value, hint) = Program.FormatAccessRow("whitelist", Dirs, 0, 0);
        Assert.Equal($"whitelist（只读白名单: {string.Join(", ", Dirs)}）", value);
        Assert.Null(hint);
    }

    // ── 收口函数本身 ──────────────────────────────────────────────────────

    [Fact]
    public void ListShrinksFromTheHeadKeepsTheTailAndSaysTheCount()
    {
        // 保尾部：最后一项（常是最近加的）不该因为"排在后面"先被丢掉
        var items = Enumerable.Range(1, 6).Select(i => $"D:/Projects/dir-{i}-abcdefghij").ToList();
        var rendered = Program.FormatListValue("x: ", items, "", 120);
        Assert.Contains(items[^1], rendered, StringComparison.Ordinal);
        Assert.Contains("未显示", rendered);
        Assert.DoesNotContain(items[0], rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ListThatFitsIsReturnedVerbatim()
    {
        // 放得下就不许加"另有"噪声：无端提示会让用户以为漏了东西
        var rendered = Program.FormatListValue("x: ", ["a", "b"], "", 200);
        Assert.Equal("x: a, b", rendered);
    }

    [Fact]
    public void ListThatCannotFitEvenOneItemStillDisclosesTheTotal()
    {
        // 一项都放不下时，**不能默默退回头部截断**——那正是本轮要修的形态。
        // 至少要把"一共几项"说出来。
        var items = Enumerable.Range(1, 12).Select(i => $"D:/very/long/path/{i}").ToList();
        // 30 列放不下整句，会退到「只报数量」那一档——数量本身就是底线信息
        foreach (var budget in new[] { 30, 22, 16 })
        {
            var rendered = Program.FormatListValue("只读目录: ", items, "", budget);
            Assert.True(TextUtil.DisplayWidth(rendered) <= budget,
                $"budget={budget} 溢出：{TextUtil.DisplayWidth(rendered)}：[{rendered}]");
            Assert.Contains("12", rendered);
        }
        // 有余量时要把"为什么没展开"也说出来，而不是只丢一个数字
        Assert.Contains("未展开", Program.FormatListValue("只读目录: ", items, "", 40));
    }

    [Fact]
    public void TheAsciiEllipsisIsBudgetedAtItsRealWidth()
    {
        // 省略号宽度不是常数（ASCII 退回 1 列 → 3 列）。写死 1 会让每条结果超宽 2 列，
        // 且只在窄屏触发——正是这种偏差最难被发现。
        var prev = Environment.GetEnvironmentVariable("CODEAGENT_ASCII");
        try
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", "1");
            var items = Enumerable.Range(1, 8).Select(i => $"D:/Projects/directory-{i}").ToList();
            foreach (var budget in new[] { 40, 60, 80, 120 })
            {
                var rendered = Program.FormatListValue("x: ", items, "", budget);
                Assert.True(TextUtil.DisplayWidth(rendered) <= budget,
                    $"budget={budget} ASCII 省略号下溢出：{TextUtil.DisplayWidth(rendered)}：[{rendered}]");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", prev);
        }
    }

    [Fact]
    public void DroppingThePrefixIsDecidedWithTheRealEllipsisWidth()
    {
        // 变异验证发现的缺口（变异：把 EllipsisWidth 写成 1，本文件其余 14 项全绿）。
        // 这条分支只决定「前缀丢不丢」，判宽 2 列的后果不是溢出（列表层自己还会收），
        // 而是**白丢内容**：以为前缀放得下 → 带上前缀后一项都塞不进 → 退回「共 N 项」，
        // 屏幕上只剩一个数字，明明丢前缀就能看见真实目录。
        //
        // 差异只在 ASCII 退回时存在（省略号 1 列 → 3 列），所以必须在那套字形下测。
        var prev = Environment.GetEnvironmentVariable("CODEAGENT_ASCII");
        try
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", "1");
            const string prefix = "x: ";
            var items = new List<string> { "D:/A-lib-aaaaaaaaaaaaa", "D:/B-lib-bbbbbbbbbbbbb" };
            // 用**真实**省略号宽度算出「带前缀需要多宽」，再把预算卡在它下面一列：
            // 正确实现必须丢前缀；写死 1 的实现会以为放得下，于是带上前缀、再把内容挤掉。
            var noteWidth = TextUtil.DisplayWidth("，" + Program.ListHiddenNote(items.Count - 1));
            var needWithPrefix = TextUtil.DisplayWidth(items[^1]) + SafeColor.Glyphs.EllipsisWidth
                                 + noteWidth + TextUtil.DisplayWidth(prefix);
            var budget = needWithPrefix - 1;
            var rendered = Program.FormatListValueCompact(prefix, items, "", budget);
            Assert.Contains(items[^1], rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(prefix, rendered, StringComparison.Ordinal);
            Assert.True(TextUtil.DisplayWidth(rendered) <= budget,
                $"溢出：{TextUtil.DisplayWidth(rendered)} > {budget}：[{rendered}]");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", prev);
        }
    }

    // ── 接线：/config 真的用了它 ───────────────────────────────────────────

    [Fact]
    public void TheConfigCommandActuallyCallsIt()
    {
        // 只测纯函数证明不了接线：回归的形态是"函数很好，/config 没调用"
        var src = ReadSource("Program.cs");
        Assert.Contains("FormatAccessRow(", src);
        Assert.Contains("access.Hint is { } hint", src);
        // 旧的死计算不许回来：它就是"值里列目录、却不管生效与否"的来源
        Assert.DoesNotContain("var roDirs = config.ReadOnlyDirs.Count == 0", src);
    }

    // ── 工具 ──────────────────────────────────────────────────────────────

    private static string ReadSource(string file) =>
        File.ReadAllText(FindSource(file)).Replace("\r\n", "\n");

    private static string FindSource(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                foreach (var rel in new[]
                         {
                             Path.Combine(d, "src", "CodeAgent", file),
                             Path.Combine(d, "src", "CodeAgent", "Tools", file),
                         })
                {
                    if (File.Exists(rel) && new FileInfo(rel).Length > 1000)
                        return rel;
                }
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException(file);
    }
}
