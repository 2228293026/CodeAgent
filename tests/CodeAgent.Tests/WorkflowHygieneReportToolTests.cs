using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class WorkflowHygieneReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-wfhygiene-" + Guid.NewGuid().ToString("N"));

    public WorkflowHygieneReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private string WriteWorkflow(string name, string content)
    {
        var dir = Path.Combine(_dir, ".github", "workflows");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name);
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public async Task WorkflowHygieneReport_FlagsMissingTimeout()
    {
        WriteWorkflow("ci.yml", """
            name: CI
            on:
              push:
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: dotnet build
            """);
        var result = await new WorkflowHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("工作流卫生报告: 1 个工作流", result);
        Assert.Contains("缺少超时: 1", result);
        Assert.Contains("ci.yml", result);
        Assert.Contains("没有「只靠 schedule 触发」的工作流", result);
    }

    [Fact]
    public async Task WorkflowHygieneReport_FlagsCronWithoutBlankLine()
    {
        // cron 前缺空行 → YAML 把 cron 并进上一个键，schedule 静默失效
        WriteWorkflow("nightly.yml", """
            name: Nightly
            on:
              schedule:
                - cron: '0 3 * * *'
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 10
                steps:
                  - run: echo hi
            """);
        var result = await new WorkflowHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("cron 无空行: 1", result);
        Assert.Contains("只靠 cron 触发、推送不触发的工作流", result);
        Assert.Contains("nightly.yml", result);
        Assert.DoesNotContain("缺少超时", result);
    }

    [Fact]
    public void ParseJobGraph_ReadsScalarAndListNeeds()
    {
        var graph = WorkflowHygieneReportTool.ParseJobGraph("""
            jobs:
              a:
                runs-on: ubuntu-latest
              b:
                needs: a
              c:
                needs: [a, "b"]
            """);
        Assert.Equal(3, graph.Count);
        Assert.Equal("a", graph["b"][0]);
        Assert.Equal(2, graph["c"].Count);
    }

    [Fact]
    public void FindCycles_DetectsTwoNodeCycle()
    {
        var graph = new Dictionary<string, List<string>>
        {
            ["a"] = new() { "c" },
            ["b"] = new() { "a", "c" },
            ["c"] = new() { "a", "b" },
        };
        var cycles = WorkflowHygieneReportTool.FindCycles(graph);
        Assert.NotEmpty(cycles);
        Assert.All(cycles, cycle =>
        {
            // 每个环的首尾必须是同一个 job
            var nodes = cycle.Split('→', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            Assert.Equal(nodes[0], nodes[^1]);
            Assert.Contains(nodes[0], graph.Keys);
        });
        Assert.Contains("c → b → c", cycles);
    }

    [Fact]
    public void FindCycles_AcyclicGraphHasNoCycle()
    {
        var graph = new Dictionary<string, List<string>>
        {
            ["a"] = new(),
            ["b"] = new() { "a" },
            ["c"] = new() { "a", "b" },
        };
        Assert.Empty(WorkflowHygieneReportTool.FindCycles(graph));
    }

    [Fact]
    public void FindMissingNeeds_DetectsUndefinedJob()
    {
        var graph = new Dictionary<string, List<string>>
        {
            ["a"] = new() { "typo" },
            ["b"] = new() { "a" },
        };
        var missing = WorkflowHygieneReportTool.FindMissingNeeds(graph);
        Assert.Equal(("a", "typo"), missing[0]);
    }

    [Fact]
    public async Task WorkflowHygieneReport_FlagsDependencyCycle()
    {
        WriteWorkflow("chain.yml", """
            name: Chain
            on:
              push:
            jobs:
              a:
                runs-on: ubuntu-latest
                timeout-minutes: 5
              b:
                runs-on: ubuntu-latest
                timeout-minutes: 5
                needs: a, c
              c:
                runs-on: ubuntu-latest
                timeout-minutes: 5
                needs: a, b
            """);
        var result = await new WorkflowHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("依赖环", result);
        Assert.Contains("GitHub 会拒绝运行整个工作流", result);
    }

    [Fact]
    public async Task WorkflowHygieneReport_IgnoresNonWorkflowYaml()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        File.WriteAllText(Path.Combine(_dir, "config", "app.yml"), "timeout-minutes: 1\n");
        var result = await new WorkflowHygieneReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 .github/workflows 下的工作流文件", result);
    }

    [Fact]
    public async Task WorkflowHygieneReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new WorkflowHygieneReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteWorkflow("ci.yml", "on:\n  push:\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowHygieneReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
