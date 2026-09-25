using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 历史列表行渲染：多行折叠 + 按显示宽度截断。
/// 回归点：旧实现按字符数截断 300，300 个汉字实际占 600 列，一行铺满整屏后角色前缀错位；
/// 且截断早于换行折叠，折叠后的实际长度会超出预算。
/// </summary>
public class HistoryLineTests
{
    /// <summary>角色前缀本身含 CJK，长度必须按字符数算（"  [用户] " = 7）。</summary>
    private const string UserPrefix = "  [用户] ";

    [Fact]
    public void FormatHistoryLine_NormalEntry_UsesRolePrefix()
    {
        Assert.Equal("  [助手] hello", Program.FormatHistoryLine("助手", "hello", false));
    }

    [Fact]
    public void FormatHistoryLine_ErrorEntry_HasMarker()
    {
        Assert.Equal("  [工具read_file] ❗ boom", Program.FormatHistoryLine("工具read_file", "boom", true));
    }

    [Fact]
    public void FormatHistoryLine_CollapsesNewlinesBeforeMeasuring()
    {
        Assert.Equal(
            "  [用户] 第一行 ⏎ 第二行",
            Program.FormatHistoryLine("用户", "第一行\r\n第二行", false));
    }

    [Fact]
    public void FormatHistoryLine_ExpandsTabs()
    {
        Assert.Equal("  [用户]     X", Program.FormatHistoryLine("用户", "\tX", false));
    }

    [Fact]
    public void FormatHistoryLine_TruncatesByDisplayWidthNotCharCount()
    {
        // 200 个汉字 = 400 列：按列截断到 30 列只剩 15 个字（150 字符）
        var line = Program.FormatHistoryLine("用户", new string('中', 200), false, 30);
        Assert.StartsWith(UserPrefix, line);
        var body = line[UserPrefix.Length..];
        Assert.True(TextUtil.DisplayWidth(body) <= 30);
        Assert.EndsWith("…", body, StringComparison.Ordinal);
        // 旧逻辑会保留全部 200 个字符（长度 200 < 300）
        Assert.True(line.Length < 60);
    }

    [Fact]
    public void FormatHistoryLine_ShortContentUnchanged()
    {
        Assert.Equal("  [助手] 短内容", Program.FormatHistoryLine("助手", "短内容", false));
    }

    [Fact]
    public void FormatHistoryLine_NeverSplitsSurrogatePair()
    {
        var line = Program.FormatHistoryLine("用户", string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 100)), false, 21);
        var body = line[UserPrefix.Length..];
        Assert.True(TextUtil.DisplayWidth(body) <= 21);
        // 不应出现落单的代理字符
        for (var i = 0; i < body.Length; i++)
        {
            if (char.IsHighSurrogate(body[i]))
            {
                Assert.True(i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]), "高代理后必须跟低代理");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(body[i]), "落单低代理字符");
            }
        }
    }

    [Fact]
    public void HistoryContentColumns_HasNamedConstant() => Assert.Equal(300, Program.HistoryContentColumns);
}
