using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

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
    public void StatusBarWidthArithmeticStillHoldsInAsciiMode()
    {
        // 状态栏在 ASCII 与 Unicode 下占的列数应当一致：分隔符同为 3 列、模式标记同为 1 列。
        // 差值不为 0 就说明某个字形的列宽变了，那套「每段扣 3 列」的预算就错了。
        int unicodeWidth;
        using (new GlyphScope(null))
            unicodeWidth = TextUtil.DisplayWidth(Program.BuildStatusBar("plan", "gpt-5", "/repo", "(main)", "10", "20", "ctx 1%", "think 0%", 0));
        using (new GlyphScope("1"))
        {
            var asciiWidth = TextUtil.DisplayWidth(Program.BuildStatusBar("plan", "gpt-5", "/repo", "(main)", "10", "20", "ctx 1%", "think 0%", 0));
            Assert.Equal(unicodeWidth, asciiWidth);
        }
    }
}
