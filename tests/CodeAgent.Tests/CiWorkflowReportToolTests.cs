using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CiWorkflowReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-ciworkflow-" + Guid.NewGuid().ToString("N"));

    public CiWorkflowReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Fact]
    public async Task CiWorkflowReport_ReadsHiddenGithubWorkflows()
    {
        var workflows = Path.Combine(_dir, ".github", "workflows");
        Directory.CreateDirectory(workflows);
        File.WriteAllText(Path.Combine(workflows, "ci.yml"), "name: CI\non:\n  push:\n  pull_request:\njobs:\n  test:\n    runs-on: windows-latest\n    steps:\n      - run: dotnet format --verify-no-changes\n      - run: dotnet test -c Release\n");
        File.WriteAllText(Path.Combine(workflows, "build.yaml"), "name: Build\non:\n  workflow_dispatch:\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet build -c Release\n");
        File.WriteAllText(Path.Combine(_dir, "config.yaml"), "not: workflow\n");
        var result = await new CiWorkflowReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("CI 工作流报告: 3 个 YAML 文件", result);
        Assert.Contains(".github/workflows/ci.yml: CI", result);
        Assert.Contains("触发器: push, pull_request", result);
        Assert.Contains("Runner: windows-latest", result);
        Assert.Contains("dotnet format", result);
        Assert.Contains("dotnet test", result);
        Assert.Contains("workflow_dispatch", result);
    }

    [Fact]
    public async Task CiWorkflowReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new CiWorkflowReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new CiWorkflowReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task CiWorkflowReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CiWorkflowReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
