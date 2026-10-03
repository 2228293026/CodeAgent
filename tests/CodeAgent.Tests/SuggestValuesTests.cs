using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 取值类参数（模式名 / shell 名 / 思考强度 / 权限档位）的拼写候选。
///
/// 此前「你是不是想」这套引擎只有命令名在用（<c>/statuss</c> → <c>/status</c>），
/// 取值侧的 <c>/mode</c> 写的是另一套判据（双向 <c>Contains</c>），于是
/// <c>/mode refactr</c>、<c>/shell basj</c>、<c>/thinking higj</c>
/// **一个字候选都不给**，只把可选值列出来让用户自己在脑子里比。
/// 同一份善意，一个命令一种表现、另一个取值另一种表现——这是"同类问题只改了一处"。
/// </summary>
public sealed class SuggestValuesTests
{
    private static readonly string[] Modes = ["code", "plan", "explain", "review", "debug", "refactor", "test", "doc"];
    private static readonly string[] Shells = ["cmd", "powershell", "pwsh", "bash", "sh", "auto"];
    private static readonly string[] Efforts = ["off", "low", "medium", "high", "auto"];

    /// <summary>候选应当**只有**这一个值。写成断言单个返回值而不是断言数组，
    /// 既避开 xUnit 对集合表达式的重载歧义，也顺带钉住"只给一个候选"这件事。</summary>
    private static string Only(string[] candidates) => Assert.Single(candidates);

    [Fact]
    public void ATranspositionIsOneEditSoAdjacentSwapTyposAreCaught()
    {
        // 手指打反相邻字母是最常见的一种手误（现实中 3~4% 的人如此），
        // 但纯 Levenshtein 把 deubg→debug 记成 2 次编辑，而 5 个字母的预算只有 1
        Assert.Equal(1, Program.Levenshtein("deubg", "debug", transpositionCostsOneEdit: true));
        Assert.Equal(2, Program.Levenshtein("deubg", "debug"));
    }

    [Fact]
    public void ATranspositionStillCostsTwoInPlainLevenshtein()
    {
        // 对照：同一个词，开着新口径是 1，关掉就是 2。
        // （dbug→debug 不适合当例子——那是少打一个 e，纯删除，本来就是 1。）
        Assert.Equal(2, Program.Levenshtein("coed", "code"));
        Assert.Equal(1, Program.Levenshtein("coed", "code", transpositionCostsOneEdit: true));
        Assert.Equal(2, Program.Levenshtein("tset", "test"));
        Assert.Equal(1, Program.Levenshtein("tset", "test", transpositionCostsOneEdit: true));
    }

    [Fact]
    public void ATranspositionAndADeletionCostTheSameWhenTheFlagIsOff()
    {
        // 新口径**不改变**普通编辑距离：只影响显式开着的调用点
        Assert.Equal(1, Program.Levenshtein("dbug", "debug"));
        Assert.Equal(1, Program.Levenshtein("dbug", "debug", transpositionCostsOneEdit: true));
    }

    [Fact]
    public void PlainLevenshteinIsUnchangedByTheNewParameter()
    {
        var cases = new (string A, string B, int D)[]
        {
            ("", "abc", 3), ("abc", "", 3), ("abc", "abc", 0),
            ("kitten", "sitting", 3), ("refactor", "refactr", 1),
            ("whitelist", "whitelst", 1), ("powershell", "powershell", 0),
        };
        foreach (var (a, b, d) in cases)
        {
            Assert.Equal(d, Program.Levenshtein(a, b));
            // 新参数不得改变普通 Levenshtein 的结果：只有显式开着的调用点才用新口径
            Assert.Equal(d, Program.Levenshtein(a, b, transpositionCostsOneEdit: true));
        }
    }

    [Fact]
    public void TheTranspositionDoesNotLowerTheBarForDistantTypos()
    {
        // 换位只放宽「本来就只差一次」这一种形态，不是把阈值从 1 抬到 2
        Assert.Empty(Program.SuggestValues("dbgu", Modes));   // 换位距离 2，超过预算 1
        Assert.Empty(Program.SuggestValues("refcat", Modes)); // 换位距离 2
        Assert.Empty(Program.SuggestValues("arge", Modes));   // 换位距离 2
    }

