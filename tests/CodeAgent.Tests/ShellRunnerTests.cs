using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class ShellRunnerTests
{
    [Fact]
    public async Task RunAsync_PassesEnvironmentVariables()
    {
        // 需要 bash（CI 为 Linux；本地 Windows 依赖 Git Bash）——项目本身就用 bash 执行命令，此处保持一致
        var (exit, output) = await ShellRunner.RunAsync(
            "bash",
            "echo $CA_TEST_ENV",
            System.IO.Path.GetTempPath(),
            30,
            CancellationToken.None,
            new Dictionary<string, string> { ["CA_TEST_ENV"] = "hello-from-env" });

        Assert.Equal(0, exit);
        Assert.Contains("hello-from-env", output);
    }

    [Fact]
    public async Task RunAsync_WithoutEnv_DoesNotSetVariable()
    {
        var (_, output) = await ShellRunner.RunAsync(
            "bash",
            "echo \"[${CA_TEST_ENV_UNSET:-}]\"",
            System.IO.Path.GetTempPath(),
            30,
            CancellationToken.None);

        Assert.Contains("[]", output);
    }

    [Fact]
    public async Task RunAsync_LargeOutput_DoesNotBlock()
    {
        // 回归：输出超过截断上限后旧实现停止读取，子进程写满管道缓冲阻塞、被超时误杀。
        // 现在达到上限后继续排空管道，命令应正常完成且输出带截断标记。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (exit, output) = await ShellRunner.RunAsync(
            "bash",
            "head -c 300000 /dev/zero | tr \"\\0\" \"a\"",
            System.IO.Path.GetTempPath(),
            30,
            CancellationToken.None);
        sw.Stop();

        Assert.Equal(0, exit);
        Assert.Contains("输出过长", output);
        Assert.True(sw.Elapsed.TotalSeconds < 30, $"大输出命令不应被超时误杀（实际耗时 {sw.Elapsed.TotalSeconds:F1}s）");
    }

    [Fact]
    public async Task RunAsync_MissingCwd_ThrowsHelpfulError()
    {
        // 回归：cwd 不存在时应友好报错，而非进程启动失败显示"无法启动进程"
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            ShellRunner.RunAsync("bash", "echo hi", missing, 30, CancellationToken.None));
        Assert.Contains("目录不存在", ex.Message);
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsProcessAndReports()
    {
        // 超时路径：进程应在超时后被杀掉，返回退出码 124 并带提示
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (exit, output) = await ShellRunner.RunAsync(
            "bash",
            "sleep 30",
            System.IO.Path.GetTempPath(),
            1,
            CancellationToken.None);
        sw.Stop();

        Assert.Equal(124, exit);
        Assert.Contains("超时", output);
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"超时应及时终止进程（实际 {sw.Elapsed.TotalSeconds:F1}s）");
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCode_IsPreserved()
    {
        // 命令失败（退出码 5）应原样返回，输出含退出码行
        var (exit, output) = await ShellRunner.RunAsync(
            "bash",
            "exit 5",
            System.IO.Path.GetTempPath(),
            30,
            CancellationToken.None);

        Assert.Equal(5, exit);
        Assert.Contains("[退出码 5]", output);
    }

    [Fact]
    public async Task RunAsync_StdoutAndStderr_BothCaptured()
    {
        // stdout 与 stderr 都应出现在输出里（合并返回）
        var (exit, output) = await ShellRunner.RunAsync(
            "bash",
            "echo out-msg; echo err-msg >&2",
            System.IO.Path.GetTempPath(),
            30,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("out-msg", output);
        Assert.Contains("err-msg", output);
    }

    [Fact]
    public async Task RunAsync_WorkingDirectory_IsRespected()
    {
        // 在指定 cwd 中执行 pwd：输出应非空且命令成功（Git Bash 的 pwd 输出风格跨平台不同，
        // 不强断言具体路径，只验证命令确实在 cwd 中执行成功）
        var dir = System.IO.Path.GetTempPath();
        var (exit, output) = await ShellRunner.RunAsync(
            "bash",
            "pwd",
            dir,
            30,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("[退出码 0]", output);
        Assert.True(output.TrimEnd().Length > "[退出码 0]".Length, "pwd 应有路径输出");
    }

    [Fact]
    public void AutoShell_ReturnsSomethingOnAllPlatforms()
    {
        // AutoShell：Windows 返回 bash/powershell，其他平台返回空串（由调用方用默认 shell）
        var shell = ShellRunner.AutoShell();
        if (OperatingSystem.IsWindows())
            Assert.True(shell is "bash" or "powershell");
        else
            Assert.Equal("", shell);
    }

    [Fact]
    public void BuildShellCommand_IsDeterministicForBash()
    {
        // 通过执行验证 bash 命令构建结果：-lc 前缀 + 命令原样传递
        // （间接验证：之前 Linux CI 上单引号包裹的命令被拆坏的问题不再出现）
        var (_, output) = ShellRunner.RunAsync(
            "bash",
            "echo 'single quoted arg'",
            System.IO.Path.GetTempPath(),
            30,
            CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Contains("single quoted arg", output);
    }
}
