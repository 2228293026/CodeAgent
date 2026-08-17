using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>TextUtil / SkipDirs / LooksBinary 的边界与回归测试（补充基础测试未覆盖的边界）。</summary>
public class TextUtilEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-util-" + Guid.NewGuid().ToString("N"));

    public TextUtilEdgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    // ===== Truncate =====

    [Fact]
    public void Truncate_ExactLength_ReturnsAsIs() =>
        Assert.Equal("abc", TextUtil.Truncate("abc", 3));

    [Fact]
    public void Truncate_EmptyMax_ReturnsNoticeOnly() =>
        Assert.Equal("\n…(共 3 字符，已截断)", TextUtil.Truncate("abc", 0));

    [Fact]
    public void Truncate_NegativeMax_Throws()
    {
        // 文档化行为：负 max 会抛越界（调用方只传正数，防御由调用方负责）
        Assert.Throws<ArgumentOutOfRangeException>(() => TextUtil.Truncate("abc", -1));
    }

    [Fact]
    public void Truncate_EmptyText_ReturnsAsIs() =>
        Assert.Equal("", TextUtil.Truncate("", 10));

    // ===== TruncateLine =====

    [Fact]
    public void TruncateLine_TabExpandsToFourSpaces_BeforeCut()
    {
        // "\t" → 4 空格后再截断：5 字符上限下 "\tX" 变成 5 空格，不再截断
        Assert.Equal("    X", TextUtil.TruncateLine("\tX", 5));
    }

    [Fact]
    public void TruncateLine_ExactLength_NoEllipsis() =>
        Assert.Equal("12345", TextUtil.TruncateLine("12345", 5));

    [Fact]
    public void TruncateLine_OverLength_AppendsEllipsis() =>
        Assert.Equal("12345 …", TextUtil.TruncateLine("123456", 5));

    // ===== CountOccurrences =====

    [Fact]
    public void CountOccurrences_EmptySub_ReturnsZero() =>
        Assert.Equal(0, TextUtil.CountOccurrences("abc", ""));

    [Fact]
    public void CountOccurrences_EmptyText_ReturnsZero() =>
        Assert.Equal(0, TextUtil.CountOccurrences("", "a"));

    [Fact]
    public void CountOccurrences_SubLongerThanText_ReturnsZero() =>
        Assert.Equal(0, TextUtil.CountOccurrences("ab", "abc"));

    [Fact]
    public void CountOccurrences_AdjacentOccurrences_NonOverlapping()
    {
        // "aaa" 中数 "aa"：不重叠 → 1（第一个 aa 占位后剩一个 a）
        Assert.Equal(1, TextUtil.CountOccurrences("aaa", "aa"));
        Assert.Equal(2, TextUtil.CountOccurrences("aaaa", "aa"));
    }

    [Fact]
    public void CountOccurrences_ChineseSubstring_Counts()
    {
        Assert.Equal(2, TextUtil.CountOccurrences("压缩压缩历史", "压缩"));
    }

    // ===== CompactTokenCount =====

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1.0k")]
    [InlineData(999_999, "1000.0k")] // 边界：未到 1M 时的 k 展示（一致但略怪，文档化）
    [InlineData(1_000_000, "1.0M")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(-5, "-5")]
    [InlineData(-1500, "-1500")] // 负数不适用 k/M 格式化（token 数语义为非负，负数防御性原样输出）
    public void CompactTokenCount_Boundaries(long n, string expected) =>
        Assert.Equal(expected, TextUtil.CompactTokenCount(n));

    // ===== FormatSessionTime / FormatElapsed / FormatDuration =====

    [Fact]
    public void FormatSessionTime_Zero() =>
        Assert.Equal("0s", TextUtil.FormatSessionTime(TimeSpan.Zero));

    [Fact]
    public void FormatSessionTime_JustUnderMinute_RoundsToWholeSeconds() =>
        Assert.Equal("60s", TextUtil.FormatSessionTime(TimeSpan.FromSeconds(59.6)));

    [Fact]
    public void FormatSessionTime_ExactMinute_ShowsZeroSeconds() =>
        Assert.Equal("1m 0s", TextUtil.FormatSessionTime(TimeSpan.FromMinutes(1)));

    [Fact]
    public void FormatElapsed_JustOverMinute_WholeSeconds() =>
        Assert.Equal("1m 0s", TextUtil.FormatElapsed(TimeSpan.FromSeconds(60)));

    [Fact]
    public void FormatElapsed_SubMinute_OneDecimal() =>
        Assert.Equal("22.5s", TextUtil.FormatElapsed(TimeSpan.FromSeconds(22.5)));

    [Fact]
    public void FormatDuration_SubSecond_UsesMilliseconds() =>
        Assert.Equal("250ms", TextUtil.FormatDuration(TimeSpan.FromMilliseconds(250)));

    [Fact]
    public void FormatDuration_ExactSecond_OneDecimal() =>
        Assert.Equal("1.0s", TextUtil.FormatDuration(TimeSpan.FromSeconds(1)));

    [Fact]
    public void FormatDuration_JustUnderSecond_Milliseconds() =>
        Assert.Equal("999ms", TextUtil.FormatDuration(TimeSpan.FromSeconds(0.999)));

    // ===== ShortModelName =====

    [Fact]
    public void ShortModelName_NoSlash_ReturnsAsIs() =>
        Assert.Equal("gpt-4o", TextUtil.ShortModelName("gpt-4o"));

    [Fact]
    public void ShortModelName_SlashWithShortSegment_KeepsFull()
    {
        // 末段 <5 字符不具辨识度：保留完整名
        Assert.Equal("org/x", TextUtil.ShortModelName("org/x"));
    }

    [Fact]
    public void ShortModelName_EmptyString() =>
        Assert.Equal("", TextUtil.ShortModelName(""));

    [Fact]
    public void ShortModelName_TrailingSlash_KeepsFull() =>
        Assert.Equal("a/", TextUtil.ShortModelName("a/"));

    // ===== PercentOf =====

    [Fact]
    public void PercentOf_NegativePart_ClampsZero() =>
        Assert.Equal(0, TextUtil.PercentOf(-10, 100));

    [Fact]
    public void PercentOf_OverHundred_ClampsHundred() =>
        Assert.Equal(100, TextUtil.PercentOf(150, 100));

    [Fact]
    public void PercentOf_ZeroTotal_ReturnsZero() =>
        Assert.Equal(0, TextUtil.PercentOf(50, 0));

    [Fact]
    public void PercentOf_NegativeTotal_ReturnsZero() =>
        Assert.Equal(0, TextUtil.PercentOf(50, -1));

    [Fact]
    public void PercentOf_ZeroPart_ReturnsZero() =>
        Assert.Equal(0, TextUtil.PercentOf(0, 100));

    [Fact]
    public void PercentOf_RoundsDown() =>
        Assert.Equal(33, TextUtil.PercentOf(33, 100));

    [Fact]
    public void PercentOf_LongMax_ClampsHundred() =>
        Assert.Equal(100, TextUtil.PercentOf(long.MaxValue, 1));

    // ===== SkipDirs =====

    [Theory]
    [InlineData(".git")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".codeagent")]
    [InlineData("Library")]
    public void SkipDirs_IsSkipped_CommonDirs(string dir) =>
        Assert.True(SkipDirs.IsSkipped(dir));

    [Fact]
    public void SkipDirs_IsSkipped_CaseInsensitive()
    {
        Assert.True(SkipDirs.IsSkipped("BIN"));
        Assert.True(SkipDirs.IsSkipped(".GIT"));
    }

    [Fact]
    public void SkipDirs_IsSkipped_NotSkipped()
    {
        Assert.False(SkipDirs.IsSkipped("src"));
        Assert.False(SkipDirs.IsSkipped(""));
        Assert.False(SkipDirs.IsSkipped("source"));
    }

    [Fact]
    public void SkipDirs_EnumerateFilesPruned_SkipsBin()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.cs"), "a");
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "build.cs"), "b");

        var files = SkipDirs.EnumerateFilesPruned(_dir).ToList();
        Assert.Contains(files, f => f.EndsWith("a.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.Contains("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public void SkipDirs_EnumerateFilesPruned_MissingRoot_YieldsNothing()
    {
        Assert.Empty(SkipDirs.EnumerateFilesPruned(Path.Combine(_dir, "nope")));
    }

    // ===== LooksBinary =====

    [Fact]
    public void LooksBinary_Empty_ReturnsFalse() =>
        Assert.False(SkipDirs.LooksBinary(""));

    [Fact]
    public void LooksBinary_WithNul_ReturnsTrue() =>
        Assert.True(SkipDirs.LooksBinary("ab\u0000cd"));

    [Fact]
    public void LooksBinary_PlainText_ReturnsFalse() =>
        Assert.False(SkipDirs.LooksBinary("普通文本 content"));

    [Fact]
    public void LooksBinary_OnlyScansFirst8192Chars()
    {
        // NUL 在 8192 之后：扫描窗口外，视为文本（窗口限制）
        var s = new string('x', 9000) + "\u0000tail";
        Assert.False(SkipDirs.LooksBinary(s));
    }

    [Fact]
    public void LooksBinary_NulWithinFirst8192_ReturnsTrue()
    {
        var s = new string('x', 100) + "\u0000" + new string('y', 100);
        Assert.True(SkipDirs.LooksBinary(s));
    }
}
