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
}
