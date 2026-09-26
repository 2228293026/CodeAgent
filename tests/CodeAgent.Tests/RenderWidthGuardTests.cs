using System;
using System.Collections.Generic;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 全仓「渲染不超宽」守卫。
///
/// 背景：用户反复报同一个现象——提示符/边框行超出终端宽度后折行，把输入块撑高。
/// 此前这类 bug 都是**逐个发现、逐个修**：修完一处，下一处同样的形状还埋在别处。
/// 仓库里已经有宽度读数守卫（TerminalWidthReadGuardTests），
/// 这里补上它的另一半：**输出**也必须守住宽度。
///
/// 三个必须做对的细节：
/// ①**逐行**断言。跨行断言在不同平台结果不同（换行符、渲染差异），不能这么测。
/// ②用 CJK、emoji、路径当素材。它们的显示宽度不是 1——
///    按字符数算的实现在这里会立刻露馅（`⏳`=3、`🔧`=2 都是实测值）。
/// ③width &lt;= 0 表示"宽度未知，不裁剪"，此时**不该**断言超宽——
///    那是刻意的设计选择（宁可少一段也不凭空猜），把它当 bug 会逼出错误修复。
/// </summary>
public sealed class RenderWidthGuardTests
{
    /// <summary>对抗性素材：长度、显示宽度、字符数三者各不相同。</summary>
    internal static readonly string[] Payloads =
    [
        "",
        "x",
        "plain ascii payload",
        "中文提示符内容测试",
        "混合 mixed 中英文 payload 内容",
        "超长无空格中文串没有任何可以断行的位置所以必须硬截断处理掉",
        new string('a', 300),
        new string('中', 150),          // 300 显示列
        "路径 D:\\Projects\\CodeAgent\\src\\CodeAgent\\Program.cs 很长很长",
        "emoji ⏳🔧✅ 混排 🎉 在这里",
        "组合符 👨‍👩‍👧‍👦 家庭 emoji 占两列",
        "零宽‍字符夹在中间测试",
        "\t制表符与 空格 混排的内容",
    ];

    private static IEnumerable<int> Widths()
    {
        for (var w = 8; w <= 200; w += 1)
            yield return w;
    }

    private static void AssertLinesFit(string text, int width, string what)
    {
        if (width <= 0)
            return; // 宽度未知 = 刻意不裁剪，不该断言
        // 逐行断言（不要跨行）
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var w = TextUtil.DisplayWidth(line);
            Assert.True(w <= width, $"{what}: 宽度 {width} 下一行宽 {w} 超宽 → {Trunc(line)}");
        }
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s[..60] + "…";

    [Fact]
    public void InputFrameTop_NeverExceedsWidth()
    {
        foreach (var p in Payloads)
            foreach (var hint in new[] { "", "◈ high · /thinking", "abc" })
                foreach (var w in Widths())
                    AssertLinesFit(Program.BuildInputFrameTop(p, hint, w), w, "BuildInputFrameTop");
    }

    [Fact]
    public void PromptText_NeverExceedsWidth()
    {
        foreach (var p in Payloads)
            foreach (var w in Widths())
                AssertLinesFit(Program.BuildPromptText("code", "space-bunny-alpha", p, w), w, "BuildPromptText");
    }

    [Fact]
    public void NoticeAndConfirmLines_NeverExceedWidth()
    {
        foreach (var p in Payloads)
            foreach (var w in Widths())
            {
                AssertLinesFit(Program.FormatNoticeLine(p, w), w, "FormatNoticeLine");
                AssertLinesFit(Program.FormatConfirmLine(p, w), w, "FormatConfirmLine");
                AssertLinesFit(Program.FormatCancelLine(p, w), w, "FormatCancelLine");
                AssertLinesFit(Program.FormatHintLine(p, w), w, "FormatHintLine");
                AssertLinesFit(Program.FormatResultLine(p, w), w, "FormatResultLine");
            }
    }

