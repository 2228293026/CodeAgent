using System;
using System.IO;
using CodeAgent;
using Xunit;
using static CodeAgent.ConsoleRenderer;

namespace CodeAgent.Tests;

/// <summary>ConsoleRenderer 行内样式解析与渲染的补充边界测试(在 ConsoleRendererTests 45 个之上)。</summary>
public class ConsoleRendererEdgeTests : IDisposable
{
    private readonly StringWriter _out = new();
    private readonly TextWriter _originalOut;

    public ConsoleRendererEdgeTests()
    {
        _originalOut = Console.Out;
        Console.SetOut(_out);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _out.Dispose();
    }

    private string Render(string text)
    {
        var r = new ConsoleRenderer(enabled: true);
        r.Append(text);
        r.Flush();
        return _out.ToString();
    }

    // ===== ParseInline 行内样式解析 =====

    [Fact]
    public void ParseInline_EmptyString_ReturnsEmpty() =>
        Assert.Empty(ParseInline(""));

    [Fact]
    public void ParseInline_PlainText_SingleNormalSpan()
    {
        var parts = ParseInline("hello world");
        Assert.Single(parts);
        Assert.Equal(("hello world", InlineStyleToken.Normal), parts[0]);
    }

    [Fact]
    public void ParseInline_CodeSpan_StyledAsCode()
    {
        var parts = ParseInline("`code`");
        Assert.Single(parts);
        Assert.Equal(("code", InlineStyleToken.Code), parts[0]);
    }

    [Fact]
    public void ParseInline_UnclosedCodeSpan_ConsumesBacktick()
    {
        // 单个 ` 未闭合：反引号被消费，后续文本以 Code 样式输出（防乱码，文本不丢）
        var parts = ParseInline("a`b");
        Assert.Equal(2, parts.Count);
        Assert.Equal(("a", InlineStyleToken.Normal), parts[0]);
        Assert.Equal(("b", InlineStyleToken.Code), parts[1]);
    }

    [Fact]
    public void ParseInline_BoldSpan_StyledAsBold()
    {
        var parts = ParseInline("**bold**");
        Assert.Single(parts);
        Assert.Equal(("bold", InlineStyleToken.Bold), parts[0]);
    }

    [Fact]
    public void ParseInline_BoldThenCode_AlternatesStyles()
    {
        // **b** `c` **d**：Bold → Normal → Code → Normal → Bold（5 段，分隔空格各成一段）
        var parts = ParseInline("**b** `c` **d**");
        Assert.Equal(5, parts.Count);
        Assert.Equal(("b", InlineStyleToken.Bold), parts[0]);
        Assert.Equal(("c", InlineStyleToken.Code), parts[2]);
        Assert.Equal(("d", InlineStyleToken.Bold), parts[4]);
    }

    [Fact]
    public void ParseInline_TripleBackticks_TreatedAsLiteral()
    {
        // 3+ 连续反引号是围栏语法，行内解析按字面保留（不当作代码开关）
        var parts = ParseInline("a```b");
        Assert.Single(parts);
        Assert.Contains("```", parts[0].text);
        Assert.Equal(InlineStyleToken.Normal, parts[0].style);
    }

    [Fact]
    public void ParseInline_AsterisksInsideCode_AreLiteral()
    {
        // 行内代码内的 ** 保留为字面，不作加粗开关
        var parts = ParseInline("`a**b`");
        Assert.Single(parts);
        Assert.Equal(("a**b", InlineStyleToken.Code), parts[0]);
    }

    [Fact]
    public void ParseInline_TripleAsterisks_AreLiteral()
    {
        // *** 分隔符按字面保留（不拆成加粗开关 + 单星号）
        var parts = ParseInline("a***b");
        Assert.Single(parts);
        Assert.Contains("***", parts[0].text);
    }

    [Fact]
    public void ParseInline_CodeSpanRestoresBold()
    {
        // 加粗中的行内代码退出后恢复 Bold（嵌套样式）
        var parts = ParseInline("**b `c` d**");
        Assert.Equal(3, parts.Count);
        Assert.Equal(("b ", InlineStyleToken.Bold), parts[0]);
        Assert.Equal(("c", InlineStyleToken.Code), parts[1]);
        Assert.Equal((" d", InlineStyleToken.Bold), parts[2]);
    }

    // ===== 渲染边界 =====

    [Fact]
    public void Render_TableThenPlainText_FlushesTableFirst()
    {
        var output = Render("| A | B |\n|---|---|\n| 1 | 2 |\n\n后文");
        Assert.Contains("A", output);
        Assert.Contains("B", output);
        Assert.Contains("后文", output);
        Assert.DoesNotContain("|", output); // 竖线不残留（表格已渲染为对齐列）
    }

    [Fact]
    public void Render_UnclosedTableSeparator_DoesNotCrash()
    {
        // 只有分隔行没有数据行：不崩溃（现有回归的补充）
        var output = Render("|---|---|\n");
        Assert.Contains("─", output);
    }

    [Fact]
    public void Render_Disabled_RawText()
    {
        var r = new ConsoleRenderer(enabled: false);
        r.Append("**bold** `code`");
        r.Flush();
        Assert.Contains("**bold** `code`", _out.ToString()); // 关闭渲染时原样输出
    }

    [Fact]
    public void Render_CRLFInput_NoStrayCarriageReturnInOutput()
    {
        // 回归：ConsoleRenderer 的 HandleTextChar 曾直接把 \r 追加进缓冲，
        // 导致 \r\n 输入在非代码文本里残留 \r，终端会把文本送回行首造成覆盖渲染。
        var output = Render("line1\r\nline2\r\n");
        Assert.DoesNotContain("\r", output);
        Assert.Contains("line1", output);
        Assert.Contains("line2", output);
    }

    [Fact]
    public void Append_CodeFenceSplitAcrossAppend_DoesNotLoseCode()
    {
        // 回归：代码围栏语言标识符跨 Append 调用时，_skipCodeIntro 未在 Flush 重置，
        // 且流式 chunk 边界落在标识符后时下一段代码内容被静默丢弃。
        var r = new ConsoleRenderer(enabled: true);
        r.Append(" ```cs");   // 触发 _skipCodeIntro = true，丢弃 cs
        r.Flush();            // Flush 应重置 _skipCodeIntro
        r.Append("code");     // 代码内容应进入 _code 而非被丢弃
        r.Append("\n```");    // 关闭围栏
        r.Flush();
        var output = _out.ToString();
        Assert.Contains("code", output);
    }
}
