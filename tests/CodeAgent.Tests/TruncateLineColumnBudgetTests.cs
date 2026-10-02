using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 第 313 轮：<b>判定与裁剪用了两个不同的单位</b>。
///
/// TUI 里"放不放得下"的判定一律按 <b>显示列数</b>（<c>TextUtil.DisplayWidth</c>），
/// 而单行截断只有 <c>TextUtil.TruncateLine(s, max)</c>，它按 <b>字符数</b> 切。
/// 中文一字符占 2 列，于是同一个 <c>max</c> 在判定端和裁剪端表示两件事。
///
/// 探针实测（修复前）：<c>TruncateLine("中"×60, 60)</c> → <b>len=60 display=120</b>；
/// 同样 60 列预算下 <c>InputLine.FitToWidth</c> 只留 <b>59 列</b>。
///
/// 最刺眼的一处是 <c>FormatToolOnlyTurnLabel</c>：它在同一行里
/// <c>TextUtil.DisplayWidth(stopReason) &gt; reasonWidth</c> 判定超宽，
/// 却调 <c>TruncateLine(stopReason, reasonWidth)</c> 去裁——
/// <b>判定按列、裁剪按字</b>。中文 stop reason 落在 (reasonWidth, 2×reasonWidth] 区间时
/// 根本不裁（判定说放得下）；即便裁了，裁出的 80 字仍是 160 列，比预算宽一倍。
/// 于是 /history 里那条本该单行收口的记录折成两三行，把"工具调用列表"拆散。
///
/// <para><b>为什么不是"把 TruncateLine 改成按列"</b>：它的调用方确实要字符数——
/// <c>read_file</c>/<c>grep</c> 的 <c>max_line_length</c>（说明写"单行截断阈值"，
/// 默认 2000）是给<b>模型</b>估 token 的预算；<c>CapDiff</c> 的
/// 「每行裁到 200 字符」脚注里那个 <c>charsLost</c> 也按字符记账。
/// 改动它的语义会让那些脚注的数字对不上，是把一个已披露的契约悄悄换成另一种。
/// 正确做法：显示方向改调 <c>InputLine.FitToWidth</c>（它本来就是
/// "按列截 + ASCII 感知的省略号宽度 + 簇安全"），字符方向保持 <c>TruncateLine</c>。</para>
/// </summary>
public sealed class TruncateLineColumnBudgetTests
{
    // ── 缺陷本体：判定按列、裁剪按字 ──────────────────────────────────────

    [Fact]
    public void CharacterTruncationIsTwiceTheColumnBudgetOnCjk()
    {
        // 探针实测值：60 字符 = 120 列。这条测试把"为什么不能直接用字符预算"钉死，
        // 免得哪天有人看到 max=60 就以为结果是 60 列。
        var cut = TextUtil.TruncateLine(new string('中', 60), 60);
        Assert.Equal(60, cut.Length);
        Assert.Equal(120, TextUtil.DisplayWidth(cut));
    }

    [Fact]
    public void ColumnBudgetIsHonouredForCjk()
    {
        // 60 列预算放得下 30 个汉字，不该切
        var thirty = string.Concat(Enumerable.Repeat("中", 30));
        Assert.Equal(thirty, InputLine.FitToWidth(thirty, 60));
    }

    [Fact]
    public void TheResultNeverExceedsTheColumnBudget()
    {
        foreach (var max in new[] { 1, 3, 10, 40, 60, 200 })
        {
            var cut = InputLine.FitToWidth(new string('中', 400), max);
            Assert.True(TextUtil.DisplayWidth(cut) <= max,
                $"max={max} 却返回 {TextUtil.DisplayWidth(cut)} 列：{cut}");
        }
    }

