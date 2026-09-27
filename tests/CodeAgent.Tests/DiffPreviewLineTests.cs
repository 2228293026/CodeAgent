using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 工具执行前的 diff 预览行渲染。
///
/// 此前是 <c>Console.WriteLine("      " + line)</c>：diff 行的长度不受控
/// （被新增/删除的**一整行代码**都可能很长）。窄终端上硬折行之后这一行就废了——
/// 续行没有 <c>+/-</c> 前缀，看起来像另一条独立的改动，看不出是删掉还是加上的。
/// </summary>
public sealed class DiffPreviewLineTests
{
    [Fact]
    public void Line_KeepsTheIndentAndTheSign()
    {
        var out1 = Program.FormatDiffPreviewLine("+var x = 1;", 80);
        Assert.StartsWith("      ", out1);
        Assert.Contains("+var x = 1;", out1);
    }

    [Fact]
    public void Line_DeletionSignSurvivesTruncation()
    {
        // 最要紧的：被截断的行仍要看得出是 + 还是 -
        foreach (var w in new[] { 8, 12, 20, 40 })
        {
            var plus = Program.FormatDiffPreviewLine("+" + new string('a', 300), w);
            var minus = Program.FormatDiffPreviewLine("-" + new string('a', 300), w);
            Assert.Contains("+", plus);
            Assert.Contains("-", minus);
            Assert.NotEqual(plus, minus);
        }
    }

    [Fact]
    public void Line_NeverExceedsWidth()
    {
        var lines = new[]
        {
            "+" + new string('a', 500),
            "-" + new string('中', 500),
            "@@ -12,7 +12,9 @@ namespace Foo.Bar {",
            "+++ b/src/CodeAgent/Agent/Agent.cs",
            " " + new string('x', 300),
        };
        for (var w = 1; w <= 200; w++)
        {
            var out1 = Program.FormatDiffPreviewLine(lines[w % lines.Length], w);
            Assert.True(TextUtil.DisplayWidth(out1) <= w, $"w={w} 超宽 {TextUtil.DisplayWidth(out1)}");
        }
    }

    [Fact]
    public void Line_TruncationCarriesAnEllipsis()
    {
        var out1 = Program.FormatDiffPreviewLine("+" + new string('a', 500), 30);
        Assert.Contains(SafeColor.Glyphs.Ellipsis, out1);
    }

    [Fact]
    public void Line_HunkHeaderKeepsItsLineNumbers()
    {
        // @@ 头尾的上下文标题（C# 里常是很长的方法签名）是装饰性的，
        // 放不下时整段丢掉——**行号范围才是 hunk 的结构信息**
        var out1 = Program.FormatDiffPreviewLine("@@ -12,7 +12,9 @@ public static string VeryLongMethodName(int a)", 40);
        Assert.Contains("@@", out1);
        Assert.Contains("-12,7", out1);
        Assert.Contains("+12,9", out1);
    }

    [Fact]
    public void Line_PathHeaderKeepsTheFileName()
    {
        // 硬截尾部（a/src/CodeAgent/Agent…）会让人以为是另一个文件；
        // 路径最有信息量的是末段文件名
        var out1 = Program.FormatDiffPreviewLine("--- a/src/CodeAgent/Agent/Agent.cs", 40);
        Assert.Contains("Agent.cs", out1);
    }

    [Fact]
    public void Line_AgreesWithSlashDiffOnTheSameLine()
    {
        // 两处 diff 渲染规则若各写一份，窄终端下就会给出两种答案。
        // 预览多六格缩进，所以预算比 /diff 少六——契约是"同一条规则、减去缩进后的预算"，
        // 而不是字面相等（那不可能成立）。
        foreach (var line in new[] { "+" + new string('a', 200), "--- a/src/CodeAgent/Agent/Agent.cs", "@@ -1,2 +1,3 @@ ctx" })
            foreach (var w in new[] { 20, 30, 40, 60 })
                Assert.Equal(
                    "      " + Program.FormatDiffLine(line, w - 6),
                    Program.FormatDiffPreviewLine(line, w));
    }

    [Fact]
    public void Line_ShortLinesAreUntouched()
    {
        Assert.Equal("      @@ -1 +1 @@", Program.FormatDiffPreviewLine("@@ -1 +1 @@", 80));
    }

    [Fact]
    public void Line_UnknownWidthShowsEverything()
    {
        // width<=0 = 宽度未知，此时不收口（这条语义反复被栽）
        var body = "+" + new string('a', 300);
        Assert.Equal("      " + body, Program.FormatDiffPreviewLine(body, 0));
    }

    [Fact]
    public void Line_StaysReadableAtAbsurdlyNarrowWidths()
    {
        foreach (var w in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var out1 = Program.FormatDiffPreviewLine("+" + new string('a', 100), w);
            Assert.NotNull(out1);
            Assert.True(TextUtil.DisplayWidth(out1) <= w);
        }
    }

    [Fact]
    public void ShowFilePreview_UsesTheBoundedRenderer()
    {
        // 只测纯函数会漏掉"预览处根本没调它"——折行 bug 原样存在
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("FormatDiffPreviewLine(", src);
        Assert.DoesNotContain("Console.WriteLine(\"      \" + line)", src);
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Agent", "Agent.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Agent/Agent.cs");
    }
}
