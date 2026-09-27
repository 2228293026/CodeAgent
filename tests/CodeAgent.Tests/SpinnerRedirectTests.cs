using System;
using System.IO;
using Xunit;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent.Tests;

/// <summary>
/// spinner 在**输出重定向**时必须完全关闭。
///
/// spinner 每 ~200ms 写一帧，清理靠 <c>\r</c> + 空格 + <c>\r</c>——那只对终端成立。
/// 重定向时 <c>\r</c> 不覆盖，帧一路堆进管道：<c>codeagent "改个错别字" &gt; out.txt</c>
/// 出来的文件里夹着几百行 <c>o | \ | / - | 用时 3s | ↑ 0 tokens</c>。
/// 脚本化使用时管道里的东西是要被**读**的，动画帧是纯噪音。
/// </summary>
public sealed class SpinnerRedirectTests
{
    [Fact]
    public void Predicate_ShowsSpinnerOnATerminal()
    {
        Assert.True(AgentClass.ShouldShowSpinner(outputRedirected: false));
    }

    [Fact]
    public void Predicate_SuppressesSpinnerWhenRedirected()
    {
        Assert.False(AgentClass.ShouldShowSpinner(outputRedirected: true));
    }

    [Fact]
    public void ShowSpinner_ConsultsThePredicate()
    {
        // 只测谓词会漏掉"ShowSpinner 压根没用它"——那正是原来每帧都画的原因
        var src = File.ReadAllText(Find());
        Assert.Contains("ShouldShowSpinner(Console.IsOutputRedirected)", src);
    }

    [Fact]
    public void FinalizeSpinner_DoesNotFreezeASpinnerThatNeverRan()
    {
        // 重定向时没有动画行可"定格"：打出来的是一行带残帧字形和 0 token 的假统计
        var src = File.ReadAllText(Find());
        Assert.Contains("if (!_spinnerShown)", src);
        Assert.Contains("_spinnerShown = true;", src);
    }

    [Fact]
    public void TheGuardIsCheckedBeforeTheStopwatchStarts()
    {
        // 提前返回必须发生在 _spinnerSw.Restart() 之前，否则空转的秒表照样在计
        var src = File.ReadAllText(Find());
        var at = src.IndexOf("ShouldShowSpinner(Console.IsOutputRedirected)", StringComparison.Ordinal);
        Assert.True(at > 0);
        var window = src.Substring(at, Math.Min(300, src.Length - at));
        Assert.True(window.IndexOf("_spinnerSw.Restart()", StringComparison.Ordinal) > 0);
    }

    [Fact]
    public void RedirectedRunEmitsNoSpinnerFrames()
    {
        // 端到端：整条链路跑一次，统计输出里出现几次动画帧
        var before = Console.IsOutputRedirected;
        Assert.True(before, "本测试需要输出重定向的环境（dotnet test 的输出就是管道）");
        Assert.False(AgentClass.ShouldShowSpinner(before));
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Agent", "Agent.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Agent/Agent.cs");
    }
}
