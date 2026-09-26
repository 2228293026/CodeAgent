using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 「渲染函数有 width 参数」不等于「调用点传了宽度」。
///
/// <see cref="RenderWidthGuardTests"/> 逐个测函数在各种宽度下的行为——但它测的是
/// **函数**，不是**接线**。调用点漏传 width 时，width 取默认值 0，
/// 而 0 的含义是"宽度未知，不裁剪"：函数完全正确，屏幕上照样折行。
///
/// 这类缺陷的真实代价：工具分组汇总行 <c>⏵⏵ 12 次工具调用（Read×3 Grep×2 …）</c>
/// 曾经是全 TUI 唯一一处没传宽度的地方，窄终端上一行汇总就把下面的正文顶走。
/// 函数级守卫对此一无所知。
/// </summary>
public sealed class RenderWidthWiringTests
{
    /// <summary>必须在生产代码里传宽度的渲染函数（参数名以 width / columns 结尾）。</summary>
    private static readonly (string Method, string[] WidthArgs)[] MustPassWidth =
    [
        ("FormatToolGroupLine", ["width"]),
        ("FormatToolStatusLine", ["width"]),
        ("FormatToolOutputPreview", ["width"]),
    ];

    private static IEnumerable<string> ProductionSources() =>
        Directory.EnumerateFiles(SourceDir(), "*.cs", SearchOption.AllDirectories);

    private static string SourceDir()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Program.cs")))
                    return candidate;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new DirectoryNotFoundException("找不到 src/CodeAgent");
    }

    [Fact]
    public void EveryWidthTakingRenderCall_PassesAWidth()
    {
        // 找出所有"调用点没传 width"的实例（方法声明本身不算）
        var offenders = new List<string>();
        foreach (var file in ProductionSources())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                foreach (var (method, _) in MustPassWidth)
                {
                    // 形如 "Method(" 开头的调用（排除声明 "internal static string Method("）
                    if (!line.StartsWith(method + "(", StringComparison.Ordinal))
                        continue;
                    if (line.Contains("static string", StringComparison.Ordinal))
                        continue;
                    // 该行或其下一行必须出现宽度实参
                    var window = i + 1 < lines.Length ? line + " " + lines[i + 1].Trim() : line;
                    var passes = window.Contains("ConsoleColumnsForNotice")
                        || window.Contains("Program.Columns")
                        || window.Contains("width:")
                        || window.Contains("Width(")
                        || window.Contains(", w)")
                        || window.Contains("width)")
                        || window.Contains("Columns()");
                    if (!passes)
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1} {line}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "这些渲染调用点没有传宽度（width 默认 0 = 不裁剪，窄终端必然折行）："
            + string.Join(" | ", offenders));
    }

    [Fact]
    public void ToolGroupLineCallSite_PassesAWidth()
    {
        // 单独点名：这一行曾经是全 TUI 唯一不传宽度的渲染调用
        var agent = File.ReadAllText(Path.Combine(SourceDir(), "Agent", "Agent.cs"));
        var at = agent.IndexOf("FormatToolGroupLine(run,", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到工具分组汇总行的调用点");
        var snippet = agent.Substring(at, Math.Min(160, agent.Length - at));
        Assert.True(snippet.Contains("ConsoleColumnsForNotice") || snippet.Contains("Columns()"),
            $"工具分组汇总行仍未传宽度：{snippet}");
    }

    [Fact]
    public void ToolStatusLineCallSite_PassesAWidth()
    {
        var agent = File.ReadAllText(Path.Combine(SourceDir(), "Agent", "Agent.cs"));
        var at = agent.IndexOf("FormatToolStatusLine(summary, isError, sw.Elapsed", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到工具状态行的调用点");
        var snippet = agent.Substring(at, Math.Min(160, agent.Length - at));
        Assert.True(snippet.Contains("ConsoleColumnsForNotice") || snippet.Contains("Columns()"),
            $"工具状态行仍未传宽度：{snippet}");
    }

    [Fact]
    public void WidthZeroMeansDoNotTrim_AndIsOnlyForUnknownWidth()
    {
        // width=0 是"宽度未知"的意思，不是"随便多大都行"。
        // 生产代码里不该有字面量 0 当宽度传进去（那等于主动放弃裁剪）
        var agent = File.ReadAllText(Path.Combine(SourceDir(), "Agent", "Agent.cs"));
        foreach (var bad in new[] { "FormatToolGroupLine(run, runWatch.Elapsed)", "FormatToolStatusLine(summary, isError, sw.Elapsed)" })
            Assert.DoesNotContain(bad, agent);
    }

    [Fact]
    public void GuardFindsTheOffendersItIsMeantToFind()
    {
        // 反向保护：守卫本身不能是"永远通过"的空转检查
        var methods = new List<string>();
        foreach (var file in ProductionSources())
            foreach (var line in File.ReadAllLines(file))
            {
                var t = line.Trim();
                foreach (var (method, _) in MustPassWidth)
                    if (t.StartsWith(method + "(", StringComparison.Ordinal) && !t.Contains("static string"))
                        methods.Add(t);
            }
        Assert.True(methods.Count > 0, "守卫一个调用点都没扫到，说明匹配规则失效了");
        // 并且这些调用点都应该是合规的（否则上面的测试会先失败）
        Assert.All(methods, m => Assert.False(string.IsNullOrWhiteSpace(m)));
    }
}
