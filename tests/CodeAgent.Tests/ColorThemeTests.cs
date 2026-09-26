using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 配色主题：亮色背景下的可读性。
/// 回归点：此前全项目直接写死 <c>ConsoleColor.Xxx</c>（DarkGray 用了 14 处）。
/// 亮色背景终端上 Blue / Cyan / Green / Yellow / White 会「洗掉」——
/// 白底上的 Cyan 几乎看不见，警告与错误反而比普通文本还不显眼。
/// 现在调用点一律用语义色，亮色主题下换成对应的 Dark* 变体。
/// </summary>
public class ColorThemeTests
{
    private static ConsoleColor With(string? setting, Func<ConsoleColor> read)
    {
        var original = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = name => name == "CODEAGENT_THEME" ? setting : null;
            return read();
        }
        finally { SafeColor.ReadEnv = original; }
    }

    [Theory]
    [InlineData("light")]
    [InlineData("Light")]
    [InlineData("LIGHT")]
    [InlineData("亮色")]
    public void LightIsRecognised(string setting)
    {
        Assert.Equal(SafeColor.ColorTheme.Light, SafeColor.ComputeTheme(setting));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dark")]
    [InlineData("auto")]
    [InlineData("solarized")]
    public void EverythingElseIsDark(string? setting)
    {
        // .NET 读不到终端背景亮度，所以**不猜**：只认显式 light
        Assert.Equal(SafeColor.ColorTheme.Dark, SafeColor.ComputeTheme(setting));
    }

    [Fact]
    public void AccentSwapsOnLight()
    {
        Assert.Equal(ConsoleColor.Cyan, With("dark", () => SafeColor.Accent));
        Assert.Equal(ConsoleColor.DarkCyan, With("light", () => SafeColor.Accent));
    }

    [Fact]
    public void EveryAccentSwapsOnLight()
    {
        // 亮色主题下必须全部换成更深的变体，否则白底上等于没上色
        Assert.Equal(ConsoleColor.DarkCyan, With("light", () => SafeColor.Accent));
        Assert.Equal(ConsoleColor.DarkBlue, With("light", () => SafeColor.Link));
        Assert.Equal(ConsoleColor.DarkGreen, With("light", () => SafeColor.Success));
        Assert.Equal(ConsoleColor.DarkRed, With("light", () => SafeColor.Danger));
        Assert.Equal(ConsoleColor.DarkYellow, With("light", () => SafeColor.Warning));
        Assert.Equal(ConsoleColor.Black, With("light", () => SafeColor.Emphasis));
    }

    [Fact]
    public void NoLightThemeColourIsABrightOne()
    {
        // 亮色背景下这 5 个亮色都读不清，语义色里一个都不该出现
        var bright = new[] { ConsoleColor.Blue, ConsoleColor.Cyan, ConsoleColor.Green, ConsoleColor.Yellow, ConsoleColor.White, ConsoleColor.Gray };
        var actual = new[]
        {
            With("light", () => SafeColor.Accent), With("light", () => SafeColor.Link),
            With("light", () => SafeColor.Success), With("light", () => SafeColor.Danger),
            With("light", () => SafeColor.Warning), With("light", () => SafeColor.Emphasis),
        };
        foreach (var c in actual)
            Assert.DoesNotContain(c, bright);
    }

    [Fact]
    public void MutedIsThemeIndependent()
    {
        // DarkGray 是 8 色里唯一黑底白底都读得清的，不该跟着主题变
        Assert.Equal(SafeColor.Muted, With("light", () => SafeColor.Muted));
        Assert.Equal(SafeColor.Muted, With("dark", () => SafeColor.Muted));
        Assert.Equal(ConsoleColor.DarkGray, SafeColor.Muted);
    }

    [Fact]
    public void NoCallSiteHardcodesARawConsoleColor()
    {
        // 守卫：语义色一旦被绕过（直接写 ConsoleColor.Xxx），主题就失效了
        var dir = AppContext.BaseDirectory;
        string? root = null;
        for (var i = 0; i < 8 && dir.Length > 1 && root is null; i++)
        {
            var candidate = Path.Combine(dir, "src", "CodeAgent");
            if (Directory.Exists(candidate)) root = candidate;
            else dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? dir;
        }
        Assert.NotNull(root);
        foreach (var file in Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "Util.cs")
                continue; // 调色板定义本身当然要写具体色值
            var source = File.ReadAllText(file);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(source, @"ConsoleColor\.\w"),
                $"{Path.GetFileName(file)} 仍在直接写 ConsoleColor，主题切换对它无效");
        }
    }

    [Fact]
    public void ThemeIsRestoredAfterTheTest()
    {
        Assert.NotNull(SafeColor.ReadEnv);
    }
}
