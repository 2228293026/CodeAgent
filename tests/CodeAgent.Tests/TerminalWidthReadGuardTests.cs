using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 全仓「终端宽度读数」的一致性守卫。
///
/// 背景：终端最小化、拖拽过程中的抖动会让 <c>Console.WindowWidth</c> 短暂返回 1~2 列。
/// 三处读取已经逐一修过——Program.ConsoleColumns（Round 64）、ConsoleRenderer.TableBudget
/// （Round 88）、InputLine.TryWindowWidth（Round 89）——但它们是**分散修**的，
/// 没有任何机制阻止第 4 处再直接读一次。
///
/// 本守卫把规则钉死：
/// ①除诊断读数（<c>W(() =&gt; …)</c> 包装，/diag 要显示原始值）外，
///   每处宽度读数所在的区域都必须出现解析调用（Resolve*）；
/// ②三处解析点的可信阈值必须一致——否则会出现「状态栏敢信、输入行不敢信」的分裂行为。
/// </summary>
public class TerminalWidthReadGuardTests
{
    private const int ResolutionPoints = 3;

    /// <summary>
    /// 扫描一段源码，返回"既没解析也不是诊断读数"的裸宽度读数（空 = 干净）。
    ///
    /// 抽成方法是为了让这条守卫**可证伪**：把真正的判定做成纯函数之后，才能拿一段
    /// 「故意加了裸读、并按同样手法加了「合法」的解析读」的代码喂进去，确认它确实会报。
    /// 否则"守卫通过"和"守卫根本没在工作"在结果上完全无法区分。
    /// </summary>
    internal static List<string> ScanForUnresolvedWidthReads(string source, string label)
    {
        var offenders = new List<string>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("Console.WindowWidth", StringComparison.Ordinal))
                continue;
            // 诊断读数：/diag 要显示终端的原始值，不该被「可信化」过
            if (lines[i].Contains("W(() =>", StringComparison.Ordinal))
                continue;
            // 否则所在区域必须有解析调用
            var region = string.Join("\n", lines.Skip(Math.Max(0, i - 8)).Take(16));
            if (!region.Contains("Resolve", StringComparison.Ordinal))
                offenders.Add($"{label}:{i + 1} {lines[i].Trim()}");
        }
        return offenders;
    }

    [Fact]
    public void EveryWidthReadIsEitherResolvedOrDiagnostic()
    {
        var offenders = new List<string>();
        foreach (var file in EnumerateSources())
            offenders.AddRange(ScanForUnresolvedWidthReads(File.ReadAllText(file), Path.GetFileName(file)));
        Assert.True(offenders.Count == 0,
            "以下宽度读数既没有解析、也不是诊断读数：" + string.Join(" | ", offenders));
    }

    [Fact]
    public void AllThreeResolutionPointsExist()
    {
        // 三处组件各自持有一套「测量值 + 上一次好值」解析
        foreach (var (file, token) in new[]
        {
            ("Program.cs", "ResolveColumns"),
            ("InputLine.cs", "ResolveWindowWidth"),
            ("ConsoleRenderer.cs", "Program.ResolveColumns"),
        })
        {
            var source = File.ReadAllText(Path.Combine(SourceRoot(), file));
            Assert.Contains(token, source);
        }
    }

    [Fact]
    public void ThresholdsAgreeAcrossComponents()
    {
        // 同一项目里「可信宽度」只能有一套标准。
        // InputLine.MinPlausibleWidth 现在是 Program.MinPlausibleColumns 的别名，
        // 不是两个各自独立的常数——所以这条断言不是靠"记得同步"维持的。
        Assert.Equal(Program.MinPlausibleColumns, InputLine.MinPlausibleWidth);
    }

    [Fact]
    public void ThresholdIsDefinedInExactlyOnePlace()
    {
        // 源码级：MinPlausibleWidth 不得是一个字面量，必须是引用
        var inputLine = File.ReadAllText(Path.Combine(SourceRoot(), "InputLine.cs"));
        var m = Regex.Match(inputLine, @"internal const int MinPlausibleWidth = (?<value>[^;]+);");
        Assert.True(m.Success, "找不到 MinPlausibleWidth 的定义");
        Assert.Equal("Program.MinPlausibleColumns", m.Groups["value"].Value.Trim());
    }

    [Fact]
    public void NoComponentReadsWidthWithoutAFallback()
    {
        // 解析必须真的带「沿用上一次好值」的能力：没有 lastGood 的解析等于没有
        Assert.True(InputLine.ResolveWindowWidth(1, 80) == 80);
        Assert.Equal(80, Program.ResolveColumns(1, 80));
    }

    [Fact]
    public void GuardWouldNoticeANewRawRead()
    {
        // 真·变异测试：拿一段**确实违规**的代码（裸读、附近没有解析调用）喂进去，确认会被报。
        // 前一版只是断言示例字符串里没有 "Resolve"——那跟守卫的真实逻辑毫无关系，
        // 通过了也证明不了任何事。
        const string bad = """
            private static int BareRead()
            {
                try { return Console.WindowWidth; } catch { return 0; }
            }
            """;
        var found = ScanForUnresolvedWidthReads(bad, "bad");
        Assert.Single(found);
        // 报的是**含读数的那一行**（不是所在方法名）
        Assert.Contains("Console.WindowWidth", found[0]);
        Assert.StartsWith("bad:", found[0]);
    }

    [Fact]
    public void Guard_IgnoresReadsThatHaveAResolveNearby()
    {
        // **如实记录这条守卫的粒度**：判定是「±8 行窗口里出现过 Resolve 就算合规」。
        // 也就是说一个真正合规的解析点会顺带为窗口内的裸读开脱。
        // 这是已知的粗放之处（先有规则、后补精度），这里把它钉住，
        // 免得有人以为这条守卫能逐处精确判定。
        const string mixed = """
            private static int Columns()
            {
                try { return Math.Clamp(Console.WindowWidth, 0, 300); } catch { return 0; }
            }
            private static int ColumnsViaHelper()
            {
                return ResolveColumns(() => Console.WindowWidth);
            }
            """;
        Assert.Empty(ScanForUnresolvedWidthReads(mixed, "mixed"));
    }

    [Fact]
    public void Guard_DiagnosticReadsAreStillExempt()
    {
        // /diag 必须能看到终端的原始值，诊断读数不该被判违规
        const string diag = """
            private static string Diag()
            {
                return W(() => Console.WindowWidth).ToString();
            }
            """;
        Assert.Empty(ScanForUnresolvedWidthReads(diag, "diag"));
    }

    [Fact]
    public void Guard_FindsEveryOffendingLineNotJustTheFirst()
    {
        // 报一个就够？不：全列出来，用户才能一次改完
        const string two = """
            private static int A() => Console.WindowWidth;
            private static int B() => Console.WindowWidth;
            """;
        var found = ScanForUnresolvedWidthReads(two, "two");
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Guard_IgnoresCrlfWorkingCopies()
    {
        // Windows 上 core.autocrlf=true 的工作区是 CRLF；扫描必须先归一化
        const string bad = "private static int BareRead()\r\n{\r\n    return Console.WindowWidth;\r\n}\r\n";
        Assert.Single(ScanForUnresolvedWidthReads(bad, "crlf"));
    }

    private static string SourceRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent");
                var program = Path.Combine(candidate, "Program.cs");
                if (Directory.Exists(candidate) && File.Exists(program) && new FileInfo(program).Length > 1000)
                    return candidate;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent");
    }

    private static string[] EnumerateSources() =>
        Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories).ToArray();
}
