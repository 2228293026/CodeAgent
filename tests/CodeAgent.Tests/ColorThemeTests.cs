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
        // 守卫：语义色一旦被绕过（直接写 ConsoleColor.Xxx），主题就失效了。
        // 定位源码目录时**校验内容**：光看「目录存在」不够——向上走几层可能撞上
        // 别处的 src/CodeAgent（测试会往临时目录写 Program.cs），那样这个守卫会
        // 扫一个空目录、静悄悄地「通过」，比没有守卫更危险。
        var root = FindSourceRoot();
        var checkedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "Util.cs")
                continue; // 调色板定义本身当然要写具体色值
            checkedFiles++;
            var source = File.ReadAllText(file);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(source, @"ConsoleColor\.\w"),
                $"{Path.GetFileName(file)} 仍在直接写 ConsoleColor，主题切换对它无效");
        }
        Assert.True(checkedFiles >= 5, $"只扫到 {checkedFiles} 个文件，说明定位到的不是真实源码目录");
    }

    /// <summary>定位真实的 <c>src/CodeAgent</c>：逐个候选校验里有 Program.cs 且内容完整。</summary>
    private static string FindSourceRoot()
    {
        var tried = new System.Collections.Generic.List<string>();
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent");
                if (Directory.Exists(candidate))
                {
                    tried.Add(candidate);
                    var program = Path.Combine(candidate, "Program.cs");
                    if (File.Exists(program) && new FileInfo(program).Length > 1000)
                        return candidate;
                }
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到真实的 src/CodeAgent。走过的路径: " + string.Join(" | ", tried));
    }

    [Fact]
    public void ThemeIsRestoredAfterTheTest()
    {
        Assert.NotNull(SafeColor.ReadEnv);
    }
}
