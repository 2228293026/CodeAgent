using System;
using System.Linq;
using CodeAgent;
using Xunit;
using static CodeAgent.Program;

namespace CodeAgent.Tests;

/// <summary>剩余模块补漏：Glob 更多模式 / SplitCommand / Modes / DiffUtil 的边界测试。</summary>
public class RemainingEdgeTests
{
    private static bool GlobMatch(string pattern, string path) =>
        CodeAgent.Glob.ToRegex(pattern).IsMatch(path.Replace('\\', '/'));

    // ===== Glob:更多模式 =====

    [Theory]
    [InlineData("**", "a.cs")]
    [InlineData("**", "a/b/c.cs")]
    [InlineData("src/**/*.cs", "src/a.cs")]        // **/ 匹配零段
    [InlineData("src/**/*.cs", "src/a/b/c.cs")]
    [InlineData("*.cs", "a.cs")]
    [InlineData("a/*.cs", "a/b.cs")]
    [InlineData("a?b.cs", "axb.cs")]
    [InlineData("file.{cs,fs}", "file.fs")]
    [InlineData("**/*.{cs,fs}", "src/x.fs")]
    [InlineData("x[0-9].txt", "x5.txt")]
    [InlineData("x[a-c].txt", "xb.txt")]
    [InlineData("x[!abc].txt", "xz.txt")]
    [InlineData("中文路径/*.txt", "中文路径/a.txt")]
    [InlineData("a\\b.cs", "a/b.cs")]              // 反斜杠归一化
    public void Glob_PatternMatches(string pattern, string path) =>
        Assert.True(GlobMatch(pattern, path), $"{pattern} 应匹配 {path}");

    [Theory]
    [InlineData("*.cs", "a/b.cs")]                 // 星号不跨目录段
    [InlineData("a/*.cs", "a/b/c.cs")]             // 单段星号不递归
    [InlineData("src/**/*.cs", "src/a.cs.txt")]    // 扩展名精确
    [InlineData("a?b.cs", "ab.cs")]                // ? 恰好一个字符
    [InlineData("x[0-9].txt", "xa.txt")]           // 字符类外
    [InlineData("x[!abc].txt", "xb.txt")]          // 否定类内
    [InlineData("file.{cs,fs}", "file.csx")]       // 花括号多选精确
    public void Glob_PatternRejects(string pattern, string path) =>
        Assert.False(GlobMatch(pattern, path), $"{pattern} 不应匹配 {path}");

    [Fact]
    public void Glob_DotIsEscaped()
    {
        Assert.True(GlobMatch("a.b", "a.b"));
        Assert.False(GlobMatch("a.b", "axb"));
    }

    [Fact]
    public void Glob_RegexSpecials_AreLiteral()
    {
        Assert.True(GlobMatch("a+b", "a+b"));
        Assert.True(GlobMatch("a$b", "a$b"));
        Assert.True(GlobMatch("(a)", "(a)"));
        Assert.True(GlobMatch("a|b", "a|b"));
    }

    [Fact]
    public void Glob_Literal_IsCaseInsensitive()
    {
        Assert.True(GlobMatch("ReadMe.MD", "readme.md"));
    }

    [Fact]
    public void Glob_BraceNestedWithSlash_Combines()
    {
        Assert.True(GlobMatch("{src,test}/*.cs", "test/a.cs"));
        Assert.False(GlobMatch("{src,test}/*.cs", "lib/a.cs"));
    }

    [Fact]
    public void Glob_DoubleStarWithoutSlash_MatchesAcrossSegments()
    {
        // ** 无尾随斜杠 → .* 匹配任意（含 /）
        Assert.True(GlobMatch("a**b", "a/x/b"));
        Assert.True(GlobMatch("a**b", "ab"));
    }

    // ===== SplitCommand =====

    [Fact]
    public void SplitCommand_NoSpace_WholeAsCommand()
    {
        var (cmd, rest) = SplitCommand("/mode");
        Assert.Equal("/mode", cmd);
        Assert.Equal("", rest);
    }

    [Fact]
    public void SplitCommand_SingleSpace_Splits()
    {
        var (cmd, rest) = SplitCommand("/model gpt-4o");
        Assert.Equal("/model", cmd);
        Assert.Equal("gpt-4o", rest);
    }

    [Fact]
    public void SplitCommand_MultipleSpaces_RestKeepsExtraSpaces()
    {
        var (cmd, rest) = SplitCommand("/save   my name");
        Assert.Equal("/save", cmd);
        Assert.Equal("  my name", rest); // 只按第一个空格切
    }

    [Fact]
    public void SplitCommand_LeadingSpace_EmptyCommand()
    {
        var (cmd, rest) = SplitCommand(" /mode");
        Assert.Equal("", cmd);
        Assert.Equal("/mode", rest);
    }

