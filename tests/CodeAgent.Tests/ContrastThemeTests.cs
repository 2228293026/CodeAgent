using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 高对比配色主题（<c>CODEAGENT_THEME=contrast</c> / <c>contrast-light</c>）。
///
/// 回归点：此前只有深色/浅色两套，而两套的 <c>Muted</c> 都是 <c>DarkGray</c>。
/// DarkGray 在黑底上大约只有 3:1 的对比度，**低于正文 4.5:1 的可读性底线**——
/// 而用 <c>Muted</c> 渲染的恰恰是提示符、目录路径、工具耗时这些用户最常扫读的部分。
/// 高对比主题把次要文本提到 <c>Gray</c>，其余语义色沿用深色/浅色各自的亮/暗变体。
///
/// 仍然坚持「不猜」：高对比的两个值都必须**显式**写出背景。
/// </summary>
public class ContrastThemeTests
{
    private sealed class ThemeScope : IDisposable
    {
        private readonly Func<string, string?> _saved = SafeColor.ReadEnv;
        public ThemeScope(string? theme) =>
            SafeColor.ReadEnv = name => name == "CODEAGENT_THEME" ? theme : null;
        public void Dispose() => SafeColor.ReadEnv = _saved;
    }

    [Fact]
    public void ContrastIsRecognised()
    {
        Assert.Equal(SafeColor.ColorTheme.Contrast, SafeColor.ComputeTheme("contrast"));
        Assert.Equal(SafeColor.ColorTheme.Contrast, SafeColor.ComputeTheme("CONTRAST"));
        Assert.Equal(SafeColor.ColorTheme.Contrast, SafeColor.ComputeTheme("高对比"));
    }

    [Fact]
    public void ContrastLightIsRecognised()
    {
        Assert.Equal(SafeColor.ColorTheme.ContrastLight, SafeColor.ComputeTheme("contrast-light"));
        Assert.Equal(SafeColor.ColorTheme.ContrastLight, SafeColor.ComputeTheme("ContrastLight"));
        Assert.Equal(SafeColor.ColorTheme.ContrastLight, SafeColor.ComputeTheme("高对比浅色"));
    }

    [Fact]
    public void ContrastIsNotConfusedWithLight()
    {
        // "contrast" 绝不能被当成 light：方向猜错会直接导致亮色压亮色，白底白字
        Assert.NotEqual(SafeColor.ComputeTheme("light"), SafeColor.ComputeTheme("contrast"));
        Assert.NotEqual(SafeColor.ComputeTheme("light"), SafeColor.ComputeTheme("contrast-light"));
    }

    [Fact]
    public void UnknownSettingsStillFallBackToDark()
    {
        foreach (var setting in new[] { null, "", "  ", "solarized", "high-contrast", "42" })
            Assert.Equal(SafeColor.ColorTheme.Dark, SafeColor.ComputeTheme(setting));
    }

    [Fact]
    public void MutedIsBrighterThanTheDefaultInContrastTheme()
    {
        // 关键收益：次要文本的对比度必须**严格提高**，否则这轮等于什么都没做
        using (new ThemeScope(null))
            Assert.Equal(ConsoleColor.DarkGray, SafeColor.Muted);
        using (new ThemeScope("contrast"))
        {
            Assert.Equal(ConsoleColor.Gray, SafeColor.Muted);
            Assert.NotEqual(ConsoleColor.DarkGray, SafeColor.Muted);
        }
    }

    [Fact]
    public void EverySemanticColourResolvesInEveryTheme()
    {
        // 高对比主题不能只改 Muted 就完事：每个语义色都必须有确定取值，
        // 否则会出现「某个主题下 Accent 落回默认分支」这种静默不一致。
        foreach (var theme in new[] { null, "dark", "light", "contrast", "contrast-light" })
        {
            using var scope = new ThemeScope(theme);
            var colours = new[]
            {
                SafeColor.Muted, SafeColor.Accent, SafeColor.Link, SafeColor.Success,
                SafeColor.Danger, SafeColor.Warning, SafeColor.Emphasis,
            };
            foreach (var c in colours)
                Assert.True(Enum.IsDefined(c));
        }
    }

    [Fact]
    public void LightBackgroundsAlwaysUseDarkVariants()
    {
        // 浅色背景下用亮色会「洗掉」——这是可读性底线，高对比浅色同样适用
        foreach (var theme in new[] { "light", "contrast-light" })
        {
            using var scope = new ThemeScope(theme);
            Assert.Equal(ConsoleColor.DarkCyan, SafeColor.Accent);
            Assert.Equal(ConsoleColor.DarkBlue, SafeColor.Link);
            Assert.Equal(ConsoleColor.DarkGreen, SafeColor.Success);
            Assert.Equal(ConsoleColor.DarkRed, SafeColor.Danger);
            Assert.Equal(ConsoleColor.DarkYellow, SafeColor.Warning);
            Assert.Equal(ConsoleColor.Black, SafeColor.Emphasis);
        }
    }

    [Fact]
    public void DarkBackgroundsKeepBrightVariants()
    {
        foreach (var theme in new[] { null, "dark", "contrast" })
        {
            using var scope = new ThemeScope(theme);
            Assert.Equal(ConsoleColor.Cyan, SafeColor.Accent);
            Assert.Equal(ConsoleColor.Blue, SafeColor.Link);
            Assert.Equal(ConsoleColor.Green, SafeColor.Success);
            Assert.Equal(ConsoleColor.Red, SafeColor.Danger);
            Assert.Equal(ConsoleColor.Yellow, SafeColor.Warning);
            Assert.Equal(ConsoleColor.White, SafeColor.Emphasis);
        }
    }

    [Fact]
    public void ContrastThemeIsIndependentOfTheAsciiSwitch()
    {
        // 配色与字形是两件事：关掉颜色不改变主题判定，ASCII 退回也不改变配色
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = name => name switch
            {
                "CODEAGENT_THEME" => "contrast",
                "CODEAGENT_ASCII" => "1",
                _ => null,
            };
            Assert.Equal(SafeColor.ColorTheme.Contrast, SafeColor.Theme);
            Assert.True(SafeColor.Glyphs.AsciiEnabled);
            Assert.Equal(ConsoleColor.Gray, SafeColor.Muted);
        }
        finally { SafeColor.ReadEnv = saved; }
    }
}