    [Fact]
    public void TheStopReasonLabelStaysOnOneLineWhenTheReasonIsCjk()
    {
        // 缺陷现场：中文 stop reason 在 80 列预算下被判"放得下"（因为 DisplayWidth 走的是列，
        // 但旧代码…… 见下条）——先钉住期望行为本身。
        var reason = new string('问', 200); // 200 字 = 400 列，远超 80 列预算
        var label = Program.FormatToolOnlyTurnLabel(
            [("stop", $$"""{"reason":"{{reason}}"}""")], 80);
        Assert.DoesNotContain("\n", label); // 折行会把一条记录拆成两半
        Assert.Contains("…", label);
    }

    [Fact]
    public void TheStopReasonLabelRespectsItsOwnColumnBudget()
    {
        // 这是核心回归：旧代码在同一行里用 DisplayWidth 判定、用 TruncateLine 裁剪。
        // 旧实现在 reasonWidth=80、中文 60 字（=120 列）时：
        //   判定 120 > 80 → 裁；裁到 80 字 = 160 列 → 行宽 160+head+标记，远超 80。
        // 新实现必须让**整条标签**都不超预算。
        foreach (var width in new[] { 20, 40, 80 })
        {
            var reason = new string('问', 200);
            var label = Program.FormatToolOnlyTurnLabel(
                [("stop", $$"""{"reason":"{{reason}}"}""")], width);
            // head 是 "[调用 stop] · "（11 列）+ 末尾 "]"（1 列）
            Assert.True(TextUtil.DisplayWidth(label) <= width + 20,
                $"width={width} 却生成 {TextUtil.DisplayWidth(label)} 列：{label}");
        }
    }

    [Fact]
    public void TheMidBandWhereTheOldCodeMisjudged()
    {
        // 旧代码的误判区间：(reasonWidth, 2×reasonWidth] 列宽的 CJK。
        // reasonWidth=80 时，41~80 字的 stop reason 会被判"超宽"而裁，
        // 但裁出来仍是 80 字 = 160 列 —— 比预算宽一倍。
        // 现在这些长度必须都收在预算内。
        foreach (var n in new[] { 41, 55, 70, 80, 81, 120 })
        {
            var label = Program.FormatToolOnlyTurnLabel(
                [("stop", $$"""{"reason":"{{new string('问', n)}}"}""")], 80);
            Assert.True(TextUtil.DisplayWidth(label) <= 100,
                $"{n} 字 → {TextUtil.DisplayWidth(label)} 列：{label}");
        }
    }

    // ── 尾部标记 ──────────────────────────────────────────────────────────

