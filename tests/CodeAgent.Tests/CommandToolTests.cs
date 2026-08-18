using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class CommandToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-cmd-" + Guid.NewGuid().ToString("N"));

    public CommandToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private static AgentContext MakeContext(string dir, bool allowCommands = true) => new()
    {
        Config = new AgentConfig { AllowCommands = allowCommands, Shell = "bash" },
        Workspace = new Workspace(dir),
    };

    [Fact]
    public async Task RunCommand_EnvJson_IsPassedToProcess()
    {
        // 端到端：工具 JSON 参数的 env 对象应注入进程环境
        var tool = new CommandTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject
            {
                ["command"] = "echo $CA_CMD_TEST",
                ["env"] = new JsonObject { ["CA_CMD_TEST"] = "from-tool-json" },
            },
            ctx, CancellationToken.None);

        Assert.Contains("from-tool-json", output);
        Assert.Contains("退出码 0", output);
    }

    [Fact]
    public async Task RunCommand_EnvOverridesInheritedValue()
    {
        // env 应覆盖进程继承的同名变量
        Environment.SetEnvironmentVariable("CA_CMD_INHERITED", "original");
        try
        {
            var tool = new CommandTool();
            var ctx = MakeContext(_dir);

            var output = await tool.ExecuteAsync(
                new JsonObject
                {
                    ["command"] = "echo $CA_CMD_INHERITED",
                    ["env"] = new JsonObject { ["CA_CMD_INHERITED"] = "overridden" },
                },
                ctx, CancellationToken.None);

            Assert.Contains("overridden", output);
            Assert.DoesNotContain("original", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CA_CMD_INHERITED", null);
        }
    }

    [Fact]
    public async Task RunCommand_NoEnv_DoesNotFail()
    {
        // env 参数缺失时行为与原来一致
        var tool = new CommandTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["command"] = "echo ok-no-env" },
            ctx, CancellationToken.None);

        Assert.Contains("ok-no-env", output);
    }

    [Fact]
    public async Task BashTool_SharedPipeline_HonorsEnv()
    {
        // 管线统一后锁定：bash 工具同样接受 env 注入与退出码格式
        var ctx = MakeContext(_dir);
        var output = await new BashTool().ExecuteAsync(
            new JsonObject
            {
                ["command"] = "echo b-$CA_B",
                ["env"] = new JsonObject { ["CA_B"] = "x" },
            },
            ctx, CancellationToken.None);
        Assert.Contains("b-x", output);
        Assert.Contains("退出码 0", output);
    }

    [Fact]
    public async Task PowerShellTool_SharedPipeline_HonorsEnv()
    {
        var ctx = MakeContext(_dir);
        var output = await new PowerShellTool().ExecuteAsync(
            new JsonObject
            {
                ["command"] = "Write-Output \"ps-$env:CA_P\"",
                ["env"] = new JsonObject { ["CA_P"] = "y" },
            },
            ctx, CancellationToken.None);
        Assert.Contains("ps-y", output);
        Assert.Contains("退出码 0", output);
    }

    [Fact]
    public async Task CommandTool_Disabled_ReturnsNoticeWithoutExecuting()
    {
        var ctx = MakeContext(_dir, allowCommands: false);
        var output = await new CommandTool().ExecuteAsync(
            new JsonObject { ["command"] = "echo should-not-run" }, ctx, CancellationToken.None);
        Assert.Contains("被禁用", output);
        Assert.DoesNotContain("should-not-run", output);
    }
}
