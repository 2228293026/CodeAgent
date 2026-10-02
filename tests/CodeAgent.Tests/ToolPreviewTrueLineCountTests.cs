using System;
using System.Linq;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具输出预览的脚注必须报**真实行数**。
///
/// 此前有两层截断叠在一起：
///   <c>BuildToolOutputPreview</c> 先把输出截到前 8 行，
///   <c>FormatToolOutputPreview</c> 再从**这份已被截断的文本**里数总行数。
/// 于是脚注恒为「共 8 行 / 已保留 8 行」——**谎报什么都没丢**。
///
/// 实测一条 12 行的输出，脚注写「共 8 行」。用户和模型都会以为命令只输出了 8 行。
///
/// <c>CapDiff</c>（同一个文件里）一直是正确的：它在截断前就记下了真实行数。
/// 这条测试把两者钉在同一口径上——别让它们再次分叉。
/// </summary>
public sealed class ToolPreviewTrueLineCountTests
{
    private static string Lines(int n) => string.Join('\n', Enumerable.Range(1, n).Select(i => $"content line {i}"));

    [Fact]
    public void ThePreviewKeepsEightLines()
    {
        var (text, _, _) = AgentClass.BuildToolOutputPreview(Lines(12));
        Assert.Equal(8, text.Split('\n').Length);
    }

    [Fact]
    public void TheTotalIsTheRealOneNotTheKeptOne()
    {
        var (text, total, _) = AgentClass.BuildToolOutputPreview(Lines(12));
        Assert.Equal(8, text.Split('\n').Length);
        // 这是本轮的核心：12 行的输出，真实值必须是 12，不是 8
        Assert.Equal(12, total);
    }

    [Fact]
    public void TheTotalCountsEveryLineEvenBeyondTheCap()
    {
        var (_, total, _) = AgentClass.BuildToolOutputPreview(Lines(500));
        Assert.Equal(500, total);
    }

    [Fact]
    public void ShortOutputKeepsItsExactCount()
    {
        var (text, total, _) = AgentClass.BuildToolOutputPreview(Lines(3));
        Assert.Equal(3, total);
        Assert.Equal(3, text.Split('\n').Length);
    }

    [Fact]
    public void EmptyOutputIsZeroLines()
    {
        var (text, total, chars) = AgentClass.BuildToolOutputPreview("");
        Assert.Equal(0, total);
        Assert.Equal(0, chars);
        Assert.Equal("", text);
    }

    [Fact]
    public void TheFootnoteReportsTheRealTotal()
    {
        var (text, total, chars) = AgentClass.BuildToolOutputPreview(Lines(12));
        var preview = AgentClass.FormatToolOutputPreview(text, trueTotalLines: total, trueTotalChars: chars);
        Assert.Contains("共 12 行", preview);
        // 绝不能出现旧口径「共 8 行」
        Assert.DoesNotContain("共 8 行", preview);
    }

    [Fact]
    public void WithoutTheTrueTotalTheFootnoteLies()
    {
        // 前提钉住：只传被截断的文本，脚注必然是「共 8 行」。
        // 将来谁把 trueTotalLines 去掉，这条会立刻变红——而不是等测试碰巧覆盖到。
        var (text, _, _) = AgentClass.BuildToolOutputPreview(Lines(12));
        var preview = AgentClass.FormatToolOutputPreview(text);
        // 12 行全在里面（只是"预览"只留了 8 行的事实没被记录），
        // 所以脚注里的总数 = 8 = 已保留数 —— 这正是原来那句谎报的由来
        Assert.DoesNotContain("共 12 行", preview);
        Assert.False(preview.Contains("仅显示前"), "没有真实总数时，连'截了多少'都说不出来");
    }

    [Fact]
    public void TheCharCountIsAlsoTheRealOne()
    {
        // 与行数同一个坑：字符数此前取的是**已被上游截到 8 行**的文本长度。
        // 实测一条 1563 字符的输出，脚注写「868 字符」。
        var bulky = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"line {i} " + new string('x', 120)));
        var (text, totalLines, totalChars) = AgentClass.BuildToolOutputPreview(bulky);
        var preview = AgentClass.FormatToolOutputPreview(text, trueTotalLines: totalLines, trueTotalChars: totalChars);
        Assert.Contains($"{totalChars:N0} 字符", preview);
        // 不能是被截断后的那份文本的长度
        Assert.DoesNotContain($"{text.Length:N0} 字符", preview);
    }

    [Fact]
    public void TheCharCountMatchesTheRawOutputExactly()
    {
        var raw = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"row {i} " + new string('y', 40)));
        var (_, _, totalChars) = AgentClass.BuildToolOutputPreview(raw);
        Assert.Equal(raw.Length, totalChars);
    }

    [Fact]
    public void CapDiffAndThePreviewNowAgreeOnTheContract()
    {
        // CapDiff 一直是对的（在截断前记行数）。把它列进来防止两条路径再次分叉：
        // 同一类"别谎报行数"的规则，不该一个遵守一个不遵守。
        var diff = DiffUtil.SplitLines(string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}")));
        var capped = AgentClass.CapDiff(string.Join('\n', diff));
        Assert.Contains("共 40 行", capped);
    }

    [Fact]
    public void NoFootnoteWhenNothingWasDropped()
    {
        var (text, total, chars) = AgentClass.BuildToolOutputPreview(Lines(3));
        var preview = AgentClass.FormatToolOutputPreview(text, trueTotalLines: total, trueTotalChars: chars);
        // 3 行全显示了，就不该出现"仅显示前 N 行"这种多余的话
        Assert.DoesNotContain("仅显示前", preview);
    }

    [Fact]
    public void BothCallSitesThreadTheRealTotal()
    {
        // 纯函数测得再全，调用点不传也是白搭——那正是原来 8 行硬编码的形状。
        // 必须匹配**解构 + 传参连在一起**：早先只查 "trueTotalLines: previewTotalLines"，
        // 而"解构之后把 previewTotalLines 改回 8"这种变异里那句话依然存在，守卫照样绿。
        var src = System.IO.File.ReadAllText(FindAgent()).Replace("\r\n", "\n");
        Assert.Contains(
            "var (preview, previewTotalLines, previewTotalChars) = BuildToolOutputPreview(output, ct);" +
            "\n                        Console.WriteLine(TextUtil.IndentBlock(FormatToolOutputPreview(preview, width: ToolPreviewWidth(Program.Columns()), trueTotalLines: previewTotalLines, trueTotalChars: previewTotalChars)",
            src);
        Assert.Contains(
            "var (failPreview, failTotalLines, failTotalChars) = BuildToolOutputPreview(output, ct);" +
            "\n                            Console.WriteLine(TextUtil.IndentBlock(" +
            "\n                                FormatToolOutputPreview(failPreview, width: ToolPreviewWidth(Program.Columns()), trueTotalLines: failTotalLines, trueTotalChars: failTotalChars)",
            src);
        // 也不能有人把真实值在传参前"顺手修正"成 keptLines
        Assert.DoesNotContain("trueTotalLines: keptLines", src);
    }

    private static string FindAgent()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var d = start;
            for (var i = 0; i < 10 && d.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(d, "src", "CodeAgent", "Agent", "Agent.cs");
                if (System.IO.File.Exists(f))
                    return f;
                var parent = System.IO.Path.GetDirectoryName(d.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == d)
                    break;
                d = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 Agent.cs");
    }
}
