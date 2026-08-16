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
