using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class EnvVarReferenceReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-envvar-" + Guid.NewGuid().ToString("N"));

    public EnvVarReferenceReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task EnvVarReferenceReport_CountsNamesAndLocations()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), string.Join("\n",
            "var k = Environment.GetEnvironmentVariable(\"MY_KEY\");",
            "var j = Environment.GetEnvironmentVariable(\"MY_KEY\");",
            "var d = Environment.GetEnvironmentVariable( \"OTHER\" );"));
        var result = await new EnvVarReferenceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("2 个变量，3 处引用", result);
        Assert.Contains("MY_KEY: 2", result);
        Assert.Contains("OTHER: 1", result);
        Assert.Contains("MY_KEY @ a.cs:1", result);
        Assert.Contains("OTHER @ a.cs:3", result);
    }

    [Fact]
    public async Task EnvVarReferenceReport_IncludeBuiltinTogglesMissingList()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "var k = Environment.GetEnvironmentVariable(\"MY_KEY\");\n");
        var on = await new EnvVarReferenceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("常见但未在代码中引用:", on);
        var off = await new EnvVarReferenceReportTool().ExecuteAsync(
            new JsonObject { ["include_builtin"] = false }, Context(), CancellationToken.None);
        Assert.DoesNotContain("常见但未在代码中引用", off);
    }

    [Fact]
    public async Task EnvVarReferenceReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new EnvVarReferenceReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EnvVarReferenceReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }

    [Fact]
    public async Task EnvVarReferenceReport_NoReferences_ReportsZero()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "class A { }\n");
        var result = await new EnvVarReferenceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("0 个变量，0 处引用", result);
    }
}
