using System;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 多行输入折叠提示要写明**按哪个键**展开。
///
/// 此前提示是「⏷ 共 9 行 ↓ 展开」——那个 ↓ 不是按键，它只是一个图标。
/// 用户粘完一大段代码，看到「共 9 行」却不知道剩下那些行怎么看。
/// 多行粘贴后最常见的疑问正是「我粘的后面那些行去哪了」。
///
/// 另一个容易漏的口子：箭头写死成 `↓` 时，ASCII 退回模式（TERM=dumb /
/// CODEAGENT_ASCII=1）下仍会打出非 ASCII 字符，破坏「ASCII 模式只出 ASCII」的约定。
/// </summary>
/// <remarks>
/// 在 <c>ConsoleOutput</c> 集合里：字形来自 <c>SafeColor.Glyphs</c>（读进程级可替换的
/// <c>SafeColor.ReadEnv</c>）。
/// </remarks>
[Collection("ConsoleOutput")]
public class FoldHintTests
{
    [Fact]
    public void HintNamesTheKeyThatExpands()
    {
        var text = InputLine.FoldText("a\nb\nc\nd\ne", windowWidth: 120);
        Assert.Contains("展开", text);
        // 图标不算「说明按键」：必须出现 Unfold 字形本身
        Assert.Contains(SafeColor.Glyphs.Unfold, text);
    }

    [Fact]
    public void PrefixAndFullHintAgreeOnTheWording()
    {
        // FoldHintPrefixWidth 是按 FoldHintPrefix 算列宽的；
        // 若 FoldText 自己另写一份文案，两处必然漂移，预算跟着算错。
        var prefix = InputLine.FoldHintPrefix(9);
        var full = InputLine.FoldText("1\n2\n3\n4\n5\n6\n7\n8\n9", windowWidth: 120);
        var hintLine = full.Split('\n')[^1];
        // 前缀（去掉末尾的「末行: 」分隔）必须出现在真正的提示行里
        var head = prefix.Split('末')[0].TrimEnd(' ', '·', '-');
        Assert.Contains(head, hintLine);
    }

    [Fact]
    public void AsciiModeUsesOnlyAsciiGlyphsInTheHint()
    {
        // 「ASCII 模式」是**装饰字形**退回 ASCII，不是整行不许有中文——
        // 文案本身是中文，拿 `ch < 128` 去卡整行只会卡出「中文也算违规」的假失败。
        // 这里只卡字形表：箭头此前写死成 `↓`，ASCII 下会原样漏出去。
        var prev = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = _ => "1";
            var text = InputLine.FoldText("a\nb\nc\nd\ne", windowWidth: 120);
            // 折叠提示里的三段装饰必须全是 ASCII
            Assert.True(SafeColor.Glyphs.Fold.All(c => c < 128), "Fold 字形不是 ASCII");
            Assert.True(SafeColor.Glyphs.Unfold.All(c => c < 128), "Unfold 字形不是 ASCII");
            Assert.Contains(SafeColor.Glyphs.Unfold, text);
            // 反过来：真正的箭头不能还留在里面
            Assert.DoesNotContain("↓", text);
        }
        finally { SafeColor.ReadEnv = prev; }
    }

    [Fact]
    public void FoldedCountIsStillReported()
    {
        // 改文案不能把「共 N 行」丢掉：那是用户判断「值不值得展开」的唯一依据
        var text = InputLine.FoldText("a\nb\nc\nd\ne", windowWidth: 120);
        Assert.Contains("5", text);
    }

    [Fact]
    public void ShortInputIsNotFoldedAtAll()
    {
        // 不折叠时不该冒出任何提示
        var text = InputLine.FoldText("a\nb", windowWidth: 120);
        Assert.DoesNotContain("展开", text);
    }
}