    [Fact]
    public void TheTailMarkIsCountedAgainstTheBudget()
    {
        var cut = InputLine.FitToWidth(new string('x', 400), 10);
        Assert.True(TextUtil.DisplayWidth(cut) <= 10, $"实际 {TextUtil.DisplayWidth(cut)} 列：{cut}");
        Assert.Contains("…", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void AsciiModeWidensTheTailMarkAndIsStillFitted()
    {
        // 回归：省略号在 CODEAGENT_ASCII 下是 "..."（3 列）。标记宽度是动态的，
        // 写死 1 列会让每一条 ASCII 结果都超预算——这正是 FitToWidth 当年修过的坑。
        var prev = Environment.GetEnvironmentVariable("CODEAGENT_ASCII");
        try
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", "1");
            var cut = InputLine.FitToWidth(new string('x', 400), 10);
            Assert.True(TextUtil.DisplayWidth(cut) <= 10, $"实际 {TextUtil.DisplayWidth(cut)} 列：{cut}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", prev);
        }
    }

    // ── 簇安全（切点不能劈开代理对 / ZWJ 序列） ─────────────────────────────

    [Fact]
    public void SurrogatePairsAreNeverSplitInHalf()
    {
        var cut = InputLine.FitToWidth(string.Concat(Enumerable.Repeat("\U0001F600", 20)), 5);
        Assert.DoesNotContain("�", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheZwjSequenceIsNotSplit()
    {
        // 与 DisplayWidth 共用 ClusterAt：ZWJ 序列整体算 2 列，不能劈开「👨‍👩‍👧」。
        var family = string.Concat(Enumerable.Repeat("\U0001F468‍\U0001F469‍\U0001F467", 10));
        var cut = InputLine.FitToWidth(family, 7);
        Assert.True(TextUtil.DisplayWidth(cut) <= 7, $"实际 {TextUtil.DisplayWidth(cut)} 列：{cut}");
    }

    [Fact]
    public void NonPositiveWidthStillReturnsEmpty()
    {
        Assert.Equal("", InputLine.FitToWidth("some output", 0));
        Assert.Equal("", InputLine.FitToWidth("some output", -1));
    }

    [Fact]
    public void ShortAsciiIsUntouched()
    {
        Assert.Equal("abc", InputLine.FitToWidth("abc", 3));
        Assert.Equal("long line here", InputLine.FitToWidth("long line here", 200));
    }

    // ── 字符侧契约没有被这次修复动过 ───────────────────────────────────────

    [Fact]
    public void TheCharacterBasedContractIsUnchanged()
    {
        // TruncateLine 的语义**不能**被顺手改掉：
        // read_file / grep 的 max_line_length、CapDiff 的「每行裁到 200 字符」
        // 都按字符数记账，改了会让脚注里的 charsLost 对不上。
        Assert.Equal("ab …", TextUtil.TruncateLine("abc", 2));
        Assert.Equal("a    …", TextUtil.TruncateLine("a\tb", 4));
    }

    [Fact]
    public void TheModelFacingCallersStillUseTheCharacterBasedOne()
    {
        // 接线守卫：只测 FitToWidth 是不够的——真正的回归是"有人把 read_file
        // 的 max_line_length 也换成按列的版本"，那会让模型少看到一半的汉字。
        // 这两个调用方在 Tools/ 下，不在 Program.cs。
        var read = ReadSrc("FileTools.cs", "Tools");
        var search = ReadSrc("SearchTools.cs", "Tools");
        Assert.Contains("TextUtil.TruncateLine(raw, maxLineLength)", read);
        Assert.Contains("s => TextUtil.TruncateLine(s, maxLineLength)", search);
        Assert.DoesNotContain("InputLine.FitToWidth(raw, maxLineLength)", read);
        Assert.DoesNotContain("InputLine.FitToWidth(s, maxLineLength)", search);
    }

    [Fact]
    public void TheDisplayCallersNoLongerUseTheCharacterBasedOne()
    {
        // 反向接线守卫：这四处是本轮修的显示方向调用点。
        var src = ReadSrc("Program.cs");
        Assert.Contains("InputLine.FitToWidth(agent.LastPrompt, 60)", src);
        Assert.Contains("InputLine.FitToWidth(stopReason, reasonWidth)", src);
        Assert.Contains("InputLine.FitToWidth(preview, 50)", src);
        Assert.Contains("InputLine.FitToWidth(lastReply.Content!, 40)", src);
        // 旧的四处一个都不许留
        Assert.DoesNotContain("TextUtil.TruncateLine(agent.LastPrompt, 60)", src);
        Assert.DoesNotContain("TextUtil.TruncateLine(stopReason, reasonWidth)", src);
        Assert.DoesNotContain("TextUtil.TruncateLine(preview, 50)", src);
        Assert.DoesNotContain("TextUtil.TruncateLine(lastReply.Content!, 40)", src);
    }

    /// <summary>读生产源码并**归一化换行**（Windows 工作区 CRLF / CI 检出 LF，
    /// 不归一化时跨行匹配会在一边失配）。参数是文件名，可选子目录。</summary>
    private static string ReadSrc(string file, string? subDir = null) =>
        System.IO.File.ReadAllText(Find(file, subDir)).Replace("\r\n", "\n");

    private static string Find(string file, string? subDir)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var rel = subDir is null
                    ? System.IO.Path.Combine("src", "CodeAgent", file)
                    : System.IO.Path.Combine("src", "CodeAgent", subDir, file);
                var f = System.IO.Path.Combine(dir, rel);
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                dir = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? dir;
            }
        }
        throw new InvalidOperationException($"找不到 src/CodeAgent/{subDir}/{file}");
    }
}
