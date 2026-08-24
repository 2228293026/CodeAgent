using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class TextUtilFormattingTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1937, "1.9k")]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(1_234_567, "1.2M")]
    public void CompactTokenCount_Formats(long n, string expected) =>
        Assert.Equal(expected, TextUtil.CompactTokenCount(n));

    [Fact]
    public void FormatSessionTime_UnderMinute_WholeSeconds() =>
        Assert.Equal("22s", TextUtil.FormatSessionTime(TimeSpan.FromSeconds(22.3)));

    [Fact]
    public void FormatSessionTime_OverMinute_MinutesAndSeconds() =>
        Assert.Equal("2m 5s", TextUtil.FormatSessionTime(TimeSpan.FromSeconds(125)));

    [Fact]
    public void FormatElapsed_UnderMinute_OneDecimal() =>
        Assert.Equal("22.7s", TextUtil.FormatElapsed(TimeSpan.FromSeconds(22.7)));

    [Fact]
    public void FormatElapsed_OverMinute_MinutesAndSeconds() =>
        Assert.Equal("2m 5s", TextUtil.FormatElapsed(TimeSpan.FromSeconds(125)));

    [Fact]
    public void FormatDuration_UnderSecond_Milliseconds() =>
        Assert.Equal("850ms", TextUtil.FormatDuration(TimeSpan.FromMilliseconds(850)));

    [Fact]
    public void FormatDuration_OverSecond_OneDecimal() =>
        Assert.Equal("1.5s", TextUtil.FormatDuration(TimeSpan.FromSeconds(1.5)));

    [Theory]
    [InlineData("deepseek-chat", "deepseek-chat")]                     // 无斜杠：原样
    [InlineData("poolside/laguna-s-2.1:free", "laguna-s-2.1:free")]    // 末段够长：取末段
    [InlineData("provider/free", "provider/free")]                     // 末段过短：保留完整名
    [InlineData("a/b/c", "a/b/c")]                                     // 末段 1 字符：保留完整名
    [InlineData("x/y/long-model-name", "long-model-name")]             // 多层路径取最后一段
    public void ShortModelName_SelectsReadableSegment(string model, string expected) =>
        Assert.Equal(expected, TextUtil.ShortModelName(model));

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(50, 100, 50)]
    [InlineData(100, 100, 100)]
    [InlineData(150, 100, 100)]   // 超出部分封顶 100（回归：曾显示 150%）
    [InlineData(500_000, 1_000_000, 50)]
    [InlineData(1_500_000, 1_000_000, 100)] // 回归：1.5M/1M 曾显示 150%
    [InlineData(10, 0, 0)]        // total ≤ 0：返回 0
    [InlineData(0, 0, 0)]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 66)]
    [InlineData(999, 1000, 99)]
    [InlineData(9999, 10000, 99)]
    [InlineData(1, 10000, 0)]     // 不足 1%：向下取整为 0
    [InlineData(-5, 100, 0)]      // part 负数：按 0 处理
    public void PercentOf_ClampsToHundred(long part, long total, int expected) =>
        Assert.Equal(expected, TextUtil.PercentOf(part, total));

    [Theory]
    [InlineData("short", 100, "short")]
    [InlineData("short", 5, "short")]
    [InlineData("short", 4, "shor\n…(共 5 字符，已截断)")]
    [InlineData("", 10, "")]
    [InlineData("hello world", 5, "hello\n…(共 11 字符，已截断)")]
    [InlineData("abcdef", 6, "abcdef")] // 恰好等于上限不截断
    public void Truncate_CutsWithNotice(string input, int max, string expected) =>
        Assert.Equal(expected, TextUtil.Truncate(input, max));

    [Theory]
    [InlineData("abc", 10, "abc")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abc", 2, "ab …")]
    [InlineData("", 5, "")]
    [InlineData("a\tb", 10, "a    b")]      // tab 展开为 4 空格
    [InlineData("a\tb", 4, "a    …")]        // 展开后 "a    b"(6) 截断前 4 字符 "a   " + " …"
    [InlineData("long line here", 4, "long …")]
    public void TruncateLine_ExpandsTabsAndCuts(string input, int max, string expected) =>
        Assert.Equal(expected, TextUtil.TruncateLine(input, max));

    [Theory]
    [InlineData("abcabcabc", "abc", 3)]
    [InlineData("aaaa", "aa", 2)]           // 不重叠计数：2 而非 3
    [InlineData("", "x", 0)]
    [InlineData("hello", "", 0)]            // 空子串：返回 0（曾死循环挂起测试主机）
    [InlineData("hello hello", "hello", 2)]
    [InlineData("Hello HELLO hello", "hello", 1)] // 区分大小写（Ordinal）
    [InlineData("xyz", "abc", 0)]
    public void CountOccurrences_CountsNonOverlapping(string text, string sub, int expected) =>
        Assert.Equal(expected, TextUtil.CountOccurrences(text, sub));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1.0k")]
    [InlineData(1937, "1.9k")]
    [InlineData(1999, "2.0k")]
    [InlineData(999_999, "1000.0k")]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(2_000_000, "2.0M")]
    [InlineData(-5, "-5")]
    public void CompactTokenCount_AdditionalBounds(long n, string expected) =>
        Assert.Equal(expected, TextUtil.CompactTokenCount(n));

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(0.4, "0s")]          // 不足 1 秒取整为 0s
    [InlineData(22.3, "22s")]
    [InlineData(59.9, "60s")]
    [InlineData(60, "1m 0s")]
    [InlineData(125, "2m 5s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(3600, "1h 0m")]      // 小时档：多小时会话不再显示 60m+ 的超长分钟数
    [InlineData(3661, "1h 1m")]
    public void FormatSessionTime_VariousDurations(double seconds, string expected) =>
        Assert.Equal(expected, TextUtil.FormatSessionTime(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, "刚刚")]
    [InlineData(-30, "刚刚")]          // 未来时间（时钟回拨）：按刚刚，不显示负数
    [InlineData(59, "刚刚")]
    [InlineData(60, "1 分钟前")]
    [InlineData(3599, "59 分钟前")]
    [InlineData(3600, "1 小时前")]
    [InlineData(86399, "23 小时前")]
    [InlineData(86400, "1 天前")]
    [InlineData(30 * 86400, "30 天前")]
    [InlineData(31 * 86400, "5月15日")]        // 超 30 天：回退日期（同年不带年份）
    [InlineData(400 * 86400, "2025年5月11日")] // 跨年：带年份
    public void RelativeTime_Cases(double secondsAgo, string expected)
    {
        // 固定基准 2026-06-15（年中，避免 31 天前必然跨年）：秒数前的时间换算应得到预期文本
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var utc = now.AddSeconds(-secondsAgo);
        Assert.Equal(expected, TextUtil.RelativeTime(utc, now));
    }

    [Theory]
    [InlineData(0, "0.0s")]
    [InlineData(22.7, "22.7s")]
    [InlineData(59.95, "60.0s")]
    [InlineData(60, "1m 0s")]
    [InlineData(125, "2m 5s")]
    [InlineData(3661.4, "61m 1s")]
    public void FormatElapsed_VariousDurations(double seconds, string expected) =>
        Assert.Equal(expected, TextUtil.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(850, "850ms")]
    [InlineData(999, "999ms")]
    [InlineData(1000, "1.0s")]
    [InlineData(1500, "1.5s")]
    [InlineData(123456, "123.5s")]
    public void FormatDuration_VariousDurations(double ms, string expected) =>
        Assert.Equal(expected, TextUtil.FormatDuration(TimeSpan.FromMilliseconds(ms)));

    [Theory]
    [InlineData("deepseek-chat", "deepseek-chat")]                     // 无斜杠：原样
    [InlineData("poolside/laguna-s-2.1:free", "laguna-s-2.1:free")]    // 末段够长：取末段
    [InlineData("provider/free", "provider/free")]                     // 末段过短：保留完整名
    [InlineData("a/b/c", "a/b/c")]                                     // 末段 1 字符：保留完整名
    [InlineData("x/y/long-model-name", "long-model-name")]             // 多层路径取最后一段
    [InlineData("", "")]                                               // 空串
    [InlineData("gpt-4o", "gpt-4o")]
    [InlineData("openai/gpt-4o-mini", "gpt-4o-mini")]
    [InlineData("a/12345", "12345")]                                   // 末段 5 字符恰好满足
    [InlineData("a/1234", "a/1234")]                                   // 末段 4 字符过短
    public void ShortModelName_AdditionalBounds(string model, string expected) =>
        Assert.Equal(expected, TextUtil.ShortModelName(model));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(5_368_709_120, "5.0 GB")] // 5 * 1024^3：跨 GB 边界
    public void FormatBytes_HumanReadable(long bytes, string expected) =>
        Assert.Equal(expected, TextUtil.FormatBytes(bytes));
}
