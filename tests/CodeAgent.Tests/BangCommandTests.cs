using System;
using System.IO;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// <c>!命令</c> bash 模式（学自 Claude Code）：直接跑 shell，不经过模型。
/// </summary>
public sealed class BangCommandTests
{
    [Fact]
    public void Bang_ParsesCommand()
    {
        Assert.True(Program.TryParseBangCommand("!ls -la", out var c));
        Assert.Equal("ls -la", c);
    }

    [Fact]
    public void Bang_TrimsAroundBang()
    {
        Assert.True(Program.TryParseBangCommand("  !  git status  ", out var c));
        Assert.Equal("git status", c);
    }

    [Fact]
    public void Bang_BareExclamationIsNotACommand()
    {
        // 单独一个 ! 多半是想打叹号；按命令处理会得到空命令 + 一句莫名其妙的错误
        Assert.False(Program.TryParseBangCommand("!", out _));
        Assert.False(Program.TryParseBangCommand("!   ", out _));
        Assert.False(Program.TryParseBangCommand("", out _));
    }

    [Fact]
    public void Bang_NonBangLinesAreNotCommands()
    {
        Assert.False(Program.TryParseBangCommand("ls -la", out _));
        Assert.False(Program.TryParseBangCommand("/help", out _));
        Assert.False(Program.TryParseBangCommand("a ! b", out _));
    }

    [Fact]
    public void Bang_DoesNotSwallowSlashCommands()
    {
        // 与 /xxx 互斥：先判谁都不会互相吞掉
        Assert.False(Program.TryParseBangCommand("/model", out _));
        Assert.True(Program.TryParseBangCommand("!echo hi", out _));
    }

    [Fact]
    public void Bang_EchoShowsThePrefix()
    {
        Assert.Equal("! ls -la", Program.FormatBangEcho("ls -la", 0));
    }

    [Fact]
    public void Bang_EchoIsTruncatedToWidth()
    {
        var text = Program.FormatBangEcho("echo " + new string('x', 200), 40);
        Assert.True(TextUtil.DisplayWidth(text) <= 40, $"超宽: {TextUtil.DisplayWidth(text)}");
        // 截断必须留省略号：照着半条命令复制粘贴会得到难以定位的错误
        Assert.Contains(SafeColor.Glyphs.Ellipsis, text);
    }

    [Fact]
    public void Bang_ResultReportsExitCode()
    {
        Assert.Contains("0", Program.FormatBangResult(0, 0));
        Assert.Contains("1", Program.FormatBangResult(1, 0));
        Assert.Contains("127", Program.FormatBangResult(127, 0));
    }

    [Fact]
    public void Bang_ResultUsesDistinctMarksForSuccessAndFailure()
    {
        // 成功与失败不能长得一样——扫读时看不出命令到底成没成
        var ok = Program.FormatBangResult(0, 0);
        var bad = Program.FormatBangResult(2, 0);
        Assert.NotEqual(ok, bad);
        Assert.Contains(SafeColor.Glyphs.Ok, ok);
    }

    [Fact]
    public void Bang_ResultFitsWidth()
    {
        var text = Program.FormatBangResult(127, 8);
        Assert.True(TextUtil.DisplayWidth(text) <= 8);
    }

    [Fact]
    public void Bang_IsWiredIntoTheReplLoop()
    {
        // 只测纯函数会漏掉「接进 REPL 循环」这一步
        var source = System.IO.File.ReadAllText(FindProgram());
        Assert.Contains("TryParseBangCommand(", source);
        Assert.Contains("RunBangCommandAsync(", source);
    }

    [Fact]
    public void Bang_BangModeRunsBeforeSlashCommands()
    {
        // 顺序不能反：反了的话 / 开头会被 ! 分支先判，虽然互斥但顺序要显式钉住
        var source = System.IO.File.ReadAllText(FindProgram());
        var bang = source.IndexOf("TryParseBangCommand(line", System.StringComparison.Ordinal);
        var slash = source.IndexOf("if (line.StartsWith('/'))", System.StringComparison.Ordinal);
        Assert.True(bang > 0 && slash > 0, "REPL 里应同时存在 ! 分支与 / 分支");
        Assert.True(bang < slash, "! 分支必须排在 / 分支之前");
    }

    private static string FindProgram()
    {
        // 与 TerminalWidthReadGuardTests 同一套定位方式：只找 src/CodeAgent 且 Program.cs 非空
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var candidate = Path.Combine(dir, "src", "CodeAgent");
                var program = Path.Combine(candidate, "Program.cs");
                if (Directory.Exists(candidate) && File.Exists(program) && new FileInfo(program).Length > 1000)
                    return program;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
