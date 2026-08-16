using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

public class ConsoleRendererTests : IDisposable
{
    private readonly StringWriter _out = new();
    private readonly TextWriter _originalOut;

    public ConsoleRendererTests()
    {
        _originalOut = Console.Out;
        Console.SetOut(_out); // 捕获渲染输出
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _out.Dispose();
    }

    private string Render(string text)
    {
        var r = new CodeAgent.ConsoleRenderer(enabled: true);
        r.Append(text);
        r.Flush();
        return _out.ToString();
    }

    [Fact]
    public void Append_PlainText_IsEmitted()
    {
        var output = Render("hello world");
        Assert.Contains("hello world", output);
    }

    [Fact]
    public void Append_CrlfLineEndings_DoNotLeakCarriageReturns()
    {
        // 回归：Windows 换行（\r\n）的行尾 \r 曾被原样输出，终端会把它当回车跳回行首覆盖本行
        var output = Render("第一行\r\n第二行\r\n");
        Assert.Contains("第一行", output);
        Assert.Contains("第二行", output);
        Assert.DoesNotContain("\r", output); // \r 不应残留
        Assert.Equal(2, output.TrimEnd('\n').Split('\n').Length); // 两行都完整
    }

    [Fact]
    public void Append_LastLineWithCrNoNewline_IsTrimmed()
    {
        // 流末尾的行若以 \r 结束（无 \n），也不应残留回车
        var r = new CodeAgent.ConsoleRenderer(enabled: true);
        r.Append("tail content\r");
        r.Flush();
        Assert.Contains("tail content", _out.ToString());
        Assert.DoesNotContain("\r", _out.ToString());
    }

    [Fact]
    public void Append_CodeFence_EmitsCodeContent()
    {
        var output = Render("```\nint x = 1;\n```");
        Assert.Contains("int x = 1;", output);
    }

    [Fact]
    public void Append_CodeFenceWithLanguageTag_StripsTag()
    {
        // 回归：```cs 的语言标注曾混入代码内容（渲染出 "cs\nint x = 1;"），现应被丢弃
        var output = Render("```cs\nint x = 1;\n```");
        Assert.Contains("int x = 1;", output);
        Assert.DoesNotContain("cs\nint", output); // 语言标注不应出现在代码内容前
    }

    [Fact]
    public void Append_TripleBackticksMidLine_AreLiteralNotFence()
    {
        // 回归：行中的 ``` 曾误判为代码围栏，_skipCodeIntro 吞掉本行剩余内容；
        // 围栏必须位于行首（可含前导空白），行中应作为字面文本保留
        var output = Render("use ```literal``` here");
        Assert.Contains("use ```literal``` here", output); // 全部文本保留
    }

    [Fact]
    public void Append_IndentedCodeFence_StillOpens()
    {
        // 前导空白 + ``` 仍是合法围栏（Markdown 允许缩进围栏）
        var output = Render("  ```\ncode\n```");
        Assert.Contains("code", output);
        Assert.DoesNotContain("  ```\ncode", output); // 围栏行本身不作为文本输出
    }

    [Fact]
    public void Append_InlineCode_EmitsContent()
    {
        var output = Render("run `dotnet build` now");
        Assert.Contains("dotnet build", output);
    }

    [Fact]
    public void Append_Table_EmitsAllCells()
    {
        var output = Render("| name | size |\n|------|------|\n| a.cs | 1 KB |\n");
        Assert.Contains("name", output);
        Assert.Contains("size", output);
        Assert.Contains("a.cs", output);
        Assert.Contains("1 KB", output);
    }

    [Fact]
    public void Append_Bold_EmitsText()
    {
        var output = Render("**important** note");
        Assert.Contains("important", output);
        Assert.Contains("note", output);
    }

    [Fact]
    public void Append_UnclosedBold_StillEmitsText()
    {
        // 回归：未闭合的 ** 不应吞掉后续文本
        var output = Render("**unclosed bold text");
        Assert.Contains("unclosed bold text", output);
    }

    [Fact]
    public void Append_UnclosedInlineCode_StillEmitsText()
    {
        // 回归：未闭合的行内代码反引号不应吞掉后续文本
        var output = Render("use `dotnet build please");
        Assert.Contains("dotnet build please", output);
    }

    [Fact]
    public void Append_BoldWithInlineCode_NestedStylesWork()
    {
        // 粗体中嵌套行内代码：两种样式都应输出文本
        var output = Render("**run `test` now**");
        Assert.Contains("run", output);
        Assert.Contains("test", output);
        Assert.Contains("now", output);
    }

    [Fact]
    public void Append_TripleAsterisks_AreLiteralNotBoldToggle()
    {
        // 回归：*** 曾被解析成 ** 加粗开关 + 单个 *，渲染时丢失两个星号
        var output = Render("a *** b");
        Assert.Contains("a *** b", output); // 三个星号完整保留
    }

    [Fact]
    public void Append_FourAsterisks_AreLiteralNotBoldToggle()
    {
        // 连续 4 个星号同样应按字面保留
        var output = Render("x **** y");
        Assert.Contains("x **** y", output);
    }

    [Fact]
    public void Append_DoubleAsterisks_StillToggleBold()
    {
        // 修复不应破坏 **bold** 的加粗语义
        var output = Render("**bold** text");
        Assert.Contains("bold", output);
        Assert.Contains("text", output);
    }

