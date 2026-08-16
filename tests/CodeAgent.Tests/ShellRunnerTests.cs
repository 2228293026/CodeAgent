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
}
