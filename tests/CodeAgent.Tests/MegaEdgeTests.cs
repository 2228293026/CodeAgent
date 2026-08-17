using System;
using CodeAgent;
using Xunit;
using static CodeAgent.Program;

namespace CodeAgent.Tests;

/// <summary>参数化边界批量补充：TextUtil / Glob / SplitCommand 的 Theory 用例。</summary>
public class MegaEdgeTests
{
    // ===== Truncate =====

    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abcdef", 3, "abc\n…(共 6 字符，已截断)")]
    [InlineData("", 0, "")]
    [InlineData("中文内容测试", 4, "中文内容\n…(共 6 字符，已截断)")]
    public void Truncate_Cases(string s, int max, string expected) =>
        Assert.Equal(expected, TextUtil.Truncate(s, max));

    [Theory]
    [InlineData("a\tb", 4, "a    …")]      // "a   b"(5 字符)>4 → 截 4 字符 + " …"
    [InlineData("a\tb", 3, "a   …")]       // "a   b"(5 字符)>3 → 截 3 字符 + " …"
    [InlineData("short", 10, "short")]
    [InlineData("longer", 4, "long …")]
    public void TruncateLine_Cases(string s, int max, string expected) =>
        Assert.Equal(expected, TextUtil.TruncateLine(s, max));

    // ===== CountOccurrences =====

    [Theory]
    [InlineData("aaa", "aa", 1)]
    [InlineData("aaaa", "aa", 2)]
    [InlineData("ababab", "ab", 3)]
    [InlineData("hello hello hello", "hello", 3)]
    [InlineData("x", "xy", 0)]
    [InlineData("", "a", 0)]
    [InlineData("abc", "", 0)]
    [InlineData("aaaa", "a", 4)]
    public void CountOccurrences_Cases(string text, string sub, int expected) =>
        Assert.Equal(expected, TextUtil.CountOccurrences(text, sub));

    // ===== CompactTokenCount =====

