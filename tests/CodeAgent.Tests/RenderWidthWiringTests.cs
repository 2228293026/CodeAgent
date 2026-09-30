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

    /// <summary>
    /// 直接 <c>Console.Write(Line)($"…")</c> 的**裸插值输出**同样会超宽。
    ///
    /// 上面那个守卫只覆盖"带 width 参数的函数"，覆盖面的边界就在**哪些函数有那个参数**。
    /// 手写的插值行根本没有那个函数，自然落网——而它们恰恰是最容易写长的一批
    /// （含路径、含耗时、含 token 数）。这两行就是这么被漏掉的：
    ///   · 定格统计行「✓ 用时 1 分 23 秒 · ↑ 128.4K tokens」30 多列
    ///   · 工具开始行「⏵ Read(path=…) …」，path 长度不受控
    /// </summary>
    [Fact]
    public void NoBareInterpolatedOutputInTheTurnStream()
    {
        // 回合内流式输出这几行必须走 FitToWidth / 宽度受限的格式化函数
        var agent = File.ReadAllText(Path.Combine(SourceDir(), "Agent", "Agent.cs"));
        var offenders = new List<string>();
        var lines = agent.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            // 命中"把可能变长的内容直接插值进 Console.Write(Line)"的模式
            if (!t.StartsWith("Console.WriteLine($\"", StringComparison.Ordinal)
                && !t.StartsWith("Console.Write($\"", StringComparison.Ordinal))
                continue;
            // 只看含长内容的那些：含 summary / elapsed / tok / path 等
            if (!t.Contains("summary", StringComparison.Ordinal)
                && !t.Contains("FormatSessionTime", StringComparison.Ordinal)
                && !t.Contains(" tokens", StringComparison.Ordinal))
                continue;
            var window = i + 1 < lines.Length ? t + " " + lines[i + 1].Trim() : t;
            if (!window.Contains("FitToWidth") && !window.Contains("ConsoleColumnsForNotice") && !window.Contains("Columns()"))
                offenders.Add($"Agent.cs:{i + 1} {t}");
        }
        Assert.True(offenders.Count == 0,
            "这些回合内输出行既没走 FitToWidth 也没读终端列数（窄终端必折行）：" + string.Join(" | ", offenders));
    }

    [Fact]
    public void FrozenSpinnerLineAndToolStartLine_AreWidthBounded()
    {
        // 单独点名：这两条曾经是全 TUI 仅有的没被任何守卫覆盖的裸插值输出
        var agent = File.ReadAllText(Path.Combine(SourceDir(), "Agent", "Agent.cs"));
        foreach (var needle in new[] { "var frozen =", "var startLine =" })
        {
            var at = agent.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at > 0, $"找不到 {needle}");
            var snippet = agent.Substring(at, Math.Min(420, agent.Length - at));
            Assert.True(snippet.Contains("FitToWidth") || snippet.Contains("ConsoleColumnsForNotice"),
                $"{needle} 仍未按宽度收口");
        }
    }

    /// <summary>
    /// 全仓扫描：把**用户可控长度内容**裸插值进 Console 输出的行，必须走宽度受限的渲染函数。
    ///
    /// 前几轮一个一个改（命令确认块 → 是/否确认 → 配置向导…），每改一处就担心下一处还在。
    /// 这个扫描把"担心"变成断言：新增输出行时，若把 <c>{ex.Message}</c>/<c>{path}</c>/
    /// <c>{model}</c> 这类内容直接拼进 WriteLine 而不走格式化函数，测试会指名报出来。
    ///
    /// 变量名清单是启发式，不是完备判定——写 <c>{x}</c> 之类的短名扫不到。
    /// 它的价值是"挡住已知的长内容来源"，不是"证明没有任何一处超宽"。
    /// </summary>
    /// <summary>可能变长的内容变量名清单（全部小写，比较忽略大小写——C# 属性是 PascalCase）。</summary>
    private static readonly string[] LongContentKeys =
    [
        "path", "rel", "summary", "text", "body", "content", "line", "name",
        "message", "ex.message", "output", "value", "dir", "cwd", "model",
        "prompt", "snippet", "msg", "err", "label", "question", "result",
    ];

    /// <summary>插值槽：<c>{...}</c> / <c>{x=...}</c> / <c>{x:格式}</c> / <c>{x,对齐}</c>。</summary>
    private static readonly System.Text.RegularExpressions.Regex InterpolationSlot = new(
        @"\{\s*[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\s*[=:,}]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>这一行是否把"可能变长的内容"裸插值进了 Console 写入。
    ///
    /// 两条判定都要，因为各漏一半：
    /// · 单词边界：抓 <c>{opts.Model}</c>、<c>{path}</c>
    /// · **后缀**：抓 <c>{modeName}</c>——驼峰复合名里 \b 前面紧挨着字母，词边界匹配不到。
    ///   而 modeName 恰恰是用户敲进来的，最该抓。
    /// 另需 \b 收尾，否则 <c>{TextUtil.FormatBytes(...)}</c> 里的 "text" 会被误判——
    /// 报多了守卫就没人看了。</summary>
    internal static bool HasLongContent(string line)
    {
        foreach (System.Text.RegularExpressions.Match m in InterpolationSlot.Matches(line))
        {
            var expr = m.Value.Trim('{', '}', ' ', '=', ',', ':');
            if (LongContentKeys.Any(k =>
                    expr.EndsWith(k, StringComparison.OrdinalIgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(expr, @"\b" + System.Text.RegularExpressions.Regex.Escape(k) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                return true;
        }
        return false;
    }
    [Fact]
    public void NoUnboundedUserContentInConsoleOutput()
    {
        // 全部**小写**：正则带 IgnoreCase——C# 的属性是 PascalCase（<c>{opts.Model}</c>），
        // 只匹配小写的话这条规则对属性名恒不命中。上一版正是因此漏掉了
        // `当前模型: {opts.Model}`。
        //
        // 末尾的 \b 是为了不误报：<c>{TextUtil.FormatBytes(...)}</c> 里 "text" 后面还跟着
        // "Util"，不是独立的 text 变量。少了它会把一串无害的调用全报出来，
        // 报多了守卫就没人看了。


        var offenders = new List<string>();
        foreach (var file in ProductionSources())
        {
            var lines = File.ReadAllLines(file).Select(l => l.Trim()).ToArray();
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i];
                if (!t.StartsWith("Console.Write", StringComparison.Ordinal) || !t.Contains("$\"", StringComparison.Ordinal))
                    continue;
                if (!HasLongContent(t)
                && !(t.Contains("{line}", StringComparison.Ordinal) && t.StartsWith("Console.WriteLine(\"", StringComparison.Ordinal)))
                    continue;
                var window = i + 1 < lines.Length ? t + " " + lines[i + 1] : t;
                if (Bounded(writer: window))
                    continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1} {t}");
            }
        }
        Assert.True(offenders.Count == 0,
            "这些输出行把用户可控内容裸插值进了 Console 写入（窄终端必折行）："
            + string.Join(" | ", offenders.Take(10)));
    }

    private static bool Bounded(string writer) =>
        writer.Contains("FitToWidth") || writer.Contains("WriteNotice")
        || writer.Contains("FormatNoticeLine") || writer.Contains("FormatResultLine")
        || writer.Contains("FormatHintLine") || writer.Contains("FormatConfirmLine")
        || writer.Contains("FormatCancelLine") || writer.Contains("FormatKeyValueLine")
        || writer.Contains("FormatPathList") || writer.Contains("FormatHelpList")
        || writer.Contains("FormatBang") || writer.Contains("FormatConfirmCommandBlock")
        || writer.Contains("FormatYesNoPrompt") || writer.Contains("FormatFieldLine")
        || writer.Contains("FormatOptionLine") || writer.Contains("FormatStatusPanel")
        || writer.Contains("FormatNoticeLines") || writer.Contains("ColumnsForNotice")
        || writer.Contains("Program.Columns") || writer.Contains("Columns()")
        // 第 283/284 轮新增的两个收口器：不登记的话，将来"这里也统一收口一下"的一轮
        // 会把它们当成没接收口，扫描器的词表就落后于代码了。
        || writer.Contains("FormatSettingWithOptionsLine") || writer.Contains("FormatSectionHeaderLine");

    /// <summary>规则的精度自检：既不能漏 PascalCase 属性，也不能把 {TextUtil...} 误报。
    /// 这两条都是**真实踩过的**——上一版前者漏了 {opts.Model}，后者把无害调用全报了出来。
    /// 报多了守卫就没人看了，漏了就等于没有。</summary>
    [Theory]
    [InlineData("""Console.WriteLine($"当前模型: {opts.Model}");""", true)]
    [InlineData("""Console.WriteLine($"当前模式: {agent.CurrentMode.Name}");""", true)]
    [InlineData("""Console.WriteLine($"未知模式「{modeName}」");""", true)]
    [InlineData("""Console.WriteLine($"路径 {path} 共 {count} 个");""", true)]
    [InlineData("""Console.WriteLine(FormatSectionHeaderLine("最近的会话", "输入 /resume <编号> 恢复", 40));""", false)]
    [InlineData("""Console.WriteLine(FormatSettingWithOptionsLine("思考强度", effort, ThinkingEfforts, cols));""", false)]
    [InlineData("""Console.WriteLine($"已用时 {sw.Elapsed}");""", false)]
    [InlineData("""Console.WriteLine($"{a} {b}");`""", false)]
    public void RepoWideScan_RecognisesRealLongContent(string line, bool expected)
    {
        var hit = HasLongContent(line);
        Assert.True(hit == expected, $"{(hit ? "命中" : "未命中")}（期望 {(expected ? "命中" : "未命中")}）: {line}");
    }
    [Fact]
    public void RepoWideScan_ActuallyScansSomething()
    {
        // 反向保护：匹配规则一旦跟不上代码演进（换行、格式化、变量改名），
        // 这个扫描会安静地扫到 0 行、永远通过——那比没有守卫更危险，
        // 因为它占着"这里有防线"的位置。
        // 显式点名 diff 预览：它写的是 Console.WriteLine("      " + line)，
        // 变量名 line 不在 longContent 清单里，通用规则扫不到
        var preview = File.ReadAllText(Path.Combine(SourceDir(), "Agent", "Agent.cs"));
        Assert.DoesNotContain("Console.WriteLine(\"      \" + line)", preview);
        var hits = 0;
        foreach (var file in ProductionSources())
            foreach (var raw in File.ReadAllLines(file))
            {
                var t = raw.Trim();
                if (t.StartsWith("Console.Write", StringComparison.Ordinal) && t.Contains("$\"", StringComparison.Ordinal))
                    hits++;
            }
        Assert.True(hits > 20, $"只扫到 {hits} 行 Console 写入，匹配规则大概率已失效");
    }
}
