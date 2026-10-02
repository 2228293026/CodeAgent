using System;
using System.Linq;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 「仅显示前 N 行」这条脚注此前**只字不提字符**——而丢掉 32 行就是丢掉上千字符。
///
/// 第 308 轮修的是"按宽度裁掉的字符没披露"，却漏掉了更常见的这一条路径：
/// <c>BuildToolOutputPreview</c> 按行截到 8 行之后，走的是
/// <c>FormatToolOutputPreview</c> 里"文本本身没再被裁"的分支。
///
/// 实测一条 40 行 / 2,310 字符的输出，脚注是：
///   <c>…（共 40 行，仅显示前 8 行）</c>
/// ——1,855 个字符一个字都没提。看到"仅显示前 8 行"的人完全会以为
/// 剩下的 8 行差不多就是全部。
///
/// 这条把**每条截断路径**都必须同时报出行与字符两个口径钉住。
/// </summary>
public sealed class RowCapFootnoteShowsCharsTests
{
    private static string LongOutput(int rows) =>
        string.Join('\n', Enumerable.Range(1, rows).Select(i => new string('x', 50) + $" row {i}"));

    [Fact]
    public void TheRowCapFootnoteAlsoReportsCharacters()
    {
        var full = LongOutput(40);
        var (text, total, chars) = AgentClass.BuildToolOutputPreview(full);
        var preview = AgentClass.FormatToolOutputPreview(text, trueTotalLines: total, trueTotalChars: chars);

        Assert.Contains("共 40 行", preview);
        Assert.Contains("仅显示前 8 行", preview);
        Assert.Contains($"{full.Length:N0} 字符", preview);      // 真实总字符
        Assert.Contains("已丢弃", preview);                       // 丢了多少
    }

    [Fact]
    public void TheDiscardedCountIsTheRealDifference()
    {
        var full = LongOutput(40);
        var (text, total, chars) = AgentClass.BuildToolOutputPreview(full);
        Assert.Equal(full.Length, chars);
        var preview = AgentClass.FormatToolOutputPreview(text, trueTotalLines: total, trueTotalChars: chars);

        var m = System.Text.RegularExpressions.Regex.Match(preview, @"已丢弃 ([\d,]+) 字符");
        Assert.True(m.Success, $"脚注没有丢弃字符数：{preview}");
        var gone = long.Parse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal));
        Assert.Equal(full.Length - text.Length, gone);
        Assert.True(gone > 0);
    }

    [Fact]
    public void NothingIsDiscardedWhenNothingIsDropped()
    {
        var (text, total, chars) = AgentClass.BuildToolOutputPreview("one\ntwo");
        var preview = AgentClass.FormatToolOutputPreview(text, trueTotalLines: total, trueTotalChars: chars);
        Assert.Equal("one\ntwo", preview);          // 原样返回，脚注整个不存在
        Assert.DoesNotContain("已丢弃", preview);
        Assert.DoesNotContain("字符", preview);
    }

    [Fact]
    public void WithoutACharTotalItMustNotClaimZeroCharacters()
    {
        // 没传 trueTotalChars 时**不能**写「/ 0 字符」——那比不写更糟：
        // 它会断言"整个输出 0 字符"，而屏幕上明明摆着 8 行内容。
        var full = LongOutput(40);
        var (text, total, _) = AgentClass.BuildToolOutputPreview(full);
        var preview = AgentClass.FormatToolOutputPreview(text, trueTotalLines: total);
        Assert.DoesNotContain("0 字符", preview);
        Assert.Contains("共 40 行", preview);
        Assert.Contains("仅显示前 8 行", preview);
    }

    [Fact]
    public void EveryCappingPathReportsCharacters()
    {
        // 三条截断路径，**每条**都必须披露字符丢失：
        //   ① 上游按行截（BuildToolOutputPreview）
        //   ② 字符预算截
        //   ③ 按宽度裁
        //
        // 行维度**不能**在这里一起断言：窄屏下脚注会退化成单一口径的精简形式
        // （只留最关键的那个事实），那是有意为之，轮不到这条测试管。
        var full = LongOutput(40);
        var (text, total, chars) = AgentClass.BuildToolOutputPreview(full);

        var viaRows = AgentClass.FormatToolOutputPreview(text, trueTotalLines: total, trueTotalChars: chars);
        var viaChars = AgentClass.FormatToolOutputPreview(full, maxChars: 400, width: 0);
        var viaWidth = AgentClass.FormatToolOutputPreview(LongSingleRow(), 800, 40);

        Assert.Contains("字符", viaRows);
        Assert.Contains("字符", viaChars);
        Assert.Contains("字符", viaWidth);
    }

    private static string LongSingleRow() =>
        "fatal: not a git repository (or any of the parent directories): .git " +
        "Make sure you have the correct access rights and the repository exists. " +
        "Check that the path is correct and that you have the appropriate permissions. " +
        "This fatal error occurs whenever a git command needs the working tree metadata " +
        "and cannot locate it, which usually means the directory was never initialised.";

    [Fact]
    public void TheRowCapBranchIsNotSilentlySharedWithTheOtherOne()
    {
        // 这两条分支必须**各自**给出脚注。早先把行截路径写成三元表达式返回 text，
        // 一旦条件写反就什么都不显示——所以显式钉住两个分支都有输出。
        var full = LongOutput(40);
        var (text, total, chars) = AgentClass.BuildToolOutputPreview(full);
        Assert.Contains("仅显示前", AgentClass.FormatToolOutputPreview(text, trueTotalLines: total, trueTotalChars: chars));
        Assert.Contains("已保留", AgentClass.FormatToolOutputPreview(full, maxChars: 300, width: 0));
    }
}
