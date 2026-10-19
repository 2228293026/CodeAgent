using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 提示符降级：超宽时按优先级丢内容，而不是让整个切换菜单退化成追加模式。
/// 优先级固定为「模式 &gt; 模型 &gt; 目录」——模式永远最重要。
/// </summary>
public class PromptDegradeTests
{
    // 终止符取自字形表：ASCII 模式下是 >，Unicode 下是 ❯。
    // 断言里写死任何一个，另一个模式就必然红。
    // 用**属性**而不是 static readonly 字段：后者的值在类型初始化时就定死，
    // 之后切换 CODEAGENT_ASCII 不会生效——读到的是上一个模式留下的值。
    private static string chev => SafeColor.Glyphs.PromptChevron;

    [Fact]
    public void BuildPromptText_FullFormWhenItFits()
    {
        Assert.Equal($"[code|gpt-5] proj{chev} ", Program.BuildPromptText("code", "gpt-5", "proj", 80));
    }

    [Fact]
    public void BuildPromptText_UnknownWidthKeepsFullForm()
    {
        var full = $"[code|{new string('m', 200)}] {new string('d', 80)}{chev} ";
        Assert.Equal(full, Program.BuildPromptText("code", new string('m', 200), new string('d', 80), 0));
    }

    [Fact]
    public void BuildPromptText_DropsModelBeforeDirectory()
    {
        // 模型名很长但目录很短：应该丢模型、保留目录
        var text = Program.BuildPromptText("code", new string('m', 120), "proj", 60);
        Assert.DoesNotContain(new string('m', 10), text);
        Assert.Contains("proj", text);
    }

    [Fact]
    public void BuildPromptText_NeverDropsMode()
    {
        foreach (var width in new[] { 10, 14, 20, 30, 40 })
        {
            var text = Program.BuildPromptText("代码审查", new string('m', 100), new string('d', 100), width);
            Assert.StartsWith("[", text);
            Assert.Contains("]", text);
        }
    }

    [Fact]
    public void BuildPromptText_KeepsModeTextAtUsableWidths()
    {
        foreach (var width in new[] { 30, 40, 60, 80 })
        {
            var text = Program.BuildPromptText("代码审查", new string('m', 100), new string('d', 100), width);
            Assert.Contains("代码审查", text);
        }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void BuildPromptText_NeverExceedsWidth(int width)
    {
        var text = Program.BuildPromptText("代码审查模式", new string('m', 100), new string('d', 100), width);
        Assert.True(TextUtil.DisplayWidth(text) <= width, $"宽度 {width} 溢出: {text}（{TextUtil.DisplayWidth(text)} 列）");
    }

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(120)]
    public void BuildPromptText_WideEnoughFitsOneRowWithMargin(int width)
    {
        // 余量还够时，降级后的提示符应能通过 PromptFitsOneRow（切换菜单可原地覆盖）
        var text = Program.BuildPromptText("代码审查模式", new string('m', 100), new string('d', 100), width);
        Assert.True(Program.PromptFitsOneRow(text, width), text);
    }

    [Fact]
    public void BuildPromptText_TruncatedDirectoryIsMarked()
    {
        var text = Program.BuildPromptText("code", new string('m', 100), new string('d', 100), 30);
        Assert.Contains("…", text);
        Assert.EndsWith($"{SafeColor.Glyphs.PromptChevron} ", text);
    }

    [Fact]
    public void BuildPromptText_DegradedPromptPassesOneRowCheck()
    {
        // 降级的意义就在于让 PromptFitsOneRow 回到 true（切换菜单能原地覆盖）
        var text = Program.BuildPromptText("code", new string('m', 100), new string('d', 100), 40);
        Assert.True(Program.PromptFitsOneRow(text, 40), text);
    }
}