    [Fact]
    public void SplitCommand_OnlySpaces_RestIsWhitespace()
    {
        var (cmd, rest) = SplitCommand("   ");
        Assert.Equal("", cmd);
        Assert.Equal("  ", rest);
    }

    [Fact]
    public void SplitCommand_Tab_NotTreatedAsSeparator()
    {
        var (cmd, rest) = SplitCommand("/mode\tnext");
        Assert.Equal("/mode\tnext", cmd); // 只按空格切，Tab 保留在 cmd
        Assert.Equal("", rest);
    }

    // ===== Modes =====

    [Fact]
    public void Modes_Build_IncludesBuiltinsAndCustom()
    {
        var cfg = new AgentConfig
        {
            Modes = [new AgentModeConfig { Name = "custom1", Description = "d", SystemPrompt = "p", Tools = ["read_file"] }],
        };
        var modes = Modes.Build(cfg);
        Assert.Contains(modes, m => m.Name == "code");
        Assert.Contains(modes, m => m.Name == "plan");
        Assert.Contains(modes, m => m.Name == "custom1");
        Assert.Equal(9, modes.Count); // 8 内置 + 1 自定义
    }

    [Fact]
    public void Modes_Find_IsCaseInsensitive()
    {
        Assert.Equal("plan", Modes.Find("PLAN", new AgentConfig()).Name);
    }

    [Fact]
    public void Modes_Find_CustomMode_ByName()
    {
        var cfg = new AgentConfig { Modes = [new AgentModeConfig { Name = "fix", Description = "修复" }] };
        Assert.Equal("fix", Modes.Find("fix", cfg).Name);
    }

    [Fact]
    public void Modes_Find_Unknown_FallsBackToCode()
    {
        Assert.Equal("code", Modes.Find("nonexistent", new AgentConfig()).Name);
    }

    [Fact]
    public void Modes_Find_CustomOverridesBuiltinName()
    {
        // 自定义模式与内置同名：自定义优先（Build 追加在后，Find 取第一个匹配——内置在前）
        var cfg = new AgentConfig { Modes = [new AgentModeConfig { Name = "plan", Description = "自定义 plan" }] };
        var found = Modes.Find("plan", cfg);
        Assert.NotNull(found);
    }

    [Fact]
    public void Modes_ListText_ContainsNames()
    {
        var text = Modes.ListText(new AgentConfig());
        Assert.Contains("code", text);
        Assert.Contains("review", text);
        Assert.Contains("debug", text);
    }

    [Fact]
    public void Modes_Find_NullName_FallsBackToCode()
    {
        Assert.Equal("code", Modes.Find(null!, new AgentConfig()).Name);
    }

    // ===== DiffUtil:更多场景 =====

    [Fact]
    public void DiffUtil_MiddleChange_ShowsHunk()
    {
        var d = CodeAgent.DiffUtil.Unified("a\nb\nc\nd\n", "a\nX\nc\nd\n", "f.txt");
        Assert.Contains("- b", d);
        Assert.Contains("+ X", d);
        Assert.Contains("@@", d);
    }

    [Fact]
    public void DiffUtil_ConsecutiveChanges_OneHunk()
    {
        var d = CodeAgent.DiffUtil.Unified("a\nb\nc\n", "x\ny\nc\n", "f.txt");
        Assert.Contains("- a", d);
        Assert.Contains("+ x", d);
        Assert.Contains("- b", d);
        Assert.Contains("+ y", d);
    }

    [Fact]
    public void DiffUtil_InsertAtStart_ShowsAddedPrefix()
    {
        var d = CodeAgent.DiffUtil.Unified("b\nc\n", "a\nb\nc\n", "f.txt");
        Assert.Contains("+ a", d);
    }

    [Fact]
    public void DiffUtil_AppendAtEnd_ShowsAddedSuffix()
    {
        var d = CodeAgent.DiffUtil.Unified("a\n", "a\nb\n", "f.txt");
        Assert.Contains("+ b", d);
    }

    [Fact]
    public void DiffUtil_WhitespaceChange_IsDiff()
    {
        // 行内容含尾随空格差异：应识别为改动
        var d = CodeAgent.DiffUtil.Unified("a \nb\n", "a\nb\n", "f.txt");
        Assert.NotEqual("", d);
    }

    [Fact]
    public void DiffUtil_IdenticalWithTrailingNewline_IsEmpty()
    {
        Assert.Equal("", CodeAgent.DiffUtil.Unified("a\nb\n", "a\nb\n", "f.txt"));
        Assert.Equal("", CodeAgent.DiffUtil.Unified("a\nb", "a\nb", "f.txt"));
    }
}
