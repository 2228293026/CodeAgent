using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 颜色的关闭信号。
/// 回归点：SafeColor 此前只有「终端抛异常」这一条降级路径，于是
/// ①输出重定向到文件/管道时，转义序列原样写进日志（grep 到的全是噪声）；
/// ②用户按 https://no-color.org 设了 NO_COLOR，仍然满屏彩色；
/// ③TERM=dumb 的终端同样被忽略。
/// </summary>
public class SafeColorEnabledTests
{
    [Fact]
    public void EnabledByDefault()
    {
        Assert.True(SafeColor.ComputeEnabled(null, null, false));
    }

    [Fact]
    public void NoColorDisables()
    {
        // 该标准的定义：任何**非空**值都生效
        Assert.False(SafeColor.ComputeEnabled("1", null, false));
        Assert.False(SafeColor.ComputeEnabled("true", null, false));
        Assert.False(SafeColor.ComputeEnabled(" ", "xterm", false));
    }

    [Fact]
    public void EmptyNoColorDoesNotDisable()
    {
        // 空值 = 未设置，不该关闭
        Assert.True(SafeColor.ComputeEnabled("", null, false));
    }

    [Fact]
    public void DumbTerminalDisables()
    {
        Assert.False(SafeColor.ComputeEnabled(null, "dumb", false));
        Assert.False(SafeColor.ComputeEnabled(null, "DUMB", false));
    }

    [Fact]
    public void OtherTerminalTypesStayEnabled()
    {
        foreach (var term in new[] { "xterm", "xterm-256color", "screen", "alacritty", "vt100" })
            Assert.True(SafeColor.ComputeEnabled(null, term, false));
    }

    [Fact]
    public void RedirectedOutputDisables()
    {
        Assert.False(SafeColor.ComputeEnabled(null, null, true));
    }

    [Fact]
    public void DumbWithNoColorStillDisables()
    {
        Assert.False(SafeColor.ComputeEnabled("1", "dumb", true));
    }

    [Fact]
    public void ReadsTheStandardVariableNames()
    {
        var seen = new System.Collections.Generic.List<string>();
        var enabled = SafeColor.ComputeEnabled(name => { seen.Add(name); return name == "NO_COLOR" ? "1" : null; }, false);
        Assert.False(enabled);
        Assert.Contains("NO_COLOR", seen);
        Assert.Contains("TERM", seen);
    }

    [Fact]
    public void EveryColourEntryPointRespectsTheFlag()
    {
        var original = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = name => name == "NO_COLOR" ? "1" : null;
            // 不抛异常即可：关闭时三个入口都应直接返回，不去碰 Console
            SafeColor.Foreground(ConsoleColor.Red);
            SafeColor.Background(ConsoleColor.Black);
            SafeColor.Reset();
            Assert.False(SafeColor.Enabled);
        }
        finally { SafeColor.ReadEnv = original; }
    }

    [Fact]
    public void FlagIsRestoredAfterTheTest()
    {
        Assert.NotNull(SafeColor.ReadEnv);
    }
}
