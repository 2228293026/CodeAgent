using System;
using CodeAgent;
using Xunit;
using static CodeAgent.Program;

namespace CodeAgent.Tests;

/// <summary>Glob/TextUtil/PercentOf/SplitCommand 的批量 Theory 用例。</summary>
public class FinalPushTests
{
    // ===== Glob 正匹配（新组合）=====

    [Theory]
    [InlineData("a/b/c.txt", "a/b/c.txt")]
    [InlineData("**/*", "x.txt")]
    [InlineData("**/*", "a/b/c.txt")]
    [InlineData("src/**/x.cs", "src/x.cs")]
    [InlineData("src/**/x.cs", "src/a/b/x.cs")]
    [InlineData("**/test/**", "test/a.cs")]
    [InlineData("**/test/**", "a/test/b.cs")]
    [InlineData("*.{md,txt}", "doc.txt")]
    [InlineData("*.{md,txt}", "doc.md")]
    [InlineData("{a,b}/**", "a/x.cs")]
    [InlineData("a[bc]d", "abd")]
    [InlineData("a[bc]d", "acd")]
    [InlineData("[0-9][0-9]", "42")]
    [InlineData("[a-f][0-9]", "f9")]
    [InlineData("x[!0-9]y", "xay")]
    [InlineData("name?.txt", "name1.txt")]
    [InlineData("name?.txt", "nameX.txt")]
    [InlineData("a/**", "a/")]
    [InlineData("a/**", "a/b")]
    [InlineData("deep/**/leaf", "deep/leaf")]
    [InlineData("deep/**/leaf", "deep/mid/leaf")]
    [InlineData("*.cs", "Program.cs")]
    [InlineData("**/*.sln", "CodeAgent.sln")]
    [InlineData("**/*.slnx", "CodeAgent.slnx")]
    [InlineData("字母/*.txt", "字母/文件.txt")]
    [InlineData("v1.*", "v1.0")]
    [InlineData("v1.*", "v1.2.3")]
    [InlineData("core/*", "core/thing")]
    public void Glob_Match_More(string pattern, string path) =>
        Assert.True(CodeAgent.Glob.ToRegex(pattern).IsMatch(path.Replace('\\', '/')), $"{pattern} 应匹配 {path}");

    // ===== Glob 反匹配（新组合）=====

    [Theory]
    [InlineData("a/b/c.txt", "a/b/d.txt")]
    [InlineData("src/**/x.cs", "src/y.cs")]
    [InlineData("**/test/**", "tests/a.cs")]
    [InlineData("a[bc]d", "aed")]
    [InlineData("[0-9][0-9]", "4a")]
    [InlineData("x[!0-9]y", "x1y")]
    [InlineData("name?.txt", "name.txt")]
    [InlineData("a/**", "ab")]
    [InlineData("deep/**/leaf", "deep/midleaf")]
    [InlineData("*.cs", "a.cs.txt")]
    [InlineData("core/*", "core/a/b")]
    [InlineData("v1.*", "v1")]
    public void Glob_Reject_More(string pattern, string path) =>
        Assert.False(CodeAgent.Glob.ToRegex(pattern).IsMatch(path.Replace('\\', '/')), $"{pattern} 不应匹配 {path}");

    // ===== TextUtil:Truncate 更多 =====

    [Theory]
    [InlineData("hello world", 5, "hello\n…(共 11 字符，已截断)")]
    [InlineData("hello world", 11, "hello world")]
    [InlineData("x", 1, "x")]
    [InlineData("xy", 1, "x\n…(共 2 字符，已截断)")]
    [InlineData("aaaaaa", 0, "\n…(共 6 字符，已截断)")]
    [InlineData("中文", 1, "中\n…(共 2 字符，已截断)")]
    public void Truncate_More(string s, int max, string expected) =>
        Assert.Equal(expected, TextUtil.Truncate(s, max));

    // ===== TextUtil:TruncateLine 更多 =====

