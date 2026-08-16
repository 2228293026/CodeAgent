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
    public void Literal_IsCaseInsensitive()
    {
        var re = Glob.ToRegex("README.md");
        Assert.Matches(re, "README.md");
        Assert.Matches(re, "readme.md"); // RegexOptions.IgnoreCase
    }
}
