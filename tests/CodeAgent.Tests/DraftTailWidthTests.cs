using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入行草稿的宽度预算。
/// 回归点：只有历史浏览态（FormatHistoryRow）有整行预算，
/// 搜索态的四个分支与普通态都直接 `prefix + draft` 拼接。
/// 搜索态前面还多了「(搜索) `查询串` ✔/未命中」这一大截前缀，
/// 窄屏上草稿必然被挤出屏幕——而草稿是**用户自己正在编辑的内容**。
/// </summary>
public class DraftTailWidthTests
{
    private const string Prompt = "> ";
    private const string Draft = "这是一段很长的草稿内容需要被裁剪才不会溢出屏幕";

    [Fact]
    public void UnknownWidthIsUnchanged()
    {
        // 宽度未知 = 不裁剪（既有约定，必须逐字不变）
        Assert.Equal($"{Prompt} (搜索) {Draft}",
            InputLine.FormatInputText(Prompt, true, "", false, -1, 0, Draft, windowWidth: 0));
        Assert.Equal(Prompt + Draft,
            InputLine.FormatInputText(Prompt, false, "", false, -1, 0, Draft, windowWidth: 0));
    }

    [Fact]
    public void SearchWithEmptyQueryIsBounded()
    {
        var line = InputLine.FormatInputText(Prompt, true, "", false, -1, 0, Draft, windowWidth: 40);
        Assert.True(TextUtil.DisplayWidth(line) <= 40, line);
    }

    [Fact]
    public void SearchWithQueryIsBounded()
    {
        var line = InputLine.FormatInputText(Prompt, true, "关键词", true, -1, 0, Draft, windowWidth: 40);
        Assert.True(TextUtil.DisplayWidth(line) <= 40, line);
    }

    [Fact]
    public void SearchWithNoQueryBudgetIsBounded()
    {
        // 查询串被整段省略、只剩「未命中」的那条分支同样要有预算
        var line = InputLine.FormatInputText(Prompt, true, "很长的查询关键词", false, -1, 0, Draft, windowWidth: 40);
        Assert.True(TextUtil.DisplayWidth(line) <= 40, line);
    }

    [Fact]
    public void PlainRowIsBounded()
    {
        var line = InputLine.FormatInputText(Prompt, false, "", false, -1, 0, Draft, windowWidth: 30);
        Assert.True(TextUtil.DisplayWidth(line) <= 30, line);
    }

    [Fact]
    public void NeverExceedsTheWidthAcrossStates()
    {
        // 只在**前缀本身放得下**的宽度上断言整行不超宽：前缀就超宽时行宽反正已超，
        // 这时能保住的是草稿（见 PrefixFillsTheLineStillKeepsTheDraft）。
        foreach (var width in new[] { 60, 80 })
        {
            var lines = new[]
            {
                InputLine.FormatInputText(Prompt, false, "", false, -1, 0, Draft, windowWidth: width),
                InputLine.FormatInputText(Prompt, true, "", false, -1, 0, Draft, windowWidth: width),
                InputLine.FormatInputText(Prompt, true, "关键词", true, -1, 0, Draft, windowWidth: width),
                InputLine.FormatInputText(Prompt, true, "关键词", false, -1, 0, Draft, windowWidth: width),
                InputLine.FormatInputText(Prompt, false, "", false, 1, 9, Draft, windowWidth: width),
            };
            foreach (var line in lines)
                Assert.True(TextUtil.DisplayWidth(line) <= width, $"预算 {width}：{line}");
        }
    }

    [Fact]
    public void TruncatedDraftIsMarked()
    {
        var line = InputLine.FormatInputText(Prompt, true, "", false, -1, 0, Draft, windowWidth: 40);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void PrefixFillsTheLineStillKeepsTheDraft()
    {
        // 关键不变式：草稿是用户正在编辑的内容，绝不能被静默丢弃——
        // 悄悄删掉比超宽危险得多（用户会以为输入被清空了），
        // 而且此时行宽反正已超，删草稿也换不回预算。
        Assert.Equal("前缀草稿", InputLine.DraftTail("前缀", "草稿", 4));
        Assert.Equal("前缀草稿", InputLine.DraftTail("前缀", "草稿", 2));
    }

    [Fact]
    public void ZeroWidthIsUnchanged()
    {
        Assert.Equal("前缀草稿", InputLine.DraftTail("前缀", "草稿", 0));
    }

    [Fact]
    public void CjkDraftIsMeasuredByDisplayWidth()
    {
        var line = InputLine.DraftTail("> ", Draft, 24);
        Assert.True(TextUtil.DisplayWidth(line) <= 24, line);
    }
}
