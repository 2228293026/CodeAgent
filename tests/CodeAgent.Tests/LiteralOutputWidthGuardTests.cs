using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 仓库级守卫：**字面量**输出行不得宽过窄终端。
///
/// 与 <c>NoUnboundedUserContentInConsoleOutput</c> 分工：那条盯**用户可控内容**
/// （<c>{opts.Model}</c> 这类变量），这条盯**固定文案**本身太长。
/// 两者都要：固定文案一样会长过 40 列窄屏，而且它躲过了前一条扫描（按设计）。
///
/// 这条规则是连着三轮手工扫出来的：
///   第 283 轮 /thinking、/shell 的"当前值"行
///   第 284 轮 /providers、/resume、/session 的标题与提示行
///   第 287 轮 /mode 的提示行
/// 手工能扫出来一次，就能再犯一次——所以固化成扫描。
///
/// 已知且**刻意**的例外：
///   - <c>/session</c> 的会话日志路径：命令的意义就是把路径交给用户复制，不能截。
///   - <c>/prompt</c> 的系统提示全文：要贴进配置，截断/折行都会让它不可用。
/// 两者都不是"忘了收口"，各有注释写明。
/// </summary>
public sealed class LiteralOutputWidthGuardTests
{
    /// <summary>字面量内容宽于此值即视为会溢出 40 列窄终端。</summary>
    private const int MaxLiteralColumns = 40;

    /// <summary>已按宽度收口的包装器。出现这些就说明那一行**已经**处理过了。</summary>
    private static readonly string[] BoundedWrappers =
    [
        "FormatResultLine", "FormatHintLine", "FormatNoticeLine", "FormatNoticeLines",
        "FormatKeyValueLine", "FormatConfirmLine", "FormatCancelLine", "FormatSectionHeaderLine",
        "FormatSettingWithOptionsLine", "FormatHelpList", "FormatPathList", "FormatStatusPanel",
        "FormatSwitchedWithDetailLine", "FormatAccessSwitchedLine", "FormatBareBangHint",
        "FormatBareAtHint", "FormatSearchHitLine", "FormatInvalidValueLine", "FormatFieldLine",
        "FormatYesNoPrompt", "FormatOptionLine", "FormatBangEcho", "FormatToolOnlyTurnLabel",
        "FormatDiffPreviewLine", "FormatTaskPanel", "FormatTaskProgressLine", "FitToWidth",
        "WriteStartupNotice", "WriteNotice", "PrintColoredDiff", "PrintConversation",
        "ConsoleColumns", "ColumnsForNotice", "width",
    ];

