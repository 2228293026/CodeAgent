using System;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/find</c> 的命中规则。
///
/// 纯子串匹配在**会话日志**这种内容上噪音极大：搜 "the" 会命中
/// <c>Pa·th·E·scapeReportToolTests.cs</c>、<c>Color·The·meTests.cs</c>、
/// <c>Contra·stThe·me</c>——搜代码标识符时几乎每一行都"命中"，等于没搜。
/// </summary>
public sealed class SearchWordBoundaryTests
{
    private static int Match(string hay, string needle, bool cs = false) =>
        AgentClass.FindOccurrence(hay, needle, 0, cs ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void MatchInsideALongerWordIsRejected()
    {
        // 三种真实噪音：左边长、右边长、中间长
        Assert.Equal(-1, Match("ColorThemeTests.cs", "the"));
        Assert.Equal(-1, Match("ContrastThemeTests.cs", "the"));
        Assert.Equal(-1, Match("PathEscapeReportTests.cs", "the"));
    }

    [Fact]
    public void StandaloneWordStillMatches()
    {
        Assert.True(Match("Use the static method", "the") >= 0);
        Assert.True(Match("\"The constructors are obsolete\"", "the") >= 0);
        Assert.True(Match("stop", "stop") >= 0);
    }

    [Fact]
    public void PrefixSearchStillMatches()
    {
        // 最关键的取舍：用户搜 Rfc2898 找的就是 Rfc2898DeriveBytes。
        // 若要求「两侧都是边界」，这里会返回 -1，把最典型的用法废掉。
        Assert.True(Match("new Rfc2898DeriveBytes()", "Rfc2898") >= 0);
    }

    [Fact]
    public void WordInternalSubstringIsStillRejected()
    {
        // 与 PathEscape 同一类：Derive 两侧都是单词字符，不是词的首尾
        Assert.Equal(-1, Match("Rfc2898DeriveBytes(byte[])", "Derive"));
    }

    [Fact]
    public void SuffixSearchStillMatches()
    {
        // 真正贴在后缀上的：Name 后面就是字符串末尾
        Assert.True(Match("CreateHashAlgorithmName", "Name") >= 0);
    }

    [Fact]
    public void MatchAtStringEdgesCountsAsBoundary()
    {
        Assert.Equal(0, Match("the end", "the"));
        Assert.Equal(6, Match("start the", "the")); // s0 t1 a2 r3 t4 ␠5 t6
    }

    [Fact]
    public void CjkKeyword_StaysSubstring()
    {
        // 中文没有词边界；硬套边界只会什么都搜不到
        Assert.True(Match("帮我修复登录bug", "登录") >= 0);
        Assert.True(Match("登录登录", "登录") >= 0);
    }

    [Fact]
    public void KeywordWithPunctuation_StaysSubstring()
    {
        // 符号本身就是边界的一部分，两侧常紧挨单词字符，
        // 按词边界匹配会把用户真正想找的东西滤掉
        Assert.False(AgentClass.NeedsSearchWordBoundary("Read()"));
        Assert.False(AgentClass.NeedsSearchWordBoundary("foo.bar"));
        Assert.False(AgentClass.NeedsSearchWordBoundary("登录"));
    }

    [Fact]
    public void PlainAsciiKeyword_NeedsWordBoundary()
    {
        Assert.True(AgentClass.NeedsSearchWordBoundary("the"));
        Assert.True(AgentClass.NeedsSearchWordBoundary("Rfc2898"));
        Assert.True(AgentClass.NeedsSearchWordBoundary("my_var"));
    }

    [Fact]
    public void EmptyKeyword_NeedsNoBoundary()
    {
        Assert.False(AgentClass.NeedsSearchWordBoundary(""));
    }

    [Fact]
    public void BoundaryRuleAppliesToCaseSensitiveSearchToo()
    {
        // 词边界与大小写敏感性无关：区分大小写搜 The，PathEscape 里的也不是
        Assert.Equal(-1, Match("ColorThemeTests.cs", "The", cs: true));
    }

    [Fact]
    public void FindsTheNextOccurrenceNotTheRejectedOne()
    {
        // 先撞上一个词中间的假命中，必须继续往后找到真的那个
        var idx = AgentClass.FindOccurrence("ColorTheme the end", "the", 0, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(11, idx);
    }

    [Fact]
    public void StartingOffsetIsRespected()
    {
        var s = "the a the";
        Assert.Equal(0, AgentClass.FindOccurrence(s, "the", 0, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(6, AgentClass.FindOccurrence(s, "the", 1, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoMatchReturnsMinusOne()
    {
        Assert.Equal(-1, Match("nothing here", "zzz"));
    }

    [Fact]
    public void DegenerateInputsAreSafe()
    {
        Assert.Equal(-1, AgentClass.FindOccurrence("", "the", 0, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-1, AgentClass.FindOccurrence("abc", "", 0, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-1, AgentClass.FindOccurrence("abc", "the", -1, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(-1, AgentClass.FindOccurrence("abc", "the", 99, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BothSearchPathsUseIt()
    {
        // MatchWindow 是会话日志与命名快照**共用**的，改一处即两处生效
        var src = System.IO.File.ReadAllText(FindSource());
        Assert.Contains("FindOccurrence(content, keyword, searchFrom, cmp)", src);
        Assert.DoesNotContain("content.IndexOf(keyword, searchFrom, cmp)", src);
    }

    private static string FindSource()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Agent", "Agent.Session.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Agent/Agent.Session.cs");
    }
}