    [Theory]
    [InlineData("", 5, "")]
    [InlineData("abc", 2, "ab …")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abcd", 4, "abcd")]
    [InlineData("a\tb\tc", 2, "a  …")]  // Tab 展开 "a    b    c" 截 2 + " …"
    public void TruncateLine_More(string s, int max, string expected) =>
        Assert.Equal(expected, TextUtil.TruncateLine(s, max));

    // ===== PercentOf 更多 =====

    [Theory]
    [InlineData(1, 1000, 0)]
    [InlineData(5, 1000, 0)]
    [InlineData(10, 1000, 1)]
    [InlineData(250, 1000, 25)]
    [InlineData(333, 1000, 33)]
    [InlineData(667, 1000, 66)]
    [InlineData(0, 1, 0)]
    [InlineData(1, 1, 100)]
    [InlineData(7, 2, 100)]
    [InlineData(3, 10, 30)]
    public void PercentOf_More(long part, long total, int expected) =>
        Assert.Equal(expected, TextUtil.PercentOf(part, total));

    // ===== CompactTokenCount 更多 =====

    [Theory]
    [InlineData(10, "10")]
    [InlineData(100, "100")]
    [InlineData(999, "999")]
    [InlineData(1001, "1.0k")]
    [InlineData(1499, "1.5k")]
    [InlineData(1500, "1.5k")]
    [InlineData(999500, "999.5k")]
    [InlineData(1000000, "1.0M")]
    [InlineData(12345678, "12.3M")]
    [InlineData(long.MaxValue, "9223372036854.8M")] // long.MaxValue/1e6 的 F1 舍入
    public void CompactTokenCount_More(long n, string expected) =>
        Assert.Equal(expected, TextUtil.CompactTokenCount(n));

    // ===== SplitCommand 更多 =====

    [Theory]
    [InlineData("/retry", "/retry", "")]
    [InlineData("/stats", "/stats", "")]
    [InlineData("/export chat-1", "/export", "chat-1")]
    [InlineData("/thinking high", "/thinking", "high")]
    [InlineData("/load snap", "/load", "snap")]
    [InlineData("/diag", "/diag", "")]
    [InlineData("/config", "/config", "")]
    [InlineData("/history 10", "/history", "10")]
    public void SplitCommand_More(string line, string cmd, string rest)
    {
        var (c, r) = SplitCommand(line);
        Assert.Equal(cmd, c);
        Assert.Equal(rest, r);
    }

    // ===== SkipDirs 更多 =====

    [Theory]
    [InlineData("dist")]
    [InlineData("build")]
    [InlineData("out")]
    [InlineData("target")]
    [InlineData("temp")]
    [InlineData("logs")]
    [InlineData(".venv")]
    [InlineData(".vs")]
    [InlineData("__pycache__")]
    [InlineData(".tox")]
    [InlineData("DerivedData")]
    [InlineData(".idea")]
    [InlineData(".vscode")]
    public void SkipDirs_MoreSkipped(string dir) =>
        Assert.True(SkipDirs.IsSkipped(dir));

    [Theory]
    [InlineData("source")]
    [InlineData("Docs")]
    [InlineData("Assets")]
    [InlineData("Tools")]
    [InlineData("config")]
    public void SkipDirs_MoreNotSkipped(string dir) =>
        Assert.False(SkipDirs.IsSkipped(dir));

    // ===== LooksBinary 更多 =====

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("中文文本")]
    [InlineData("line1\nline2\n")]
    public void LooksBinary_Text_False(string s) =>
        Assert.False(SkipDirs.LooksBinary(s));

    [Theory]
    [InlineData("\u0000")]
    [InlineData("a\u0000")]
    [InlineData("abc\u0000def")]
    [InlineData("\u0000leading")]
    public void LooksBinary_Nul_True(string s) =>
        Assert.True(SkipDirs.LooksBinary(s));
}
