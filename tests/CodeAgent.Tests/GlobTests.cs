using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class GlobTests
{
    [Fact]
    public void Star_MatchesWithinSingleSegment()
    {
        var re = Glob.ToRegex("*.cs");
        Assert.Matches(re, "Program.cs");
        Assert.DoesNotMatch(re, "Program.cs.txt");
        Assert.DoesNotMatch(re, "sub/Program.cs"); // * 不跨目录
    }

    [Fact]
    public void DoubleStar_CrossesDirectories()
    {
        var re = Glob.ToRegex("**/*.rs");
        Assert.Matches(re, "main.rs");
        Assert.Matches(re, "src/a/b.rs");
    }

    [Fact]
    public void DoubleStarSlash_DoesNotMatchPartialSegment()
    {
        // 回归：a/**/b 曾用 .* 导致误匹配 a/xb（x 不是目录段）；现在只匹配完整目录段
        var re = Glob.ToRegex("a/**/b");
        Assert.Matches(re, "a/b");
        Assert.Matches(re, "a/x/b");
        Assert.Matches(re, "a/x/y/b");
        Assert.DoesNotMatch(re, "a/xb");
        Assert.DoesNotMatch(re, "a/xb/c");
    }

    [Fact]
    public void DoubleStarSlash_AtStart_DoesNotMatchPartialSegment()
    {
        var re = Glob.ToRegex("**/foo.txt");
        Assert.Matches(re, "foo.txt");
        Assert.Matches(re, "src/deep/foo.txt");
        Assert.DoesNotMatch(re, "xfoo.txt");
    }

    [Fact]
    public void Question_MatchesExactlyOneChar()
    {
        var re = Glob.ToRegex("a?c.txt");
        Assert.Matches(re, "abc.txt");
        Assert.DoesNotMatch(re, "ac.txt");
    }

    [Fact]
    public void CharClass_MatchesAnyListedChar()
    {
        var re = Glob.ToRegex("file[abc].txt");
        Assert.Matches(re, "filea.txt");
        Assert.Matches(re, "fileb.txt");
        Assert.Matches(re, "filec.txt");
        Assert.DoesNotMatch(re, "filed.txt");
    }

    [Fact]
    public void CharClass_Range_MatchesWithinRange()
    {
        var re = Glob.ToRegex("[a-c].txt");
        Assert.Matches(re, "a.txt");
        Assert.Matches(re, "c.txt");
        Assert.DoesNotMatch(re, "d.txt");
    }

    [Fact]
    public void CharClass_Negated_ExcludesListedChars()
    {
        var re = Glob.ToRegex("[!ab].txt");
        Assert.Matches(re, "c.txt");
        Assert.DoesNotMatch(re, "a.txt");
        Assert.DoesNotMatch(re, "b.txt");
    }

    [Fact]
    public void UnclosedCharClass_TreatedAsLiteral()
    {
        var re = Glob.ToRegex("a[b");
        Assert.Matches(re, "a[b");
        Assert.DoesNotMatch(re, "ab");
    }

    [Fact]
    public void BraceAlternation_MatchesAnyOption()
    {
        var re = Glob.ToRegex("*.{cs,rs}");
        Assert.Matches(re, "main.cs");
        Assert.Matches(re, "main.rs");
        Assert.DoesNotMatch(re, "main.py");
        Assert.DoesNotMatch(re, "sub/main.cs"); // * 不跨目录
    }

    [Fact]
    public void BraceWithoutComma_TreatedAsLiteral()
    {
        var re = Glob.ToRegex("a{b");
        Assert.Matches(re, "a{b");
        Assert.DoesNotMatch(re, "ab");
    }

    [Fact]
    public void BraceWithEmptyOption_IgnoresEmptyParts()
    {
        var re = Glob.ToRegex("x.{cs,}");
        Assert.Matches(re, "x.cs");
        Assert.DoesNotMatch(re, "x.rs"); // 空选项被忽略，仅保留 cs
    }

    [Fact]
    public void Literal_IsCaseInsensitive()
    {
        var re = Glob.ToRegex("README.md");
        Assert.Matches(re, "README.md");
        Assert.Matches(re, "readme.md"); // RegexOptions.IgnoreCase
    }

    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "Program.csx", false)]
    [InlineData("*.cs", "sub/Program.cs", false)]
    [InlineData("**/*.rs", "main.rs", true)]
    [InlineData("**/*.rs", "src/a/b.rs", true)]
    [InlineData("src/**", "src/a/b.rs", true)]
    [InlineData("src/**", "src.rs", false)]           // ** 后无 / 时按字面匹配到末尾
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("a/**/b", "a/xb", false)]
    [InlineData("?a?.txt", "bab.txt", true)]
    [InlineData("?a?.txt", "ab.txt", false)]
    [InlineData("file[0-9].txt", "file5.txt", true)]
    [InlineData("file[0-9].txt", "fileX.txt", false)]
    [InlineData("*.{cs,rs,py}", "x.py", true)]
    [InlineData("*.{cs,rs,py}", "x.go", false)]
    public void ToRegex_VariousPatterns(string pattern, string path, bool shouldMatch)
    {
        var re = Glob.ToRegex(pattern);
        if (shouldMatch)
            Assert.Matches(re, path);
        else
            Assert.DoesNotMatch(re, path);
    }

    [Fact]
    public void BackslashPattern_IsNormalizedToForwardSlash()
    {
        // 回归：Windows 风格反斜杠分隔符的 pattern（src\**\*.cs）曾匹配不到
        // 已归一化成正斜杠的相对路径（工具层 rel.Replace('\\','/')）；现在 ToRegex 内部归一化
        var re = Glob.ToRegex("src\\**\\*.cs");
        Assert.Matches(re, "src/a/b/c.cs");
        Assert.Matches(re, "src/main.cs");
        Assert.DoesNotMatch(re, "other/a.cs");
    }

    [Fact]
    public void RegexSpecials_AreEscapedAsLiteral()
    {
        var re = Glob.ToRegex("a+b(c).txt");
        Assert.Matches(re, "a+b(c).txt");
        Assert.DoesNotMatch(re, "aabcc.txt"); // + ( ) 应被转义为字面
    }

    [Fact]
    public void Dot_IsEscapedNotWildcard()
    {
        var re = Glob.ToRegex("a.b.txt");
        Assert.Matches(re, "a.b.txt");
        Assert.DoesNotMatch(re, "axb.txt"); // . 不应匹配任意字符
    }

    [Fact]
    public void CaretInsideCharClass_ActsAsNegation()
    {
        var re = Glob.ToRegex("[^ab].txt"); // ^ 也支持否定（与 ! 等价）
        Assert.Matches(re, "c.txt");
        Assert.DoesNotMatch(re, "a.txt");
    }

    [Fact]
    public void BraceNestedWithPath_Combines()
    {
        var re = Glob.ToRegex("src/{main,util}.cs");
        Assert.Matches(re, "src/main.cs");
        Assert.Matches(re, "src/util.cs");
        Assert.DoesNotMatch(re, "src/other.cs");
    }

    [Fact]
    public void EmptyPattern_MatchesEmptyOnly()
    {
        var re = Glob.ToRegex("");
        Assert.Matches(re, "");
        Assert.DoesNotMatch(re, "x");
    }

    [Fact]
    public void QuestionInCharClass_IsLiteral()
    {
        var re = Glob.ToRegex("[a?].txt"); // ? 在字符类内是字面
        Assert.Matches(re, "a.txt");
        Assert.Matches(re, "?.txt");
        Assert.DoesNotMatch(re, "b.txt");
    }
}
