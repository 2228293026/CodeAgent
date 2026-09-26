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
}