    [Fact]
    public void Append_DoubleAsterisksInsideInlineCode_AreLiteral()
    {
        // 回归：`a**b` 中 ** 曾被当作加粗开关消费，渲染后星号丢失（输出 "ab"）；
        // 行内代码内容应逐字保留
        var output = Render("run `a**b` now");
        Assert.Contains("a**b", output); // 星号完整保留
        Assert.Contains("run", output);
        Assert.Contains("now", output);
    }

    [Fact]
    public void Append_BoldWithInlineCode_KeepBackticks()
    {
        // 粗体内嵌套行内代码：反引号内容与星号都应保留
        var output = Render("**`x**y`**");
        Assert.Contains("x**y", output);
    }

    [Fact]
    public void Disabled_OutputsRawText()
    {
        var r = new CodeAgent.ConsoleRenderer(enabled: false);
        r.Append("plain **markdown** `code`");
        r.Flush();
        Assert.Contains("plain **markdown** `code`", _out.ToString()); // 禁用时原样输出
    }

    [Fact]
    public void Table_CjkCells_AreWidthAligned()
    {
        // 回归：中文按 2 列宽对齐，短单元格应补齐空格使竖线对齐
        var output = Render("| 名称 | size |\n|------|------|\n| 中文 | 10 |\n");
        var line3 = output.Split('\n')[2]; // 数据行
        // 2 列表格只有 1 条竖线分隔符；"中文"(宽4) 与 "10" 需按列宽补齐
        var pipe = line3.IndexOf("│", StringComparison.Ordinal);
        Assert.True(pipe > 0, $"数据行应有竖线: {line3}");
        Assert.False(line3[(pipe + 1)..].Contains("│"), $"2 列表格不应有第二条竖线: {line3}");
        // 表头、分隔行、数据行的竖线列应对齐
        var headPipe = output.Split('\n')[0].IndexOf("│", StringComparison.Ordinal);
        var sepPipe = output.Split('\n')[1].IndexOf("│", StringComparison.Ordinal);
        Assert.True(Math.Abs(headPipe - pipe) <= 2, $"竖线列应对齐（表头 {headPipe} vs 数据 {pipe}）");
        Assert.True(Math.Abs(sepPipe - pipe) <= 2, $"竖线列应对齐（分隔 {sepPipe} vs 数据 {pipe}）");
    }

    [Fact]
    public void UnclosedCodeFence_IsFlushedAtEnd()
    {
        // 回归：未闭合的代码围栏在 Flush 时不应丢失内容
        var r = new CodeAgent.ConsoleRenderer(enabled: true);
        r.Append("```\nint x = 1;");
        r.Flush();
        Assert.Contains("int x = 1;", _out.ToString());
    }

    [Fact]
    public void Table_SeparatorRow_IsNotTreatedAsData()
    {
        // 回归：|---| 分隔行应渲染为横线而非普通内容
        var output = Render("| a | b |\n|---|---|\n| 1 | 2 |\n");
        Assert.Contains("─", output); // 分隔行渲染为横线
        Assert.DoesNotContain("|---|", output); // 原始分隔行不出现
    }

    [Fact]
    public void Table_SingleCharDataCells_AreNotSeparators()
    {
        // 回归：单字符数据行（如 - 或 :）曾被宽松判定误判为分隔行而丢失，
        // 现应作为数据单元格正常渲染（分隔行需至少 3 个 -）
        var output = Render("| a | b |\n|---|---|\n| - | : |\n");
        Assert.Contains("-", output); // 数据 - 保留
        Assert.Contains(":", output); // 数据 : 保留
        Assert.Contains("│", output); // 数据行仍有分隔竖线（非横线）
        var dataLine = output.TrimEnd('\n').Split('\n')[^1];
        Assert.DoesNotContain("──", dataLine); // 数据行不应渲染成横线
    }

    [Fact]
    public void Table_ColonAlignmentSeparators_StillRecognized()
    {
        // Markdown 对齐分隔行（:---、---:、:---:）仍应渲染为横线
        var output = Render("| a | b |\n|:---|:---:|\n| 1 | 2 |\n");
        Assert.Contains("─", output); // 对齐分隔行渲染为横线
    }

    [Fact]
    public void Table_TrailingRowWithoutNewline_IsStillAligned()
    {
        // 回归：流以未换行的表格行结束时，该行应仍按表格对齐输出，
        // 而非作为普通文本带原始 | 输出
        var r = new CodeAgent.ConsoleRenderer(enabled: true);
        r.Append("| name | size |\n|------|------|\n| a.cs | 10 |");
        r.Flush();
        var output = _out.ToString();

        Assert.Contains("│", output); // 表格对齐竖线
        Assert.DoesNotContain("| a.cs | 10 |", output); // 原始未对齐形式不出现
        var lastLine = output.TrimEnd('\n').Split('\n')[^1];
        Assert.StartsWith("  ", lastLine); // 表格行有缩进前缀
        Assert.Contains("a.cs", lastLine);
        Assert.Contains("10", lastLine);
    }

    [Fact]
    public void Table_VeryLargeTable_IsTruncatedWithNotice()
    {
        // 回归：超大表格曾全部渲染撑爆终端；现应截断并提示总行数
        var sb = new System.Text.StringBuilder();
        sb.Append("| n |\n|---|\n");
        for (int i = 1; i <= 120; i++)
            sb.Append($"| {i} |\n");
        var output = Render(sb.ToString());

        Assert.Contains("仅显示前 50 行", output);
        Assert.Contains("共 122 行", output); // 表头 + 分隔行 + 120 数据行
        Assert.DoesNotContain("| 120 |", output); // 末尾行被截断
    }
}
