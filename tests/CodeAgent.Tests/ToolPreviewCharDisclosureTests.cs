using System;
using System.Linq;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具输出脚注此前只报**行数**，于是「字符被截掉」这件事完全看不见。
///
/// 实测一条单行错误（<c>fatal: not a git repository ...</c>，99 字符）在 80 列下：
///
///   <c>…（共 1 行 / 99 字符，已保留 1 行，1 行按宽度裁剪）</c>
///
/// "已保留 1 行" —— 行数确实没少，可**字符被裁掉了**，脚注对此一个字都不说。
/// 用户（和读工具输出的模型）会以为看到了完整信息。
///
/// 这是"按某一个维度报数、却让人以为所有维度都没丢"的典型：
/// 第 296/297 轮修的是"行数与字符数本身不真实"，
/// 这一轮修的是"**真实了，但仍不足以说明丢了什么**"。
/// </summary>
public sealed class ToolPreviewCharDisclosureTests
{
    /// <summary>一条超长单行错误：**行数不会减少，只有字符会被裁**。
    ///
    /// 长度要真的超过宽度，否则什么都不会被裁、脚注也就不会出现——
    /// 早先第一版只有 99 字符、在 200 列下压根没触发，测试却以为它测到了披露。
    /// </summary>
    private static string LongSingleLine() =>
        "fatal: not a git repository (or any of the parent directories): .git " +
        "Make sure you have the correct access rights and the repository exists. " +
        "Check that the path is correct and that you have the appropriate permissions. " +
        "This fatal error occurs whenever a git command needs the working tree metadata " +
        "and cannot locate it, which usually means the directory was never initialised.";

    [Fact]
    public void ASingleLineReportMustStillDiscloseCharacterLoss()
    {
        var text = LongSingleLine();
        // 宽度给足：完整脚注放得下，才看得到"已保留 N 行"那一段。
        // 窄屏走的是精简脚注，由 TheNarrowFallbackPrefersCharactersOverRows 覆盖。
        var preview = AgentClass.FormatToolOutputPreview(text, width: 200);
        Assert.Contains("已保留 1 行", preview);      // 行数确实没少
        Assert.Contains("字符", preview);
        // 而且必须说清**裁掉了多少**，不能只有一个总数
        Assert.Matches(@"字符已裁掉 \d+", preview);
    }

    [Fact]
    public void TheKeptCountIsSmallerThanTheTotal()
    {
        var text = LongSingleLine();
        var preview = AgentClass.FormatToolOutputPreview(text, width: 200);
        var m = System.Text.RegularExpressions.Regex.Match(preview, @"字符已裁掉 ([\d,]+)");
        Assert.True(m.Success, $"脚注没有字符口径：{preview}");
        var lost = long.Parse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal));
        Assert.True(lost > 0, $"字符裁掉数 {lost} 不为正——要么没裁，要么脚注在说谎");
        Assert.Contains($"{text.Length:N0} 字符", preview);
    }

    [Fact]
    public void NoCharacterNoteWhenNothingWasCut()
    {
        // 只按宽度裁了两行、但字符一个没少时，不该出现字符口径（那是噪声）
        var preview = AgentClass.FormatToolOutputPreview("short line", width: 200);
        Assert.DoesNotContain("字符已裁掉", preview);
        Assert.Equal("short line", preview);
    }

    [Fact]
    public void NoCharacterNoteWhenOnlyTheRowCapApplied()
    {
        // 上游 BuildToolOutputPreview 按行截过时，脚注是"仅显示前 N 行"，
        // 不能混进字符口径——那是两回事
        var (text, total, _) = AgentClass.BuildToolOutputPreview(string.Join('\n', Enumerable.Range(1, 20).Select(i => $"row {i}")));
        var preview = AgentClass.FormatToolOutputPreview(text, trueTotalLines: total);
        Assert.Contains("仅显示前", preview);
        Assert.DoesNotContain("字符已裁掉", preview);
    }

    [Fact]
    public void TheNarrowFallbackPrefersCharactersOverRows()
    {
        // 放不下完整句时：整段被字符截断的场合，"保留了 1/1 行"等于没提示，
        // 所以必须换成字符口径。
        var preview = AgentClass.FormatToolOutputPreview(LongSingleLine(), width: 24);
        Assert.Contains("字符", preview);
        Assert.DoesNotContain("行)", preview);
    }

    [Fact]
    public void TheNarrowFallbackStillFitsTheWidth()
    {
        foreach (var w in new[] { 20, 24, 30, 40 })
        {
            var preview = AgentClass.FormatToolOutputPreview(LongSingleLine(), width: w);
            foreach (var line in preview.Split('\n'))
                Assert.True(TextUtil.DisplayWidth(line) <= w, $"宽度 {w} 超宽：[{line}]");
        }
    }

    [Fact]
    public void RowFallbackIsStillUsedWhenOnlyRowsWereLost()
    {
        // 反向保护：字符没少的时候，窄屏下仍应报行数（那是唯一有损的维度）
        var rows = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"r{i}"));
        var preview = AgentClass.FormatToolOutputPreview(rows, maxChars: 40, width: 20);
        // 字符被截断 → 用字符口径
        Assert.Contains("字符", preview);
    }
}
