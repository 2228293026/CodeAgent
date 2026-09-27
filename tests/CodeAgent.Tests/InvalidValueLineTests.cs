using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 「无效取值」提示的统一格式。
///
/// 此前同一个意思有四种写法：<c>无效权限模式: x(可选 a | b | c)</c>（半角括号——
/// 中文句子里夹半角括号本身就是排版错误）、<c>无效值: x（可选: a / b / c）</c>、
/// <c>无效值: x（可选 a / b / c）</c>、<c>没有模式「x」，可用: a, b, c</c>。
/// 用户不该在四个命令里看到四种标点风格。
/// </summary>
public sealed class InvalidValueLineTests
{
    [Fact]
    public void Line_UsesFullWidthParentheses()
    {
        // 中文句子里的半角括号是排版错误
        var line = Program.FormatInvalidValueLine("无效权限模式", "bogus", ["strict", "full"]);
        Assert.Contains("（可选: strict / full）", line);
        Assert.DoesNotContain("(可选", line);
    }

    [Fact]
    public void Line_KeepsTheGivenValue()
    {
        Assert.Contains("bogus", Program.FormatInvalidValueLine("无效值", "bogus", ["a", "b"]));
    }

    [Fact]
    public void Line_UsesSlashSeparator()
    {
        var line = Program.FormatInvalidValueLine("无效值", "x", ["a", "b", "c"]);
        Assert.Contains("a / b / c", line);
        Assert.DoesNotContain(" | ", line);
        Assert.DoesNotContain(", ", line);
    }

    [Fact]
    public void Line_HasAColonAfterKexuan()
    {
        // 三种历史写法在「可选」后有的有冒号有的没有
        foreach (var label in new[] { "无效值", "无效权限模式", "没有模式" })
            Assert.Contains("可选: ", Program.FormatInvalidValueLine(label, "x", ["a"]));
    }

    [Fact]
    public void Line_WithoutOptions_DropsTheParenthetical()
    {
        Assert.Equal("无效值: bogus", Program.FormatInvalidValueLine("无效值", "bogus", []));
    }

    [Fact]
    public void Line_EmptyOptionList_DoesNotProduceEmptyParens()
    {
        var line = Program.FormatInvalidValueLine("无效值", "x", []);
        Assert.DoesNotContain("（）", line);
    }

    [Fact]
    public void Line_IsBoundedWhenGivenAWidth()
    {
        var many = Enumerable.Range(1, 60).Select(i => "option-with-a-long-name-" + i);
        var line = Program.FormatInvalidValueLine("无效值", new string('x', 200), many, 40);
        Assert.True(TextUtil.DisplayWidth(line) <= 40, $"超宽 {TextUtil.DisplayWidth(line)}");
    }

    [Fact]
    public void Line_UnknownWidthIsUntrimmed()
    {
        // width<=0 = 宽度未知，不做裁剪（这条语义反复被栽）
        var line = Program.FormatInvalidValueLine("无效值", new string('x', 300), ["a"], 0);
        Assert.Equal($"无效值: {new string('x', 300)}（可选: a）", line);
    }

    [Fact]
    public void NoCallSiteFormatsItByHand()
    {
        // 手写格式的调用点是这个问题的根源：四种写法就是四个调用点各写各的。
        // 留一个结构性守卫，避免将来又长出第五种。
        var src = File.ReadAllText(Find());
        foreach (var handRolled in new[]
        {
            "无效值: {rest}（可选",
            "无效权限模式: {rest}(可选",
            "没有模式「{wanted}」",
        })
            Assert.DoesNotContain(handRolled, src);
        Assert.True(Count(src, "FormatInvalidValueLine(") >= 5, "四个调用点 + 定义");
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (string.CompareOrdinal(haystack, i, needle, 0, needle.Length) == 0)
                n++;
        return n;
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
