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
}
