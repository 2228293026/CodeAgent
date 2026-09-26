using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// UI 符号的 ASCII 退回。
/// 回归点：渲染层用制表符（▌ │ ─）和符号（… ⚠ →）。在老式 Windows 控制台、
/// 代码页 437/850 的日志、或某些 CI 采集端里，这些字符会变成 ? 或乱码——
/// 而**框线错位比没有框线更糟**：分隔线变成问号，表格的列对应关系就彻底读不出来。
/// <c>CODEAGENT_ASCII=1</c>（或 TERM=dumb）可显式退回纯 ASCII。
/// </summary>
[Collection("ConsoleOutput")]
public class AsciiGlyphTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-glyph-" + Guid.NewGuid().ToString("N"));

    public AsciiGlyphTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class GlyphScope : IDisposable
    {
        private readonly Func<string, string?> _saved = SafeColor.ReadEnv;
        public GlyphScope(string? ascii, string? term = null) =>
            SafeColor.ReadEnv = name => name switch
            {
                "CODEAGENT_ASCII" => ascii,
                "TERM" => term,
                _ => null,
            };
        public void Dispose() => SafeColor.ReadEnv = _saved;
    }

    [Fact]
    public void EnvironmentSwitchTurnsAsciiOn()
    {
        using var scope = new GlyphScope("1");
        Assert.True(SafeColor.Glyphs.AsciiEnabled);
        Assert.Equal("|", SafeColor.Glyphs.Badge);
        Assert.Equal("|", SafeColor.Glyphs.ColumnSeparator);
        Assert.Equal('-', SafeColor.Glyphs.Rule);
        Assert.Equal("...", SafeColor.Glyphs.Ellipsis);
        Assert.Equal("!", SafeColor.Glyphs.Warn);
    }

    [Fact]
    public void DefaultKeepsUnicode()
    {
        using var scope = new GlyphScope(null);
        Assert.False(SafeColor.Glyphs.AsciiEnabled);
        Assert.Equal("▌", SafeColor.Glyphs.Badge);
        Assert.Equal("│", SafeColor.Glyphs.ColumnSeparator);
        Assert.Equal('─', SafeColor.Glyphs.Rule);
        Assert.Equal("…", SafeColor.Glyphs.Ellipsis);
    }

    [Fact]
    public void DumbTerminalFallsBackToAscii()
    {
        // TERM=dumb 的终端普遍不支持制表符
        using var scope = new GlyphScope(null, "dumb");
        Assert.True(SafeColor.Glyphs.AsciiEnabled);
    }

    [Fact]
    public void UnrecognisedValueFallsBackToAscii()
    {
        // 写了别的值时宁可退回 ASCII，也不要赌它想要 Unicode
        using var scope = new GlyphScope("yes-please");
        Assert.True(SafeColor.Glyphs.AsciiEnabled);
    }

    [Fact]
    public void ExplicitOffKeepsUnicode()
    {
        using var scope = new GlyphScope("0");
        Assert.False(SafeColor.Glyphs.AsciiEnabled);
    }

    [Fact]
    public void AsciiTableKeepsColumnAlignment()
    {
        // 顺序要紧：ColourTestScope 也会替换 SafeColor.ReadEnv 并在 Dispose 时还原，
        // 所以它必须在 GlyphScope **外面**——否则退出颜色作用域时会把符号作用域的
        // ReadEnv 还原掉，符号退回 Unicode。这类"两个作用域共用一个注入点"的坑
        // 只有在真的按这个顺序写出来时才会暴露。
        using var colour = ColourTestScope.Off();
        using var glyphs = new GlyphScope("1");
        var writer = new StringWriter();
        var stdout = Console.Out;
        try
        {
            Console.SetOut(writer);
            var renderer = new ConsoleRenderer(true);
            renderer.SetWidth(60);
            renderer.Append("| aaa | bbbb |\n| --- | --- |\n| 1 | 2 |\n");
            renderer.Flush();
        }
        finally { Console.SetOut(stdout); }
        var lines = ConsoleRendererTests.OutputLines(writer.ToString().Replace("\r\n", "\n")).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(3, lines.Length);
        foreach (var line in lines)
        {
            Assert.DoesNotContain('?', line);
            Assert.DoesNotContain('│', line);
            Assert.Contains('|', line);
        }
    }

    [Fact]
    public void AsciiNoteStillRespectsTheWidthBudget()
    {
        // ASCII 版省略号是 3 个字符而 Unicode 版是 1 个：宽度判定必须按实际渲染算，
        // 否则 ASCII 模式下会溢出终端。
        using (new GlyphScope("1"))
            Assert.True(TextUtil.DisplayWidth(ConsoleRenderer.TableDroppedColsNote(12, 2, 24)) <= 24);
        using (new GlyphScope(null))
            Assert.True(TextUtil.DisplayWidth(ConsoleRenderer.TableDroppedColsNote(12, 2, 24)) <= 24);
    }

    [Fact]
    public void AsciiNoteUsesAsciiArrow()
    {
        using (new GlyphScope("1"))
            Assert.Contains("->", ConsoleRenderer.TableDroppedRowsNote(50, 20, 20));
        using (new GlyphScope(null))
            Assert.Contains("→", ConsoleRenderer.TableDroppedRowsNote(50, 20, 20));
    }

    [Fact]
    public void AsciiBadgeStillFitsOrIsOmitted()
    {
        using var scope = new GlyphScope("1");
        Assert.Equal("  |cs", ConsoleRenderer.FormatCodeLangBadge("cs", 80));
        // 放不下时仍然整行省略，不让它自己折行把代码块顶歪
        Assert.Equal("", ConsoleRenderer.FormatCodeLangBadge("cs", 4));
    }

    [Fact]
    public void FoldHintPrefixMatchesTheLineItMeasures()
    {
        // 关键不变式：量前缀宽度用的那段文本，必须与真正拼出来的那一行**逐字相同**。
        // 此前前缀里写死了 Unicode 符号；一旦前缀也随 ASCII 退回变短，
        // 量出来的列数就会比实际行长多，末行预览被算少甚至整段被丢掉。
        using (new GlyphScope(null))
        {
            var text = InputLine.FoldText("a\nb\nc\nd\ne\nf", 3, 80);
            var hint = ConsoleRendererTests.OutputLines(text).Last(l => l.Trim().Length > 0);
            Assert.StartsWith(InputLine.FoldHintPrefix(6).TrimEnd(), hint);
            Assert.Equal(InputLine.FoldHintPrefix(6), InputLine.FoldHintPrefix(6).TrimEnd() + " ");
        }
        using (new GlyphScope("1"))
        {
            var text = InputLine.FoldText("a\nb\nc\nd\ne\nf", 3, 80);
            var hint = ConsoleRendererTests.OutputLines(text).Last(l => l.Trim().Length > 0);
            Assert.StartsWith(InputLine.FoldHintPrefix(6).TrimEnd(), hint);
        }
    }

    [Fact]
    public void FoldHintIsAsciiWhenAsked()
    {
        using (new GlyphScope("1"))
        {
            var text = InputLine.FoldText("a\nb\nc\nd\ne\nf", 3, 80);
            Assert.DoesNotContain('⏷', text);
            Assert.DoesNotContain('↓', text);
            Assert.DoesNotContain('·', text);
            Assert.Contains("展开", text);
        }
        using (new GlyphScope(null))
            Assert.Contains('⏷', InputLine.FoldText("a\nb\nc\nd\ne\nf", 3, 80));
    }

    [Fact]
    public void NarrowTerminalDropsTheTailInsteadOfWrapping()
    {
        // 窄到放不下末行预览时，正确行为是**整段丢掉末行**（保持折叠块行高不变，
        // 否则菜单锚点会错位），而不是让它折行。断言的是"没有末行预览"，
        // 而不是"整行不超宽"——前缀本身在极窄终端下装不下是既定行为。
        using var scope = new GlyphScope("1");
        foreach (var width in new[] { 20, 24, 28, 32, 80, 120 })
        {
            var text = InputLine.FoldText("a\nb\nc\nd\ne\nf", 3, width);
            foreach (var line in ConsoleRendererTests.OutputLines(text))
            {
                if (line.Trim().Length == 0) continue;
                if (TextUtil.DisplayWidth(line) > width)
                    Assert.DoesNotContain("末行:", line);
            }
        }
    }

    [Fact]
    public void AsciiPrefixIsNoWiderThanUnicode()
    {
        // 前缀宽度直接从「末行预览」的可用列数里扣。ASCII 退回**不能**因此少给几列预览，
        // 所以等宽的 Unicode 符号必须换成 1 列的 ASCII 写法，而不是 `[+]` 那种 3 字符的。
        int unicode;
        using (new GlyphScope(null))
            unicode = InputLine.FoldHintPrefixWidth(42);
        using (new GlyphScope("1"))
        {
            var ascii = InputLine.FoldHintPrefixWidth(42);
            Assert.True(ascii > 0);
            Assert.Equal(unicode, ascii);
        }
    }

    [Fact]
    public void SubstituteReplacesEveryBoxGlyph()
    {
        using var scope = new GlyphScope("1");
        var line = "  a │ b │ c\n  ─────\n  ⚠ 注意 …";
        var ascii = SafeColor.Glyphs.Substitute(line);
        foreach (var glyph in new[] { '│', '─', '⚠', '…' })
            Assert.False(ascii.Contains(glyph), $"还剩 {glyph}: {ascii}");
        Assert.Contains("a | b | c", ascii);
        Assert.Contains("! ", ascii);
    }

    [Fact]
    public void SubstituteIsIdentityWhenUnicodeIsOn()
    {
        using var scope = new GlyphScope(null);
        const string line = "  a │ b";
        Assert.Equal(line, SafeColor.Glyphs.Substitute(line));
    }

    [Fact]
    public void NarrowDotStaysShorterThanTheSegmentSeparator()
    {
        // 极窄回退走的是"连方括号都放不下"的路径，它存在的意义就是**更短**。
        // 曾经误用了 3 列的 SegmentSeparator，把这条路径撑宽了 1 列。
        foreach (var ascii in new[] { null, "1" })
        {
            using var scope = new GlyphScope(ascii);
            Assert.Equal(2, TextUtil.DisplayWidth(SafeColor.Glyphs.NarrowDot));
            Assert.True(TextUtil.DisplayWidth(SafeColor.Glyphs.NarrowDot)
                        < TextUtil.DisplayWidth(SafeColor.Glyphs.SegmentSeparator));
        }
    }

    [Fact]
    public void SegmentSeparatorIsAlwaysThreeColumns()
    {
        // 状态栏的宽度预算按「每段额外扣 3 列」算（Program.PrintStatusBar 的
        // othersWidth）。换了字形若宽度变了，那套扣减就与实际行长对不上，
        // 缩短路径时永远缩不到位——而且这种偏差只在窄屏上显形。
        foreach (var ascii in new[] { null, "1" })
        {
            using var scope = new GlyphScope(ascii);
            Assert.Equal(3, TextUtil.DisplayWidth(SafeColor.Glyphs.SegmentSeparator));
        }
    }

    [Fact]
    public void StatusBarAndNoticeUseTheFallback()
    {
        using var scope = new GlyphScope("1");
        // 状态栏首段用 ASCII 标记与分隔符
        var bar = Program.BuildStatusBar("plan", "gpt-5", "/repo", "(main)", "10", "20", "ctx 1%", "think 0%", 100);
        Assert.Contains('>', bar);
        Assert.DoesNotContain('⏵', bar);
        Assert.Contains(" | ", bar);
        // 通知行用 ASCII 警告标记
        var notice = Program.FormatNoticeLine("工作区外可读写", 60);
        Assert.StartsWith("! ", notice);
        Assert.DoesNotContain('⚠', notice);
    }

    [Fact]
    public void StatusMarkersAreAlwaysOneColumn()
    {
        // 这些标记参与 FormatToolStatusLine / BuildHistoryLine 的**宽度预算扣减**：
        // 宽度一变，耗时或角色名就会被静默切掉，而症状只在窄屏上出现。
        // Unicode 与 ASCII 两套都必须是 1 列。
        foreach (var ascii in new[] { null, "1" })
        {
            using var scope = new GlyphScope(ascii);
            foreach (var value in new[]
            {
                SafeColor.Glyphs.Warn, SafeColor.Glyphs.Ok, SafeColor.Glyphs.Tool,
                SafeColor.Glyphs.Error, SafeColor.Glyphs.Hit, SafeColor.Glyphs.Fold,
                SafeColor.Glyphs.Unfold, SafeColor.Glyphs.StatusMark,
            })
                Assert.Equal(1, TextUtil.DisplayWidth(value));
        }
    }

    [Fact]
    public void ToolStatusLineUsesTheFallbackAndKeepsItsWidth()
    {
        var elapsed = TimeSpan.FromMilliseconds(1234);
        string asciiLine;
        using (new GlyphScope("1"))
        {
            asciiLine = AgentClass.FormatToolStatusLine("read_file", false, elapsed, 80);
            Assert.StartsWith("  v ", asciiLine);
            Assert.DoesNotContain('✔', asciiLine);
        }
        using (new GlyphScope(null))
        {
            var unicode = AgentClass.FormatToolStatusLine("read_file", false, elapsed, 80);
            Assert.StartsWith("  ✔ ", unicode);
            // 两种模式必须占同样多的列，否则窄屏下的截断行为会分叉
            Assert.Equal(TextUtil.DisplayWidth(unicode), TextUtil.DisplayWidth(asciiLine));
        }
    }

    [Fact]
    public void ToolStatusLineKeepsTheDurationInBothModes()
    {
        // 标记换成 ASCII 后不得把耗时截掉——这正是 1 列不变式要保住的行为
        foreach (var ascii in new[] { null, "1" })
        {
            using var scope = new GlyphScope(ascii);
            var line = AgentClass.FormatToolStatusLine("a_very_long_tool_name_for_truncation", false, TimeSpan.FromSeconds(42), 40);
            Assert.Contains("(42", line);
        }
    }

    [Fact]
    public void EllipsisWidthIsReportedNotAssumed()
    {
        // 省略号在 ASCII 下是 3 列。任何"给省略号预留 N 列"的预算都应读这里。
        using (new GlyphScope(null))
            Assert.Equal(1, SafeColor.Glyphs.EllipsisWidth);
        using (new GlyphScope("1"))
            Assert.Equal(3, SafeColor.Glyphs.EllipsisWidth);
    }

    [Fact]
    public void TruncationNeverExceedsItsBudgetInEitherMode()
    {
        // 核心不变式：ASCII 退回把省略号从 1 列变成 3 列，而 FitToWidth 此前**写死**
        // 只预留 1 列，于是每一条截断结果都超宽 2 列。这类偏差只在窄屏显形。
        const string cjk = "代码代理正在把这段很长的中文路径名字压缩到可读宽度以内去";
        const string ascii = "a_very_long_unbroken_identifier_that_cannot_be_break_nicely_at_all";
        foreach (var asciiMode in new[] { false, true })
        {
            using var scope = new GlyphScope(asciiMode ? "1" : null);
            foreach (var source in new[] { cjk, ascii, "", "短" })
                foreach (var width in new[] { 1, 2, 3, 4, 5, 8, 12, 20, 40 })
                {
                    var fitted = InputLine.FitToWidth(source, width);
                    Assert.True(TextUtil.DisplayWidth(fitted) <= width,
                        $"ascii={asciiMode} width={width} 得到 {TextUtil.DisplayWidth(fitted)} 列: {fitted}");
                }
        }
    }

    [Fact]
    public void PromptNeverExceedsItsBudgetInEitherMode()
    {
        // 提示符超宽会把整行输入顶到折行，比截断难看得多。这条在所有宽度上都必须成立。
        foreach (var asciiMode in new[] { false, true })
        {
            using var scope = new GlyphScope(asciiMode ? "1" : null);
            foreach (var width in new[] { 8, 10, 12, 16, 20, 30, 50, 80 })
            {
                var prompt = Program.BuildPromptText("执行模式", "gpt-5-codex", "D:\\Projects\\SomeVeryLongProjectName", width);
                var budget = width - Program.PromptFitMargin >= 4 ? width - Program.PromptFitMargin : width;
                Assert.True(TextUtil.DisplayWidth(prompt) <= budget,
                    $"ascii={asciiMode} width={width}: {TextUtil.DisplayWidth(prompt)} > {budget}");
            }
        }
    }

    [Fact]
    public void PromptKeepsTheModeBracketOnRealisticWidths()
    {
        // 「模式永不丢弃」：现实宽度下必须保住方括号对。
        // 极窄（预算不足 5 列）时只保证不超宽——那时连 "[…]> " 都放不下，
        // 硬塞方括号就会超宽，而超宽会把整行输入顶到折行。
        foreach (var asciiMode in new[] { false, true })
        {
            using var scope = new GlyphScope(asciiMode ? "1" : null);
            foreach (var width in new[] { 20, 30, 50, 80 })
                Assert.Contains(']', Program.BuildPromptText("执行模式", "gpt-5-codex", "D:\\Projects\\SomeVeryLongProjectName", width));
        }
    }

    [Fact]
    public void PromptOverheadIsMeasuredNotHardcoded()
    {
        // 源码级守卫：BuildPromptText 里不得再出现省略号相关的写死常数。
        // 曾经的 `- 4` 和 `- 6` 在 ASCII 退回（1 列 → 3 列）后会让提示符超预算。
        var source = File.ReadAllText(FindSource("Program.cs"));
        var at = source.IndexOf("internal static string BuildPromptText", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到 BuildPromptText");
        var body = source[at..];
        body = body[..body.IndexOf("PromptFitMargin = 8", StringComparison.Ordinal)];
        Assert.DoesNotContain("Glyphs.Ellipsis + \"> \"".Replace("Glyphs.Ellipsis", "…"), body);
        Assert.Contains("Glyphs.Ellipsis", body);
    }

    [Fact]
    public void ShortenPathNeverExceedsItsBudgetInEitherMode()
    {
        const string path = @"D:\Projects\CodeAgent\src\CodeAgent\Tools\SomeVeryLongToolName.cs";
        foreach (var ascii in new[] { null, "1" })
        {
            using var scope = new GlyphScope(ascii);
            for (var budget = 1; budget <= path.Length + 4; budget++)
            {
                var shortened = Program.ShortenPath(path, budget);
                Assert.True(TextUtil.DisplayWidth(shortened) <= budget,
                    $"ascii={ascii} budget={budget} 得到 {TextUtil.DisplayWidth(shortened)} 列: {shortened}");
            }
        }
    }

    [Fact]
    public void ShortenSummaryNeverExceedsItsBudgetInEitherMode()
    {
        const string summary = "read_file(path: string, encoding: string, max_lines: int, start_line: int)";
        foreach (var ascii in new[] { null, "1" })
        {
            using var scope = new GlyphScope(ascii);
            for (var budget = 4; budget <= summary.Length; budget++)
            {
                var line = AgentClass.FormatToolStatusLine(summary, false, TimeSpan.Zero, budget);
                Assert.True(TextUtil.DisplayWidth(line) <= budget,
                    $"ascii={ascii} budget={budget} 得到 {TextUtil.DisplayWidth(line)} 列: {line}");
            }
        }
    }

    [Fact]
    public void ArgsPlaceholderIsNeverHardcodedAsThreeColumns()
    {
        // 源码级守卫：ShortenSummaryAsWhole 里不得再出现写死的 `budget - 3`。
        // `(…)` 是 3 列而 ASCII 的 `(...)` 是 4 列，写死 3 会让工具状态行超预算 1 列。
        var source = File.ReadAllText(FindSource(Path.Combine("Agent", "Agent.cs")));
        var at = source.IndexOf("ShortenSummaryAsWhole(string summary, int budget)", StringComparison.Ordinal);
        Assert.True(at >= 0, "找不到 ShortenSummaryAsWhole");
        var body = source[at..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.DoesNotContain("budget - 3", body);
        Assert.Contains("Glyphs.ArgsPlaceholder", body);
    }

    [Fact]
    public void SpinnerFramesAreOneColumnAndCycle()
    {
        // spinner 的清行按 _spinnerLastWidth 的**实测**显示宽度走；一旦某帧宽度不同，
        // 清行就会留下残字（上一帧比这一帧宽时）或删掉后面的内容。
        foreach (var ascii in new[] { null, "1" })
        {
            using var scope = new GlyphScope(ascii);
            var seen = new List<string>();
            for (var tick = 0; tick < 12; tick++)
            {
                var frame = SafeColor.Glyphs.SpinnerFrame(tick);
                Assert.Equal(1, TextUtil.DisplayWidth(frame));
                if (seen.Count == 0 || seen[^1] != frame) seen.Add(frame);
            }
            Assert.True(seen.Count >= 3, $"spinner 应当有多帧可轮换，实际 {seen.Count} 帧");
        }
    }

    [Fact]
    public void SpinnerIsPureAsciiInAsciiMode()
    {
        using (new GlyphScope("1"))
        {
            // 盲文点字（⠦⠸⠼…）在代码页 437/850 的终端里是乱码
            for (var tick = 0; tick < 8; tick++)
                Assert.All(SafeColor.Glyphs.SpinnerFrame(tick), c => Assert.True(c < 128, $"非 ASCII 字符: {c}"));
        }
        using (new GlyphScope(null))
            Assert.True(SafeColor.Glyphs.SpinnerFrame(0) != SafeColor.Glyphs.SpinnerFrame(1));
    }

    [Fact]
    public void WaitGlyphIsOneColumnInUnicode()
    {
        // 回归点：此前用 ⏳，它是 emoji、算 3 列，和 Round 94 的 🔧 同一类问题——
        // 凭空多吃两列，后面跟着的状态文字先被挤出屏幕。Unicode 形态必须是 1 列。
        using (new GlyphScope(null))
            Assert.Equal(1, TextUtil.DisplayWidth(SafeColor.Glyphs.Wait));
        // ASCII 形态跟随省略号（3 列）——这是既定设计，只需确认两者一致、不各算各的
        using (new GlyphScope("1"))
        {
            Assert.Equal(SafeColor.Glyphs.Ellipsis, SafeColor.Glyphs.Wait);
            Assert.Equal(SafeColor.Glyphs.EllipsisWidth, TextUtil.DisplayWidth(SafeColor.Glyphs.Wait));
        }
    }

    [Fact]
    public void SetupWizardUsesTheFallbackGlyphs()
    {
        // 源码级守卫：配置向导不得再直接写死 ✔/⚠/⏳
        var code = StripComments(File.ReadAllText(FindSource("SetupWizard.cs")));
        foreach (var glyph in new[] { "✔", "⚠", "⏳" })
            Assert.DoesNotContain(glyph, code);
        Assert.Contains("Glyphs.Ok", code);
        Assert.Contains("Glyphs.Warn", code);
        Assert.Contains("Glyphs.Wait", code);
    }

    [Fact]
    public void BrailleSpinnerTableLivesOnlyInGlyphs()
    {
        // 盲文点字表必须收口到 Glyphs 一处，否则 ASCII 退回永远覆盖不到
        Assert.DoesNotContain("⠦", StripComments(File.ReadAllText(FindSource(Path.Combine("Agent", "Agent.cs")))));
        Assert.Contains("⠦", StripComments(File.ReadAllText(FindSource("Util.cs"))));
    }

    private static string StripComments(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"(?s)/\*.*?\*/|//[^\n]*", "");

    private static string FindSource(string name)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent", name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1000)
                    return candidate;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException($"找不到 src/CodeAgent/{name}");
    }
}
