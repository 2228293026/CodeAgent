using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 光敲一个叹号（<c>!</c>）的处理。
///
/// 此前它既不算 bash 命令、又被当成一条**普通消息送去问模型**——整整一轮往返：
/// 花 token、花时间，换来模型一句「看起来消息是空的（只有一个 !），我这边没收到具体
/// 任务，请告诉我你想做什么」。用户敲 <c>!</c> 几乎都是想执行命令时漏写了后半截，
/// 本地给一行提示即可，零成本零延迟。
/// </summary>
public sealed class BareBangLineTests
{
    [Theory]
    [InlineData("!")]
    [InlineData("!   ")]
    [InlineData("  !  ")]
    public void BareBang_IsRecognized(string line)
    {
        Assert.True(Program.IsBareBangLine(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ls -la")]
    [InlineData("你好")]
    [InlineData("!= a")]
    [InlineData("echo a && echo b")]
    public void OtherLines_AreNotBareBang(string line)
    {
        Assert.False(Program.IsBareBangLine(line));
    }

    [Fact]
    public void BareBang_IsStillNotACommand()
    {
        // 既有决定保留：单独一个 ! 不当命令执行（多半是想打叹号）。
        // 这条测试把它钉住，防止将来有人"顺手"把它当空命令去跑。
        Assert.False(Program.TryParseBangCommand("!", out var cmd));
        Assert.Equal(string.Empty, cmd);
    }

    [Fact]
    public void RealBangCommand_StillParses()
    {
        Assert.True(Program.TryParseBangCommand("! dotnet build", out var cmd));
        Assert.Equal("dotnet build", cmd);
    }

    [Fact]
    public void Hint_ShowsAConcreteExample()
    {
        // 只说"请输入命令"不够；给出可直接照抄的例子
        var hint = Program.FormatBareBangHint();
        Assert.Contains("! dotnet build", hint);
        Assert.Contains("! git status", hint);
    }

    [Fact]
    public void Hint_ExplainsHowToJustTypeAnExclamation()
    {
        // 既有决定的理由是"多半是想打叹号"——那就得告诉用户怎么表达
        Assert.Contains("叹号", Program.FormatBareBangHint());
    }

    [Fact]
    public void Hint_IsBoundedWhenGivenAWidth()
    {
        for (var w = 8; w <= 120; w += 4)
            Assert.True(TextUtil.DisplayWidth(Program.FormatBareBangHint(w)) <= w);
    }

    [Fact]
    public void Hint_UnknownWidthIsUntrimmed()
    {
        // width<=0 = 宽度未知，不裁剪（这条语义反复被栽）
        Assert.Equal(Program.FormatBareBangHint(0), Program.FormatBareBangHint());
    }

    [Fact]
    public void ReplHandlesItLocallyWithoutAModelRoundTrip()
    {
        // 只测纯函数会漏掉"REPL 里没接"——那正是原来要付一整轮往返的原因
        var src = File.ReadAllText(Find());
        Assert.Contains("if (Program.IsBareBangLine(line))", src);
        var at = src.IndexOf("if (Program.IsBareBangLine(line))", StringComparison.Ordinal);
        var window = src.Substring(at, Math.Min(300, src.Length - at));
        Assert.Contains("continue", window);
    }

    [Fact]
    public void BareBangCheckRunsAfterTheCommandCheck()
    {
        // 顺序不能反：! dotnet build 必须先被当成命令跑掉
        var src = File.ReadAllText(Find());
        var cmdAt = src.IndexOf("if (Program.TryParseBangCommand(line, out var bangCommand))", StringComparison.Ordinal);
        var bareAt = src.IndexOf("if (Program.IsBareBangLine(line))", StringComparison.Ordinal);
        Assert.True(cmdAt > 0 && bareAt > cmdAt, "裸叹号检查必须排在命令检查之后");
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
