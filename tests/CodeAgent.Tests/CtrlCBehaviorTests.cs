using System;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入行的 Ctrl+C 语义（学自 Claude Code）。
///
/// 此前 Ctrl+C 在输入行里**直接终止整个进程**：
/// 对话没了、草稿没了、正在写的东西没了，而且按下和松手之间来不及反应。
/// 没有任何提示，也没有任何挽回机会。
/// </summary>
public sealed class CtrlCBehaviorTests
{
    [Fact]
    public void CtrlC_OnNonEmptyInput_ClearsTheLine()
    {
        // 误触不该有不可挽回的后果：先清这一行，会话继续
        Assert.Equal(InputLine.CtrlCOutcome.ClearInput, InputLine.CtrlCBehavior("帮我改一下 README"));
    }

    [Fact]
    public void CtrlC_OnEmptyInput_Exits()
    {
        // 已经空了还按，说明是真想走
        Assert.Equal(InputLine.CtrlCOutcome.Exit, InputLine.CtrlCBehavior(""));
    }

    [Fact]
    public void CtrlC_OnWhitespaceOnlyInput_ClearsFirst()
    {
        // 只有一个空格也算"有内容"：用户可能是想输一个空格
        Assert.Equal(InputLine.CtrlCOutcome.ClearInput, InputLine.CtrlCBehavior(" "));
    }

    [Fact]
    public void CtrlC_NeedsTwoPressesToLoseTheSession()
    {
        // 核心不变量：任何输入下都不该"按一下就没"
        foreach (var text in new[] { "", " ", "a", "帮我改一下 README", new string('x', 5000) })
        {
            var first = InputLine.CtrlCBehavior(text);
            if (text.Length == 0)
                Assert.Equal(InputLine.CtrlCOutcome.Exit, first);
            else
                Assert.Equal(InputLine.CtrlCOutcome.ClearInput, first);
        }
    }

    [Fact]
    public void CtrlC_AfterClearingTheSecondPressExits()
    {
        // 两段式的完整序列
        var text = "帮我改一下 README";
        Assert.Equal(InputLine.CtrlCOutcome.ClearInput, InputLine.CtrlCBehavior(text));
        text = string.Empty; // 清空之后
        Assert.Equal(InputLine.CtrlCOutcome.Exit, InputLine.CtrlCBehavior(text));
    }

    [Fact]
    public void TreatControlCIsReadWithoutThrowing()
    {
        // 重定向/不支持的平台上也必须能问，不能在输入行入口就炸
        _ = InputLine.TreatControlCAsInputSafe();
    }

    [Fact]
    public void CtrlCIsRestoredAfterReading()
    {
        // 接线守卫：读输入期间要 TreatControlCAsInput=true（Ctrl+C 才不会杀进程），
        // 但**必须**在 finally 里恢复——回合内 Ctrl+C 要继续走 CancelKeyPress 去取消本轮。
        // 漏恢复会让"取消当前回合"整体失灵，比原来的行为更糟。
        var src = System.IO.File.ReadAllText(FindInputLine());
        Assert.Contains("Console.TreatControlCAsInput = true", src);
        Assert.Contains("finally", src);
        Assert.Contains("Console.TreatControlCAsInput = priorTreatControlC", src);
    }

    [Fact]
    public void CtrlCIsHandledBeforeTheGenericKeySwitch()
    {
        // Ctrl+C 落到默认分支就会被当成普通字符插进输入框
        var src = System.IO.File.ReadAllText(FindInputLine());
        var at = src.IndexOf("case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0", StringComparison.Ordinal);
        Assert.True(at > 0, "输入行里找不到 Ctrl+C 分支");
        var sw = src.IndexOf("switch (key.Key)", StringComparison.Ordinal);
        Assert.True(sw > 0 && at > sw, "Ctrl+C 分支必须位于按键 switch 内部");
    }

    private static string FindInputLine()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "InputLine.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/InputLine.cs");
    }
}