    [Theory]
    [InlineData(1, "1")]
    [InlineData(999, "999")]
    [InlineData(1000, "1.0k")]
    [InlineData(1234, "1.2k")]
    [InlineData(999_999, "1000.0k")]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(1_500_000, "1.5M")]
    [InlineData(2_000_000_000, "2000.0M")]
    [InlineData(-1, "-1")]
    [InlineData(0, "0")]
    public void CompactTokenCount_Cases(long n, string expected) =>
        Assert.Equal(expected, TextUtil.CompactTokenCount(n));

    // ===== FormatSessionTime =====

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(5, "5s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m 0s")]
    [InlineData(125, "2m 5s")]
    [InlineData(3600, "60m 0s")]
    [InlineData(59.4, "59s")]
    [InlineData(59.6, "60s")]
    public void FormatSessionTime_Cases(double seconds, string expected) =>
        Assert.Equal(expected, TextUtil.FormatSessionTime(TimeSpan.FromSeconds(seconds)));

    // ===== FormatElapsed =====

    [Theory]
    [InlineData(0, "0.0s")]
    [InlineData(1.5, "1.5s")]
    [InlineData(59.95, "60.0s")]
    [InlineData(60, "1m 0s")]
    [InlineData(125.4, "2m 5s")]
    public void FormatElapsed_Cases(double seconds, string expected) =>
        Assert.Equal(expected, TextUtil.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    // ===== FormatDuration =====

    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(0.5, "500ms")]
    [InlineData(0.999, "999ms")]
    [InlineData(1, "1.0s")]
    [InlineData(2.25, "2.2s")]            // 2.25 的 double 表示略小于 2.25 → F1 得 "2.2"
    public void FormatDuration_Cases(double seconds, string expected) =>
        Assert.Equal(expected, TextUtil.FormatDuration(TimeSpan.FromSeconds(seconds)));

    // ===== ShortModelName =====

    [Theory]
    [InlineData("gpt-4o", "gpt-4o")]
    [InlineData("deepseek/deepseek-chat", "deepseek-chat")]
    [InlineData("tencent/hy3:free", "hy3:free")]
    [InlineData("a/b/c", "a/b/c")]        // 末段 1 字符 < 5 → 完整
    [InlineData("org/x", "org/x")]       // 末段 1 字符 < 5 → 完整
    [InlineData("", "")]
    [InlineData("ab/cdef", "ab/cdef")]   // 末段 4 字符 < 5 → 完整
    [InlineData("ab/cdefg", "cdefg")]    // 末段 5 字符 → 段
    public void ShortModelName_Cases(string model, string expected) =>
        Assert.Equal(expected, TextUtil.ShortModelName(model));

    // ===== PercentOf =====

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(50, 100, 50)]
    [InlineData(100, 100, 100)]
    [InlineData(150, 100, 100)]
    [InlineData(-10, 100, 0)]
    [InlineData(10, 0, 0)]
    [InlineData(10, -5, 0)]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 66)]
    [InlineData(999, 1000, 99)]
    public void PercentOf_Cases(long part, long total, int expected) =>
        Assert.Equal(expected, TextUtil.PercentOf(part, total));

    // ===== Glob 模式（正匹配）=====

    [Theory]
    [InlineData("*.txt", "a.txt")]
    [InlineData("*.txt", "b.txt")]
    [InlineData("dir/*.cs", "dir/x.cs")]
    [InlineData("**/*.md", "README.md")]
    [InlineData("**/*.md", "docs/guide.md")]
    [InlineData("**/*.md", "a/b/c/readme.md")]
    [InlineData("src/**", "src/a.cs")]
    [InlineData("src/**", "src/a/b/c.cs")]
    [InlineData("?oo", "foo")]
    [InlineData("f?o", "foo")]
    [InlineData("[a-c]x", "bx")]
    [InlineData("[!a-c]x", "zx")]
    [InlineData("{a,b}.cs", "a.cs")]
    [InlineData("{a,b}.cs", "b.cs")]
    [InlineData("*.{cs,fs,vb}", "x.fs")]
    [InlineData("x*", "x")]
    [InlineData("x*", "xyz")]
    [InlineData("a/**/b", "a/b")]
    [InlineData("a/**/b", "a/x/b")]
    [InlineData("a/**/b", "a/x/y/b")]
    [InlineData("UPPER", "upper")]
    [InlineData("with space.txt", "with space.txt")]
    [InlineData("dash-name.ext", "dash-name.ext")]
    [InlineData("num[0-9]", "num7")]
    public void Glob_Match_Cases(string pattern, string path) =>
        Assert.True(CodeAgent.Glob.ToRegex(pattern).IsMatch(path.Replace('\\', '/')), $"{pattern} 应匹配 {path}");

    // ===== Glob 模式（反匹配）=====

    [Theory]
    [InlineData("*.txt", "a.md")]
    [InlineData("*.txt", "sub/a.txt")]
    [InlineData("dir/*.cs", "dir/sub/x.cs")]
    [InlineData("?oo", "fooo")]
    [InlineData("f?o", "fo")]
    [InlineData("[a-c]x", "dx")]
    [InlineData("[!a-c]x", "bx")]
    [InlineData("{a,b}.cs", "c.cs")]
    [InlineData("a/**/b", "a/xb")]
    [InlineData("x[0-9]", "xa")]
    [InlineData("file.", "file")]
    [InlineData(".hidden", "visible")]
    public void Glob_Reject_Cases(string pattern, string path) =>
        Assert.False(CodeAgent.Glob.ToRegex(pattern).IsMatch(path.Replace('\\', '/')), $"{pattern} 不应匹配 {path}");

    // ===== SplitCommand =====

    [Theory]
    [InlineData("/help", "/help", "")]
    [InlineData("/clear", "/clear", "")]
    [InlineData("/model gpt-4o", "/model", "gpt-4o")]
    [InlineData("/mode plan", "/mode", "plan")]
    [InlineData("/save 我的 会话", "/save", "我的 会话")]
    [InlineData("/exit", "/exit", "")]
    [InlineData("/undo 3", "/undo", "3")]
    [InlineData("/access whitelist", "/access", "whitelist")]
    public void SplitCommand_Cases(string line, string expectedCmd, string expectedRest)
    {
        var (cmd, rest) = SplitCommand(line);
        Assert.Equal(expectedCmd, cmd);
        Assert.Equal(expectedRest, rest);
    }
}
