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
