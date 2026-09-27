using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 配置向导的提问渲染。
///
/// 此前是 <c>output.Write($"{prompt} [{def}]: ")</c>：一行放不下就折行，
/// 而折行点常常正好落在末尾的 <c>": "</c> 之前——用户看到的是一个**没有提示的行**，
/// 在那儿敲完内容，回车后才发现填错了一项。
/// </summary>
public sealed class SetupAskPromptTests
{
    [Fact]
    public void Prompt_ShowsTheFieldAndTheDefaultOnOneLine()
    {
        var p = SetupWizard.FormatAskPrompt("API 地址", "https://api.example.com/v1", 120);
        Assert.Contains("API 地址", p);
        Assert.Contains("https://api.example.com/v1", p);
        Assert.DoesNotContain("\n", p);
        Assert.EndsWith(": ", p); // 冒号必须待在有提示的那一行末尾
    }

    [Fact]
    public void Prompt_WithoutDefaultHasNoBrackets()
    {
        var p = SetupWizard.FormatAskPrompt("API 地址（必填）", null, 120);
        Assert.Contains("API 地址（必填）", p);
        Assert.DoesNotContain("[", p);
    }

    [Fact]
    public void Prompt_EmptyDefaultIsTreatedAsNoDefault()
    {
        var p = SetupWizard.FormatAskPrompt("字段", "", 120);
        Assert.DoesNotContain("[", p);
    }

    [Fact]
    public void Prompt_EveryLineFitsTheWidth()
    {
        var url = "https://a-very-long-provider-hostname.example.com/anthropic/v1";
        var model = "some-extremely-long-model-identifier-2024-05-01-preview";
        foreach (var (prompt, def) in new[]
        {
            ("API 地址", url),
            ("模型", model),
            ("一个相当长的中文字段名提示", "一个相当长的中文默认值内容"),
            ("x", new string('z', 300)),
        })
        {
            foreach (var w in new[] { 8, 12, 20, 30, 40, 60, 80, 120 })
            {
                var text = SetupWizard.FormatAskPrompt(prompt, def, w);
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                    Assert.True(TextUtil.DisplayWidth(line) <= w,
                        $"w={w} 超宽 {TextUtil.DisplayWidth(line)}: {line}");
            }
        }
    }

    [Fact]
    public void Prompt_NeverOrphansTheColonOntoAnEmptyLine()
    {
        // 核心不变量：带 ": " 的那一行**必须**有内容，不能是个空行
        var url = "https://a-very-long-provider-hostname.example.com/anthropic/v1";
        foreach (var w in new[] { 8, 12, 20, 30, 40, 60 })
        {
            var text = SetupWizard.FormatAskPrompt("API 地址", url, w);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var last = lines[^1];
            Assert.False(string.IsNullOrWhiteSpace(last), $"w={w} 末行是空的：{text}");
            Assert.EndsWith(": ", last);
        }
    }

    [Fact]
    public void Prompt_NeverTruncatesTheDefault()
    {
        // 用户会把半条 URL 当成真值存进配置——所以放不下时**整段不显示**，不截断
        var url = "https://a-very-long-provider-hostname.example.com/anthropic/v1";
        foreach (var w in new[] { 20, 40, 60, 80, 120, 200 })
        {
            var text = SetupWizard.FormatAskPrompt("API 地址", url, w);
            // 要么完整出现，要么一个字都不出现
            if (!text.Contains(url))
                Assert.DoesNotContain("默认:", text);
        }
    }

    [Fact]
    public void Prompt_DropsTheDefaultDisplayRatherThanTruncatingIt()
    {
        // 极窄时默认值整段消失——但回车仍会用它（行为不变，只是看不见了）
        var url = "https://a-very-long-provider-hostname.example.com/anthropic/v1";
        var narrow = SetupWizard.FormatAskPrompt("API 地址", url, 20);
        Assert.DoesNotContain(url, narrow);
        Assert.DoesNotContain("默认:", narrow);
        Assert.DoesNotContain("…", narrow); // 关键：没有任何被截断的残片
    }

    [Fact]
    public void Prompt_FieldNameMayBeTrimmedButTheLineNeverEmpties()
    {
        // 字段名是标签，被收口可以接受；提示行不能空
        var field = "一个相当长的中文字段名提示内容";
        foreach (var w in new[] { 4, 8, 12, 20, 40, 80, 200 })
        {
            var text = SetupWizard.FormatAskPrompt(field, "默认值", w);
            var last = text.Replace("\r\n", "\n").Split('\n')[^1];
            Assert.False(string.IsNullOrWhiteSpace(last), $"w={w} 末行为空");
            Assert.EndsWith(": ", last);
        }
    }

    [Fact]
    public void Prompt_UnknownWidthStaysOnOneLine()
    {
        var p = SetupWizard.FormatAskPrompt("API 地址", "https://example.com/v1", 0);
        Assert.DoesNotContain("\n", p);
    }

    [Fact]
    public void Prompt_StaysUsableAtAbsurdlyNarrowWidths()
    {
        // 1~2 列是终端抖动的读数，但渲染不该因此崩或产生空行
        foreach (var w in new[] { 1, 2, 3, 4 })
        {
            var text = SetupWizard.FormatAskPrompt("字段", "默认值", w);
            Assert.NotNull(text);
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                Assert.False(string.IsNullOrWhiteSpace(line), $"w={w} 产生了空行");
        }
    }

    [Fact]
    public void Prompt_UsesNoHardcodedDecoration()
    {
        var p = SetupWizard.FormatAskPrompt("字段", "默认值", 120);
        Assert.DoesNotContain('*', p);
        Assert.DoesNotContain('#', p);
    }
}