    [Fact]
    public void SwappedAdjacentLettersAreCaughtEvenWhenTheWordIsShort()
    {
        Assert.Equal("debug", Only(Program.SuggestValues("deubg", Modes)));
        Assert.Equal("plan", Only(Program.SuggestValues("plna", Modes)));
        Assert.Equal("test", Only(Program.SuggestValues("tset", Modes)));
        Assert.Equal("code", Only(Program.SuggestValues("cdoe", Modes)));
    }

    [Fact]
    public void EveryValueKindGetsTheSameTreatment()
    {
        Assert.Equal("bash", Only(Program.SuggestValues("basj", Shells)));
        Assert.Equal("cmd", Only(Program.SuggestValues("cmdd", Shells)));
        Assert.Equal("high", Only(Program.SuggestValues("higj", Efforts)));
        Assert.Equal("strict", Only(Program.SuggestValues("stric", Program.FileAccessModes)));
        Assert.Equal("whitelist", Only(Program.SuggestValues("whitelst", Program.FileAccessModes)));
    }

    [Fact]
    public void APlainTypoStillWorks()
    {
        Assert.Equal("review", Only(Program.SuggestValues("revie", Modes)));
        Assert.Equal("medium", Only(Program.SuggestValues("medim", Efforts)));
        Assert.Equal("low", Only(Program.SuggestValues("lwo", Efforts)));
    }

    [Fact]
    public void AdjacentSwapsAreSuggestedEvenThoughContainsWouldNeverFindThem()
    {
        // coed 与 code 互不含（双向 Contains 判据对这一整类打错完全无能），
        // 但它就是一次相邻换位，必须给候选
        Assert.Equal("code", Only(Program.SuggestValues("coed", Modes)));
    }

    [Fact]
    public void SomethingEntirelyDifferentStillGetsNothing()
    {
        // 离所有取值都很远时**不提示**：给八竿子打不着的建议比不提示更让人困惑
        Assert.Empty(Program.SuggestValues("zzzzz", Modes));
        Assert.Empty(Program.SuggestValues("rgba", Modes));
        Assert.Empty(Program.SuggestValues("", Modes));
        Assert.Empty(Program.SuggestValues("   ", Modes));
    }

    [Fact]
    public void CaseIsIgnoredBecauseValuesAreLowercaseIdentifiers()
    {
        // Levenshtein 区分大小写，/mode CODE 曾被当成差 4 个字符的真拼错
        Assert.Equal("code", Only(Program.SuggestValues("CODE", Modes)));
        Assert.Equal("code", Only(Program.SuggestValues("Cdoe", Modes)));
    }

    [Fact]
    public void AnExactValueIsSuggestedButNeverOfferedAsAFixForItself()
    {
        // 纯函数不看上下文：精确命中的距离是 0，它会出现在候选里。
        // 这本身无害（精确命中是有效值，走不到报错这条路），
        // 但渲染层必须把「你自己刚敲的那个」从候选里剔掉——
        // 否则哪天校验与提示口径一错，就会出现
        // 「无效值: basj（可选: …）」下一行「你是不是想: basj」，那是在拿错误搪塞错误。
        Assert.Equal("code", Only(Program.SuggestValues("code", Modes)));
        var lines = Program.FormatInvalidValueLines("无效值", "code", ["code", "plan"], 0);
        Assert.Single(lines);
        Assert.DoesNotContain("你是不是想", lines[0]);
    }

    [Fact]
    public void APartiallyTypedValueIsStillOffered()
    {
        // 值打到一半（cod → code）与拼错同理，要给候选而不是当作无效值
        Assert.Contains("code", Program.SuggestValues("cod", Modes));
    }

    [Fact]
    public void ResultsAreBounded()
    {
        var near = Program.SuggestValues("stri", Program.FileAccessModes);
        Assert.True(near.Length <= 3, string.Join(",", near));
        Assert.Contains("strict", near);
    }

