using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// `--help` 在窄终端上的排版。
///
/// 修之前整块帮助是硬写在字符串里的，按**显示宽度**算最长一行 90 列
/// （`--mode` 那行字符数看着只有 67——中文占两列）。
/// 80 列终端上它必然折行，折出来的续行从第 0 列开始，与选项列彻底错位：
/// 用户看到的是「某个选项的说明跑到了左边顶格」。
/// </summary>
public class HelpNarrowWidthTests
{
    [Fact]
    public void EveryOptionLineFitsTheWidth()
    {
        var rows = CodeAgent.Program.HelpOptionRows();
        var keyWidth = 0;
        foreach (var r in rows)
            keyWidth = System.Math.Max(keyWidth, TextUtil.DisplayWidth(r.Key));

        foreach (var w in new[] { 40, 50, 60, 70, 80, 100, 120 })
        {
            foreach (var (key, desc) in rows)
            {
                var text = CodeAgent.Program.FormatHelpOption(key, desc, keyWidth, w);
                foreach (var line in text.Split('\n'))
                {
                    Assert.True(TextUtil.DisplayWidth(line) <= w,
                        $"宽度 {w}：「{key}」这行溢出了 → [{line}]");
                }
            }
        }
    }

    [Fact]
    public void ContinuationLinesAlignWithTheDescriptionColumn()
    {
        // 折行后的续行必须与说明首列对齐。退回缩进处会让续行读起来
        // 像一个独立选项，或者像键的一部分。
        var width = 60;
        var text = CodeAgent.Program.FormatHelpOption(
            "--mode <名>",
            "以指定工作模式启动（会话级覆盖 defaultMode，不写回配置；/mode 查看）",
            TextUtil.DisplayWidth("--mode <名>"), width);
        var lines = text.Split('\n');
        Assert.True(lines.Length > 1, "这段说明在 60 列下不可能一行装完");
        var headWidth = TextUtil.DisplayWidth("  " + "--mode <名>" + "  ");
        for (var i = 1; i < lines.Length; i++)
        {
            var indent = lines[i].Length - lines[i].TrimStart().Length;
            Assert.Equal(headWidth, indent);
        }
    }

    [Fact]
    public void UsageLinesWrapInsteadOfOverflowing()
    {
        foreach (var w in new[] { 40, 50, 60, 70, 80 })
        {
            var text = CodeAgent.Program.FormatHelpUsageLine(
                "  codeagent [选项] [\"任务描述\"]     交互式 REPL（默认）；带任务则一次性执行后退出", w);
            foreach (var line in text.Split('\n'))
                Assert.True(TextUtil.DisplayWidth(line) <= w,
                    $"宽度 {w}：用法行溢出 → [{line}]");
        }
    }

    [Fact]
    public void UnknownWidthLeavesTheTextAlone()
    {
        // 宽度未知（管道/重定向）时不折行：没有依据就别改用户的文本
        var raw = "  codeagent [选项] [\"任务描述\"]     交互式 REPL（默认）；带任务则一次性执行后退出";
        Assert.Equal(raw, CodeAgent.Program.FormatHelpUsageLine(raw, 0));
        Assert.Equal("  -c, --config <路径>  指定配置文件",
            CodeAgent.Program.FormatHelpOption("-c, --config <路径>", "指定配置文件",
                TextUtil.DisplayWidth("-c, --config <路径>"), 0));
    }
}
