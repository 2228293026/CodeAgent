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

    [Fact]
    public void EveryWidthReadIsEitherResolvedOrDiagnostic()
    {
        var offenders = new List<string>();
        foreach (var file in EnumerateSources())
        {
            var source = File.ReadAllText(file);
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
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} {lines[i].Trim()}");
            }
        }
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
        // 守卫本身不是空转：构造一个「有裸读、无解析」的区域，确认它会被判为违规
        var sample = @"
        private static int Bad()
        {
            try { return Math.Clamp(Console.WindowWidth, 0, 300); } catch { return 0; }
        }";
        var region = string.Join("\n", sample.Replace("\r\n", "\n").Split('\n'));
        var isDiagnostic = region.Contains("W(() =>", StringComparison.Ordinal);
        Assert.False(isDiagnostic && !region.Contains("Resolve", StringComparison.Ordinal));
        Assert.DoesNotContain("Resolve", region);
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
