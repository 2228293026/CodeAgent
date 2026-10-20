using System;
using CodeAgent;
using CodeAgent.Agent;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// 工具输出里的装饰字形，在 ASCII 退回模式下必须降级。
///
/// 工具是**独立作者**：60 多个工具各自往输出里写 `→ ⚠ ✓`。
/// 指望它们全走 <c>SafeColor.Glyphs</c> 是不现实的——实测扫出 8 个文件，
/// 写了个守卫之后立刻又冒出十几个。逐个改是打地鼠，改完还会再长。
///
/// 正确的口子在**显示边界**：工具输出变成屏幕文字只经过
/// <c>FormatToolOutputPreview</c>，在那里统一降级，覆盖率就是 100%，
/// 新增工具自动受保护。
/// </summary>
public class ToolOutputGlyphGuardTests
{
    [Fact]
    public void ArrowsDegradeToAscii()
    {
        try
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", "1");
            Assert.Equal("a -> b", AgentClass.DegradeToolGlyphs("a → b"));
            Assert.Equal("! 双向分歧", AgentClass.DegradeToolGlyphs("⚠ 双向分歧"));
            Assert.Equal("^待推送", AgentClass.DegradeToolGlyphs("↑待推送"));
            Assert.Equal("v待拉取", AgentClass.DegradeToolGlyphs("↓待拉取"));
            Assert.Equal("v 同步", AgentClass.DegradeToolGlyphs("✓ 同步"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", null);
        }
    }

    [Fact]
    public void UnicodeModeLeavesThemAlone()
    {
        try
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", "0");
            Environment.SetEnvironmentVariable("TERM", null);
            const string raw = "a → b ⚠ ✓ ↑ ↓";
            Assert.Equal(raw, AgentClass.DegradeToolGlyphs(raw));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", null);
        }
    }

    [Fact]
    public void DegradeHappensBeforeTruncation()
    {
        // 顺序必须是「先降级、后截断」：`→` 是 1 列，`->` 是 2 列。
        // 先按原宽裁好再降级，那一行会重新溢出终端。
        try
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", "1");
            // 12 列宽："a -> b -> c" 恰好 11 列。
            // 若先按 12 列裁 "a → b → c"（9 列）再降级，结果 11 列仍然放得下；
            // 换个更紧的宽度才能暴露问题：
            var text = AgentClass.FormatToolOutputPreview("a → b → c", maxChars: 1000, width: 10);
            foreach (var line in text.Split('\n'))
                Assert.True(TextUtil.DisplayWidth(line) <= 10,
                    $"降级顺序错了，这行溢出 10 列 → [{line}]");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", null);
        }
    }

    [Fact]
    public void TheFallbackTableCoversWhatWeActuallyEmit()
    {
        // 这些字形在工具输出里真的出现过（逐个 grep 出来的），一个都不能漏
        try
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", "1");
            foreach (var g in new[] { "→", "←", "↔", "↑", "↓", "⚠", "✓", "✔", "❗", "●" })
            {
                var degraded = AgentClass.DegradeToolGlyphs(g);
                Assert.DoesNotContain(g, degraded);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEAGENT_ASCII", null);
        }
    }
}
