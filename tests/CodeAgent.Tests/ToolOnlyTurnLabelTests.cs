using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>/history</c> 里"只有工具调用、没有正文"那一轮的显示。
///
/// 此前一律是 <c>[调用 stop]</c>，<b>把参数全丢了</b>。而 <c>stop</c> 的
/// <c>reason</c> 恰恰是模型给用户的最终总结或<b>要问用户的问题</b>——
/// 系统提示写明"任务完成或需要提问时调用 stop"。
/// 于���回看历史只看到「助手调用了 stop」，不知道它到底说了什么。
/// </summary>
public sealed class ToolOnlyTurnLabelTests
{
    private static List<(string, string)> Calls(params (string Name, string Args)[] xs) =>
        xs.Select(x => (x.Name, x.Args)).ToList();

    [Fact]
    public void StopReason_IsShown()
    {
        var label = Program.FormatToolOnlyTurnLabel(Calls(("stop", """{"reason":"需要你确认要改哪个文件。"}""")), 80);
        Assert.Contains("stop", label);
        Assert.Contains("需要你确认要改哪个文件。", label);
    }

    [Fact]
    public void StopReason_IsTheQuestionTheModelIsAsking()
    {
        // 真实场景：模型把问题放进 reason 就结束了本轮
        var label = Program.FormatToolOnlyTurnLabel(
            Calls(("stop", """{"reason":"我发现有两个同名文件，需要你告诉我该改哪个。"}""")), 80);
        Assert.Contains("需要你告诉我该改哪个", label);
    }

    [Fact]
    public void StopWithoutReason_StillShowsTheCall()
    {
        var label = Program.FormatToolOnlyTurnLabel(Calls(("stop", "{}")), 80);
        Assert.Equal("[调用 stop]", label);
    }

    [Fact]
    public void StopWithBlankReason_StillShowsTheCall()
    {
        Assert.Equal("[调用 stop]", Program.FormatToolOnlyTurnLabel(Calls(("stop", """{"reason":"   "}""")), 80));
    }

    [Fact]
    public void StopWithNoReasonKey_StillShowsTheCall()
    {
        Assert.Equal("[调用 stop]", Program.FormatToolOnlyTurnLabel(Calls(("stop", """{"other":"x"}""")), 80));
    }

    [Fact]
    public void OtherTools_AreListedWithoutTheirArgs()
    {
        // edit_file 的参数在 diff 预览里已经看过；路径/行号塞进历史只会撑长这一行
        var label = Program.FormatToolOnlyTurnLabel(Calls(("read_file", """{"path":"a/very/long/path.cs"}""")), 80);
        Assert.Equal("[调用 read_file]", label);
    }

    [Fact]
    public void MixedCalls_ListThemAllAndStillShowTheStopReason()
    {
        var label = Program.FormatToolOnlyTurnLabel(
            Calls(("read_file", "{}"), ("stop", """{"reason":"看完了，没问题。"}""")), 80);
        Assert.Contains("read_file", label);
        Assert.Contains("stop", label);
        Assert.Contains("看完了，没问题。", label);
    }

    [Fact]
    public void MalformedArgs_DoNotBreakTheLabel()
    {
        // 展示用途，参数不是合法 JSON 也不能让 /history 整个挂掉
        var label = Program.FormatToolOnlyTurnLabel(Calls(("stop", "not json at all")), 80);
        Assert.Equal("[调用 stop]", label);
    }

    [Fact]
    public void EmptyCallList_ProducesNothing()
    {
        Assert.Equal(string.Empty, Program.FormatToolOnlyTurnLabel(new List<(string, string)>(), 80));
    }

    [Fact]
    public void LongReason_IsTruncatedNotWrapped()
    {
        var reason = new string('问', 400);
        var label = Program.FormatToolOnlyTurnLabel(Calls(("stop", $$"""{"reason":"{{reason}}"}""")), 80);
        // 折行会把这一行拆成两半，读起来像两条记录
        Assert.DoesNotContain("\n", label);
        Assert.Contains("…", label);
    }

    [Fact]
    public void UnicodeEscapes_AreNotShownRaw()
    {
        // JsonNode 默认编码器会把中文转成 \uXXXX（曾导致工具行显示 \u9879\u76EE…）
        var label = Program.FormatToolOnlyTurnLabel(Calls(("stop", """{"reason":"中文要原样显示"}""")), 80);
        Assert.Contains("中文要原样显示", label);
        Assert.DoesNotContain("\\u", label);
    }

    [Fact]
    public void HistoryActuallyUsesIt()
    {
        // 只测纯函数会漏掉"/history 根本没调它"——那正是原来的 bug
        var src = System.IO.File.ReadAllText(Find());
        // 旧写法是 $"[调用 {calls}]"（calls 为局部变量名），源码里不该再出现
        Assert.DoesNotContain("calls = string.Join", src);
        Assert.Contains("FormatToolOnlyTurnLabel(", src);
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
