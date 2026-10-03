using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 「无效取值」的第二行：点名最接近的取值。
///
/// 此前四个取值命令里只有 <c>/mode</c> 会提示候选，而它的判据是双向 <c>Contains</c>，
/// <c>refactr</c>、<c>deubg</c> 这类打错一个字母的**一个候选都不给**；
/// <c>/shell</c>、<c>/thinking</c>、<c>/access</c> 连候选的概念都没有，
/// 只打一句「可选: cmd / powershell / pwsh / bash / sh / auto」——
/// 六个值全列出来，用户得自己在脑子里做编辑距离。
/// </summary>
public sealed class InvalidValueSuggestionTests
{
    private static readonly string[] Modes = ["code", "plan", "explain", "review", "debug", "refactor", "test", "doc"];

    [Fact]
    public void TheHintLineNamesTheClosestValue()
    {
        var lines = Program.FormatInvalidValueLines("无效值", "basj", Program.ShellNames, 0);
        Assert.Equal(2, lines.Count);
        Assert.Contains("basj", lines[0]);
        Assert.Contains("你是不是想", lines[1]);
        Assert.Contains("bash", lines[1]);
    }

    [Fact]
    public void NoHintLineWhenNothingIsClose()
    {
        // 离所有取值都很远时不提示：八竿子打不着的建议比不提示更让人困惑
        var lines = Program.FormatInvalidValueLines("无效值", "zzzzzz", Program.ShellNames, 0);
        Assert.Single(lines);
        Assert.DoesNotContain("你是不是想", lines[0]);
    }

    [Fact]
    public void TheFirstLineAlwaysStillListsTheOptions()
    {
        // 候选是**追加**的一行，不是替换：用户仍需要知道全部合法值
        var lines = Program.FormatInvalidValueLines("无效权限模式", "stric", Program.FileAccessModes, 0);
        Assert.Contains("可选: strict / whitelist / full", lines[0]);
    }

    [Fact]
    public void SeveralCandidatesAreJoinedWithTheChineseListSeparator()
    {
        var lines = Program.FormatInvalidValueLines("无效值", "pwsh", ["pws", "pwsh", "pwshx", "shell"], 0);
        Assert.Contains("、", string.Concat(lines));
    }

    [Fact]
    public void TheOverloadWithPrecomputedCandidatesAgreesWithTheComputedOne()
    {
        var computed = Program.FormatInvalidValueLines("无效值", "basj", Program.ShellNames, 0);
        var given = Program.FormatInvalidValueLines("无效值", "basj", Program.ShellNames, ["bash"], 0);
        Assert.Equal(computed, given);
    }

    [Fact]
    public void TheOverloadNeverInventsACandidate()
    {
        // 调用点算出的候选为空时，不该由渲染层自己"猜"一个出来
        var lines = Program.FormatInvalidValueLines("无效值", "zzzzzz", Program.ShellNames, [], 0);
        Assert.Single(lines);
    }

    [Fact]
    public void BothLinesAreBoundedWhenGivenAWidth()
    {
        var longValue = new string('x', 200);
        foreach (var width in new[] { 12, 24, 40, 80 })
        {
            foreach (var line in Program.FormatInvalidValueLines("无效值", longValue, Program.ShellNames, width))
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void AModeTypoAlsoGetsAHint()
    {
        // /mode 的取值来自自定义列表，但判据必须与命令名、其它取值一致
        var near = Program.SuggestValues("refactr", Modes);
        Assert.Equal("refactor", Assert.Single(near));
        var lines = Program.FormatInvalidValueLines("没有模式", "refactr", Modes, near, 0);
        Assert.Contains("refactor", string.Concat(lines));
    }

    [Fact]
    public void NoCallSiteInventsItsOwnDistance()
    {
        // 判据散落到调用点，迟早有一处用旧规则（双向 Contains），
        // 表现就是"同一个错误三种提示"
        var src = File.ReadAllText(Find());
        Assert.DoesNotContain("mode.Name.Contains(wanted)", src);
        Assert.DoesNotContain("wanted.Contains(mode.Name)", src);
        // 带尾分号：否则 "Levenshtein(want, name)" 会作为子串匹配到
        // "Levenshtein(want, name, transpositionCostsOneEdit: true)" 身上——
        // 一条断言它"不存在"的守卫，恰好在功能存在时失败，等于反向撒谎
        Assert.DoesNotContain("Levenshtein(want, name);", src);
        // 计数是量出来的，不是猜的：定义 1 处 + /mode 直接调用 1 处 + FormatInvalidValueLines 内部 1 处 = 3。
        // 「四个取值命令都调用」这句话不是靠这个数字证的，是靠下面那几个 Contains 锚点证的。
        Assert.True(Count(src, "SuggestValues(") >= 3, "定义 + /mode + FormatInvalidValueLines");
    }

    [Fact]
    public void TheSingleLineFormatterIsStillUsedForTheOptionsList()
    {
        // 这次是加一行，不是换掉那一行：原有的统一格式（圆括号 / 斜杠 / 冒号）不能退化
        var line = Program.FormatInvalidValueLine("无效值", "bogus", ["a", "b"]);
        Assert.Contains("（可选: a / b）", line);
        Assert.DoesNotContain("你是不是想", line);
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
