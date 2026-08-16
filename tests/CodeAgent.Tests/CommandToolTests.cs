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

    private static AgentContext MakeContext(string dir) => new()
    {
        Config = new AgentConfig { AllowCommands = true, Shell = "bash" },
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
}
