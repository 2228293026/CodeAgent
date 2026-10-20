using System;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 取消行之前该不该先断行。
///
/// 按 Ctrl+C 打断流式输出时，屏幕上留着**半句话**。取消行直接贴上去就成了：
///     我看了下这个报错，应该是配置里的路径写错了⏹ 已取消。
/// 事后回看分不清哪半句是模型的、哪半句是系统的。
///
/// 这个 bug 只在**一次性模式**下露出来：REPL 调用传 prefixNewline=true，
/// 换行是顺带就有了；一次性模式传 false，于是那一分支等于无条件不断行。
/// 而流式路径的 streamedOnLineBoundary 明明算好了却没人看。
/// </summary>
public class CancelLineBreakTests
{
    [Fact]
    public void HalfLineStreamNeedsABreak()
    {
        // 一次性模式：prefixNewline=false，流式过、停在半行
        Assert.True(Program.NeedsBreakBeforeCancel(
            prefixNewline: false, streamed: true, streamedOnLineBoundary: false));
    }

    [Fact]
    public void StreamEndingOnALineBoundaryNeedsNoExtraBreak()
    {
        // 模型本来就以换行结束：再补一个空行是噪音
        Assert.False(Program.NeedsBreakBeforeCancel(
            prefixNewline: false, streamed: true, streamedOnLineBoundary: true));
    }

    [Fact]
    public void NoStreamAtAllNeedsNoBreak()
    {
        // 一个字都没吐就按了 Esc：光标本来就在行首
        Assert.False(Program.NeedsBreakBeforeCancel(
            prefixNewline: false, streamed: false, streamedOnLineBoundary: true));
    }

    [Fact]
    public void ExplicitRequestAlwaysWins()
    {
        // REPL 传 true：不论流式状态如何都要断（它前面通常还有一个 spinner 行）
        Assert.True(Program.NeedsBreakBeforeCancel(true, false, true));
        Assert.True(Program.NeedsBreakBeforeCancel(true, true, true));
        Assert.True(Program.NeedsBreakBeforeCancel(true, true, false));
        Assert.True(Program.NeedsBreakBeforeCancel(true, false, false));
    }

    [Fact]
    public void TheOldBugIsActuallyCovered()
    {
        // 反向证明：旧实现是 `prefixNewline`（只看这一个），
        // 在一次性模式那组参数下它会返回 false——正是漏掉的那个情形
        static bool Old(bool prefixNewline) => prefixNewline;
        Assert.False(Old(prefixNewline: false));
        Assert.True(Program.NeedsBreakBeforeCancel(false, streamed: true, streamedOnLineBoundary: false));
    }
}