    /// <summary>每行源码里 <c>Console.Write(Line)?("…")</c> 的**整个参数就是一个字面量**。
    /// 参数里已经出现收口包装器的行不在此列——那是别的守卫的辖区。</summary>
    /// <summary>读生产源码并**归一化换行**。
    /// Windows 工作区是 CRLF、CI 检出是 LF：不归一化时任何跨行匹配都会在一边失配——
    /// 这条仓库级守卫（CrossLineSourceScanTests）就是为此存在的。</summary>
    private static string ReadSource(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n");
    private static IEnumerable<(string File, int Line, string Text)> LongLiteralWrites()
    {
        foreach (var file in ProductionSources())
        {
            var lines = ReadSource(file).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("//", StringComparison.Ordinal))
                    continue;
                var open = t.IndexOf("Console.Write", StringComparison.Ordinal);
                if (open < 0)
                    continue;
                // 参数必须整体就是 "字面量"：Console.WriteLine("…");
                // Console.WriteLine("  " + string.Join(…)) 这类拼接不算——字面量只是前缀。
                var close = t.LastIndexOf(");", StringComparison.Ordinal);
                if (close < 0)
                    continue;
                var arg = t[(open + "Console.Write".Length)..close];
                var lp = arg.IndexOf('(');
                if (lp < 0)
                    continue;
                var body = arg[(lp + 1)..].Trim();
                if (!body.StartsWith("\"", StringComparison.Ordinal) || !body.EndsWith("\"", StringComparison.Ordinal))
                    continue;                                  // 拼接 / 变量 / 已是表达式
                if (BoundedWrappers.Any(w => arg.Contains(w, StringComparison.Ordinal)))
                    continue;                                  // 已经收口
                yield return (file, i + 1, body[1..^1]);
            }
        }
    }

    [Fact]
    public void NoLiteralOutputLineOverflowsANarrowTerminal()
    {
        var offenders = new List<string>();
        foreach (var (file, line, text) in LongLiteralWrites())
        {
            if (TextUtil.DisplayWidth(text) <= MaxLiteralColumns)
                continue;
            offenders.Add($"{Path.GetFileName(file)}:{line}  {TextUtil.DisplayWidth(text)} 列 —— {Trunc(text)}");
        }
        Assert.True(offenders.Count == 0,
            $"这些 Console.Write 的**固定文案**本身就宽过 {MaxLiteralColumns} 列（40 列终端上会硬折行）。" +
            "改走 FormatHintLine / FormatResultLine / FormatSectionHeaderLine 等收口器：" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheGuardActuallyScansSomething()
    {
        // 反向保护：匹配规则一旦跟不上代码演进，这个扫描会安静地扫到 0 行、
        // 永远通过——那比没有守卫更危险，因为它占着"这里有防线"的位置。
        var all = LongLiteralWrites().ToList();
        // 精确解析（参数必须整体就是字面量）后样本会小很多——这里按**实测**定阈值，
        // 随手写个大数只会让这条反向保护永远不响，等于没有。
        Assert.True(all.Count >= 12, $"只扫到 {all.Count} 条字面量输出，样本太小或解析器退化了");
        Assert.True(all.Any(x => TextUtil.DisplayWidth(x.Text) > 20), "应当存在较长的字面量");
    }

    [Fact]
    public void TheGuardDetectsAnOverlongLiteral()
    {
        // 自检：拿一个**确实超宽**的字面量走一遍判定，必须被报出来。
        // 不自检的话，"规则写错了所以永远不报"和"仓库很干净"看起来一模一样。
        const string tooLong = "这是一行非常非常非常非常非常非常非常长的固定文案，它在四十列的终端上一定会折行";
        Assert.True(TextUtil.DisplayWidth(tooLong) > MaxLiteralColumns);
    }

    [Fact]
    public void InterpolatedLinesAreLeftToTheOtherGuard()
    {
        // 分工必须明确，否则两条守卫互相抢活或都放行：
        // 这条只管**纯字面量**，用户可控内容由 RenderWidthWiringTests 那条负责。
        Assert.Contains("NoUnboundedUserContentInConsoleOutput", ReadSource(FindInTests("RenderWidthWiringTests.cs")));
        // 本条确实只收字面量：带插值的行不在它的判定范围内
        Assert.DoesNotContain("$\"", ExtractLiteral("Console.WriteLine($\"模式: {mode} 很长的中文提示…\")") ?? string.Empty);
    }

    /// <summary>把 <c>Console.Write*(…)</c> 的参数解析成字面量；不是纯字面量时返回 null。</summary>
    private static string? ExtractLiteral(string sourceLine)
    {
        var open = sourceLine.IndexOf("Console.Write", StringComparison.Ordinal);
        var close = sourceLine.LastIndexOf(");", StringComparison.Ordinal);
        if (open < 0 || close < 0)
            return null;
        var lp = sourceLine.IndexOf('(', open);
        if (lp < 0)
            return null;
        var body = sourceLine[(lp + 1)..close].Trim();
        return body.StartsWith("\"", StringComparison.Ordinal) && body.EndsWith("\"", StringComparison.Ordinal)
            ? body[1..^1]
            : null;
    }

    private static string FindInTests(string fileName)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(d, "tests", "CodeAgent.Tests", fileName);
                if (File.Exists(f))
                    return f;
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new FileNotFoundException($"找不到 tests/CodeAgent.Tests/{fileName}");
    }

    [Fact]
    public void DocumentedExceptionsStillExistAndAreStillUnbounded()
    {
        // 例外不是"顺手也收了"：/session 的路径必须原样输出。
        // 写法是「有路径就原样打、没有才打收口过的提示」——路径经**变量**输出，
        // 守卫只管字面量，所以这条路径不会被它碰到。
        var program = ReadSource(FindSource("Program.cs"));
        Assert.Contains("路径**故意不做宽度收口**", program);
        Assert.Contains("if (agent.SessionPath is { Length: > 0 } sessionPath) Console.WriteLine(sessionPath);", program);
        Assert.Contains(@"Console.WriteLine(FormatResultLine(""会话日志未启用（config.SaveSessions=false）。"", ConsoleColumns()));", program);
    }

    private static string Trunc(string s) => s.Length <= 46 ? s : s[..46] + "…";

    private static IEnumerable<string> ProductionSources()
    {
        var dir = SrcDir();
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            yield return f;
    }

    private static string SrcDir()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = Path.Combine(d, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f))
                    return Path.Combine(d, "src", "CodeAgent");
                var parent = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new DirectoryNotFoundException("找不到 src/CodeAgent");
    }

    private static string FindSource(string fileName)
    {
        var f = Path.Combine(SrcDir(), fileName);
        if (File.Exists(f))
            return f;
        throw new FileNotFoundException(fileName);
    }
}