    [Fact]
    public void StatusAndFooterBars_NeverExceedWidth()
    {
        foreach (var p in Payloads)
            foreach (var branch in new[] { "", "main", "feature/非常长的分支名字" })
                foreach (var w in Widths())
                {
                    AssertLinesFit(
                        Program.BuildStatusBar("code", "space-bunny-alpha", p, branch, "0", "0", "ctx 12%", "high", w),
                        w, "BuildStatusBar");
                    AssertLinesFit(
                        Program.BuildFooterBar(branch, p, "space-bunny-alpha", 12345, 6789, "ctx 12%", "21:14", w),
                        w, "BuildFooterBar");
                }
    }

    [Fact]
    public void TurnSummary_NeverExceedsWidth()
    {
        foreach (var w in Widths())
        {
            AssertLinesFit(Program.BuildTurnSummary(3, 12, "12.3s", "↑ 1.2k ↓ 340", " 思考 2.5s", " 缓存 80%", " ≈$0.01", w),
                w, "BuildTurnSummary");
            // 空的可丢段也走一遍：丢段逻辑的分支比满段更多
            AssertLinesFit(Program.BuildTurnSummary(1, 0, "0.4s", "", "", "", "", w), w, "BuildTurnSummary(空)");
        }
    }

    [Fact]
    public void HelpAndKeyValueLists_NeverExceedWidth()
    {
        var entries = new Program.HelpEntry[]
        {
            new("/help", "显示本帮助"),
            new("/export [名/编号/all]", "导出会话为 Markdown（同名快照优先）"),
            new(new string('x', 200), new string('中', 100)),
        };
        foreach (var w in Widths())
        {
            AssertLinesFit(Program.FormatHelpList(entries, w), w, "FormatHelpList");
            foreach (var p in Payloads)
            {
                AssertLinesFit(Program.FormatKeyValueLine("键", p, 8, w), w, "FormatKeyValueLine");
                AssertLinesFit(Program.FormatPathList(new[] { p, "x" }, w), w, "FormatPathList");
                AssertLinesFit(Program.FormatSearchHitLine("user", p, w), w, "FormatSearchHitLine");
            }
        }
    }

    [Fact]
    public void BannerAndHintLines_NeverExceedWidth()
    {
        foreach (var p in Payloads)
            foreach (var w in Widths())
            {
                AssertLinesFit(Program.BuildBannerHeadline("CodeAgent 0.4.0", p, w), w, "BuildBannerHeadline");
                AssertLinesFit(Program.BuildBannerNotice(p, w), w, "BuildBannerNotice");
                AssertLinesFit(Program.BuildHintLine(p, w), w, "BuildHintLine");
                AssertLinesFit(Program.FormatSettingSavedLine("已切换", p, p, w), w, "FormatSettingSavedLine");
            }
    }

    [Fact]
    public void BangModeLines_NeverExceedWidth()
    {
        foreach (var p in Payloads)
            foreach (var w in Widths())
            {
                AssertLinesFit(Program.FormatBangEcho(p, w), w, "FormatBangEcho");
                foreach (var code in new[] { 0, 1, 127, 255, 30000 })
                    AssertLinesFit(Program.FormatBangResult(code, w), w, "FormatBangResult");
            }
    }

    [Fact]
    public void ToolAndRendererLines_NeverExceedWidth()
    {
        foreach (var p in Payloads)
            foreach (var w in Widths())
            {
                AssertLinesFit(CodeAgent.Agent.Agent.FormatToolStatusLine(p, false, TimeSpan.FromSeconds(3.5), w),
                    w, "FormatToolStatusLine");
                AssertLinesFit(CodeAgent.Agent.Agent.FormatToolOutputPreview(p, 60, w), w, "FormatToolOutputPreview");
                AssertLinesFit(ConsoleRenderer.FormatCodeLangBadge(p, w), w, "FormatCodeLangBadge");
                AssertLinesFit(Program.FormatDiffLine(p, w), w, "FormatDiffLine");
            }
    }
}
