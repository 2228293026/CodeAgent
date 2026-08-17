using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>ShellRunner 与 CommandTool 的执行边界测试(补充 ShellRunnerTests / CommandToolTests)。</summary>
public class ShellCommandEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-shell-" + Guid.NewGuid().ToString("N"));

    public ShellCommandEdgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private static AgentContext MakeContext(string dir) => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(dir),
    };

    // ===== ShellRunner.RunAsync =====

    [Fact]
    public async Task RunAsync_Success_ReturnsZeroExit()
    {
        var (exit, output) = await ShellRunner.RunAsync("bash", "echo hello", _dir, 30, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains("hello", output);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_IsReported()
    {
        var (exit, output) = await ShellRunner.RunAsync("bash", "exit 3", _dir, 30, CancellationToken.None);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task RunAsync_Stderr_IsCaptured()
    {
        var (_, output) = await ShellRunner.RunAsync("bash", "echo 'to-stderr' >&2", _dir, 30, CancellationToken.None);
        Assert.Contains("to-stderr", output); // stderr 合并进输出
    }

    [Fact]
    public async Task RunAsync_MissingCwd_Throws()
    {
        var bad = Path.Combine(_dir, "nope");
        await Assert.ThrowsAsync<ToolException>(
            () => ShellRunner.RunAsync("bash", "echo x", bad, 30, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_EnvVar_IsPropagated()
    {
        var (_, output) = await ShellRunner.RunAsync(
            "bash", "echo \"$MY_TEST_VAR\"", _dir, 30, CancellationToken.None,
            new Dictionary<string, string> { ["MY_TEST_VAR"] = "propagated" });
        Assert.Contains("propagated", output);
    }

    // ===== CommandTool =====

    [Fact]
    public async Task CommandTool_MissingCommand_Throws()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(
            () => new CommandTool().ExecuteAsync(new JsonObject(), MakeContext(_dir), CancellationToken.None));
        Assert.Contains("command", ex.Message);
    }

    [Fact]
    public async Task CommandTool_AllowCommandsFalse_ReturnsMessage()
    {
        var ctx = MakeContext(_dir);
        ctx.Config.AllowCommands = false;
        var args = new JsonObject { ["command"] = "echo hi" };
        var result = await new CommandTool().ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.Contains("禁用", result);
    }

    [Fact]
    public async Task CommandTool_Bash_Echo_ReturnsOutput()
    {
        var args = new JsonObject { ["command"] = "echo cmd-output" };
        var result = await new CommandTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("cmd-output", result);
    }

    [Fact]
    public async Task CommandTool_Env_IsPassed()
    {
        var ctx = MakeContext(_dir);
        var args = new JsonObject
        {
            ["command"] = "echo \"$TOOL_VAR\"",
            ["env"] = new JsonObject { ["TOOL_VAR"] = "env-ok" },
        };
        var result = await new CommandTool().ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.Contains("env-ok", result);
    }

    [Fact]
    public async Task CommandTool_RelativeCwd_ResolvesWithinWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        var args = new JsonObject { ["command"] = "pwd", ["cwd"] = "sub" };
        var result = await new CommandTool().ExecuteAsync(args, MakeContext(_dir), CancellationToken.None);
        Assert.Contains("sub", result); // 输出含子目录名
    }
}