    [Fact]
    public void SuggestionsAreDeterministic()
    {
        var a = Program.SuggestValues("pwshh", Program.ShellNames);
        var b = Program.SuggestValues("pwshh", Program.ShellNames);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ModeStillUsesItsOwnListButTheSameCriterion()
    {
        // /mode 的候选来自自定义模式列表，走的是同一个判据
        Assert.Equal("refactor", Only(Program.SuggestValues("refactr", Modes)));
    }

    [Fact]
    public void CommandAndValueSuggestionsAgreeOnTranspositions()
    {
        // 同一个拼错，写成命令要能提示，写成取值也要能提示——口径一致
        Assert.Equal("/debug", Only(Program.SuggestCommands("deubg", ["/debug"])));
        Assert.Equal("debug", Only(Program.SuggestValues("deubg", ["debug"])));
    }

    [Fact]
    public void TheValueListsAreSingleSource()
    {
        // 三处各写一份字面量，改选项时必有一处漏掉，
        // 而漏掉的那处会让"可选"列表与实际可设值对不上
        var src = File.ReadAllText(Find());
        Assert.DoesNotContain("\"cmd\" or \"powershell\" or \"pwsh\" or \"bash\"", src);
        Assert.DoesNotContain("\"strict\" or \"whitelist\" or \"full\"", src);
        Assert.Contains("internal static readonly string[] ShellNames", src);
        Assert.Contains("internal static readonly string[] FileAccessModes", src);
    }

    [Fact]
    public void EveryValueCommandGoesThroughTheCandidateHelper()
    {
        // 守卫：四个取值命令都要走 FormatInvalidValueLines（自带候选），
        // 而不是只打印「可选」列表
        var src = File.ReadAllText(Find());
        foreach (var call in new[]
        {
            "FormatInvalidValueLines(\"无效值\", rest, ThinkingEfforts",
            "FormatInvalidValueLines(\"无效值\", rest, ShellNames",
            "FormatInvalidValueLines(\"无效权限模式\", rest, FileAccessModes",
            "SuggestValues(wanted, modes.Select(m => m.Name))",
        })
            Assert.Contains(call, src);
    }

    [Fact]
    public void TheTranspositionFlagIsActuallyPassedAtTheCallSite()
    {
        // 形状守卫：只测 Levenshtein 本身证明不了"命令建议真的用上了它"。
        // 改动前命令侧本就有候选，但阈值之外的换位全被丢弃——
        // 光看 Levenshtein 的单测会以为这功能早就在。
        var src = File.ReadAllText(Find());
        Assert.Contains("Levenshtein(want, name, transpositionCostsOneEdit: true)", src);
        Assert.Contains("internal static int Levenshtein(string a, string b, bool transpositionCostsOneEdit = false)", src);
        // 注意 1：不能写 DoesNotContain("Levenshtein(want, name)")——那是上面那行的**子串**，
        // 断言它不存在等于断言功能不存在。这条形状守卫曾经这样写，测试一片红。
        Assert.DoesNotContain("Levenshtein(want, name);", src);
        // 注意 2：断言的必须是**代码**，不能是自己的说明文字。上一条守卫的字符串
        // 一度出现在 XML 文档注释里（讲历史时引了旧代码），于是一条"代码不该再这么写"
        // 的守卫，被文档里的一段历史判成了真——守卫自己撒了谎。
    }

    [Fact]
    public void TheRollingBufferIsWideEnoughForTheLookback()
    {
        // 换位要回看第 i-2 行，而第一版沿用两行滚动缓冲：换位分支读到的是第 i-1 行，
        // 于是 deubg→debug 仍算 2，功能看起来"改了但没生效"。
        // 钉住缓冲必须有三行——否则重构时很容易顺手改回两行。
        var src = File.ReadAllText(Find());
        Assert.Contains("transpositionCostsOneEdit ? new int[b.Length + 1] : prev", src);
        Assert.Contains("Math.Min(cur[j], prev2[j - 2] + 1)", src);
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
