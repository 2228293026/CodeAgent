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
}
