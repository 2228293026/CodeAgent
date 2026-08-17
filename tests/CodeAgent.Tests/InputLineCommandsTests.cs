using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class InputLineCommandsTests
{
    [Fact]
    public void Commands_CoverAllReplCommands()
    {
        // 回归：菜单目录须覆盖 REPL 支持的全部命令（Program.HandleCommand 的 case
        // 加 REPL 循环特殊处理的 /retry），防止新增命令后忘记同步菜单。
        var handled = new[]
        {
            "/help", "/clear", "/compact", "/cls", "/model", "/config", "/session", "/setup",
            "/undo", "/diff", "/save", "/load", "/history", "/export", "/stats",
            "/tools", "/providers", "/mode", "/access", "/diag", "/models", "/thinking",
            "/exit", "/quit", "/retry",
        };
        var menu = InputLine.Commands.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var cmd in handled)
            Assert.True(menu.Contains(cmd), $"命令菜单缺少: {cmd}");
    }

    [Fact]
    public void Commands_NoDuplicatesAndSlashPrefixed()
    {
        // 菜单项应互不重复、都以 / 开头、且都有说明文字
        Assert.Equal(
            InputLine.Commands.Length,
            InputLine.Commands.Select(c => c.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(InputLine.Commands, c => Assert.StartsWith("/", c.Name));
        Assert.All(InputLine.Commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Desc)));
    }

    [Theory]
    // 回归：菜单 23 项、窗口 8 项时，数字 9 应返回 -1（第 9 项不可见，不应被触发）
    [InlineData(1, 0, 8, 23, 0)]   // 窗口第 1 项
    [InlineData(8, 0, 8, 23, 7)]   // 窗口最后一项
    [InlineData(9, 0, 8, 23, -1)]  // 超出可见窗口 → 忽略
    [InlineData(3, 5, 8, 23, 7)]   // 窗口已滚动：第 3 项对应列表下标 5+3-1=7
    [InlineData(9, 5, 8, 23, -1)]  // 滚动后数字 9 仍超出可见窗口
    [InlineData(4, 0, 2, 2, -1)]   // 菜单只有 2 项：数字 4 无效
    [InlineData(2, 0, 2, 2, 1)]    // 菜单 2 项：数字 2 有效
    // 滚动模式（menuShown=0）：可见数 = min(项数, 9)，与 PrintFilterScroll 显示项数一致
    [InlineData(1, 0, 0, 23, 0)]   // 滚动模式：数字 1 有效
    [InlineData(9, 0, 0, 23, 8)]   // 滚动模式：数字 9 有效（上限内）
    [InlineData(10, 0, 0, 23, -1)] // 滚动模式：数字 10 超出上限 9 → 忽略（回归）
    [InlineData(0, 0, 8, 23, -1)]  // 数字 0 无效
    // 额外边界：窗口偏移与可见数组合
    [InlineData(1, 10, 9, 23, 10)]  // 窗口滚动到 offset=10：数字 1 → 列表下标 10
    [InlineData(9, 10, 9, 23, 18)]  // 窗口滚动：数字 9 → 下标 18
    [InlineData(9, 15, 9, 23, -1)]  // offset=15 + 8 = 23 越界 → 忽略（回归：越界下标不触发）
    [InlineData(1, 0, 9, 1, 0)]     // 单选项：数字 1 有效
    [InlineData(2, 0, 9, 1, -1)]    // 单选项：数字 2 无效
    [InlineData(1, 0, 0, 0, -1)]    // 空菜单：任何数字无效
    [InlineData(5, 0, 0, 5, 4)]     // 滚动模式 5 项：数字 5 有效
    [InlineData(5, 0, 0, 4, -1)]    // 滚动模式 4 项：数字 5 超出
    public void DigitKeySelection_RespectsVisibleWindow(int n, int offset, int shown, int count, int expected) =>
        Assert.Equal(expected, InputLine.DigitKeySelection(n, offset, shown, count));

    [Fact]
    public void Commands_AreListedInLogicalOrder()
    {
        // 常用命令应排在前部（帮助/清空/压缩/清屏/模型切换），方便数字键快速触发
        var names = InputLine.Commands.Select(c => c.Name).ToList();
        Assert.Equal("/help", names[0]);
        Assert.Equal("/clear", names[1]);
        Assert.Equal("/compact", names[2]);
        Assert.Equal("/cls", names[3]);
        Assert.Equal("/model", names[4]);
        Assert.Equal("/quit", names[^1]); // 退出命令在末尾（/quit 在 /exit 之后）
    }

    [Fact]
    public void Commands_EveryNameMatchesHandleCommand()
    {
        // 每个菜单命令都应在 HandleCommand 的 case 或 REPL 特殊处理中有对应逻辑
        // （间接验证：菜单与实现不脱节）
        var special = new[] { "/retry" }; // REPL 循环特殊处理
        var handled = new[]
        {
            "/help", "/clear", "/compact", "/cls", "/model", "/config", "/session", "/setup",
            "/undo", "/diff", "/save", "/load", "/history", "/export", "/stats",
            "/tools", "/providers", "/mode", "/access", "/diag", "/models", "/thinking",
            "/exit", "/quit",
        };
        foreach (var c in InputLine.Commands)
        {
            Assert.True(
                handled.Contains(c.Name) || special.Contains(c.Name),
                $"命令 {c.Name} 在 HandleCommand 中无对应处理");
        }
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("单行", 3)]
    [InlineData("a\nb", 3)]
    [InlineData("a\nb\nc", 3)]   // 恰好 3 行：不折叠
    public void FoldText_AtOrBelowThreshold_ReturnsAsIs(string text, int threshold)
    {
        Assert.Equal(text, InputLine.FoldText(text, threshold));
    }

    [Fact]
    public void FoldText_OverThreshold_FoldsToFirstTwoLinesPlusHint()
    {
        var input = "行1\n行2\n行3\n行4\n行5";
        var folded = InputLine.FoldText(input);
        var lines = folded.Split('\n');
        Assert.Equal(3, lines.Length);                 // 前 2 行 + 折叠提示行
        Assert.Equal("行1", lines[0]);
        Assert.Equal("行2", lines[1]);
        Assert.Contains("共 5 行", lines[2]);          // 提示行包含总行数
        Assert.DoesNotContain("行3", folded);          // 隐藏中间行
    }

    [Fact]
    public void FoldText_FourLines_FoldsToOneHint()
    {
        // 4 行（刚超阈值）：折成前 2 行 + 提示（共 4 行）
        var folded = InputLine.FoldText("a\nb\nc\nd");
        Assert.Equal(3, folded.Split('\n').Length);
        Assert.Contains("共 4 行", folded);
    }

    [Fact]
    public void FoldText_CustomThreshold_Respected()
    {
        // 自定义阈值 4：超过 4 行才折叠，折成前 3 行 + 提示
        var folded = InputLine.FoldText("a\nb\nc\nd\ne", 4);
        Assert.Equal(4, folded.Split('\n').Length);    // 前 3 行 + 提示行
        Assert.Contains("共 5 行", folded);
        Assert.Contains("c", folded);                  // 第 3 行保留（阈值 4 折前 3 行）
    }

    [Theory]
    [InlineData("/model", "/model")]
    [InlineData("／model", "/model")] // 全角斜杠归一化
    [InlineData("model", "model")]    // 无前导斜杠原样返回
    [InlineData("／", "/")]
    [InlineData("", "")]
    public void NormalizeCommandFilter_ConvertsLeadingFullWidthSlash(string input, string expected) =>
        Assert.Equal(expected, InputLine.NormalizeCommandFilter(input));

    [Theory]
    [InlineData("/", true)]      // 全量前缀：所有命令都命中
    [InlineData("/m", true)]     // /mode /model 等
    [InlineData("/MODE", true)]  // 前缀匹配忽略大小写
    [InlineData("／model", true)]// 全角归一化后命中
    [InlineData("/model", true)] // 恰等完整命令：自身即前缀（菜单保留唯一项）
    [InlineData("/model ", false)]// 追加参数（空格）：进入参数输入，菜单应关闭让位
    [InlineData("/modes", false)]// 不是任何命令的前缀
    [InlineData("／zzz", false)]
    [InlineData("model", false)] // 不以斜杠开头
    public void IsCommandPrefix_OnlyWhileInputIsACommandPrefix(string input, bool expected) =>
        Assert.Equal(expected, InputLine.IsCommandPrefix(input));
}
