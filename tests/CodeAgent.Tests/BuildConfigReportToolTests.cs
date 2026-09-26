using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class BuildConfigReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-buildcfg-" + Guid.NewGuid().ToString("N"));

    public BuildConfigReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteFile(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task BuildConfigReport_FindsVersionDrift()
    {
        WriteFile("A.csproj", "<Project>", "  <Version>1.2.0</Version>", "</Project>");
        WriteFile("B.csproj", "<Project>", "  <Version>1.3.0</Version>", "</Project>");
        var result = await new BuildConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("构建配置报告:", result);
        Assert.Contains("版本号不一致: 1", result);
        Assert.Contains("1.3.0", result);
    }

    [Fact]
    public async Task BuildConfigReport_ConsistentVersionsAreFine()
    {
        WriteFile("A.csproj", "<Project>", "  <Version>1.2.0</Version>", "</Project>");
        WriteFile("B.csproj", "<Project>", "  <Version>1.2.0</Version>", "</Project>");
        var result = await new BuildConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("版本号不一致", result);
    }

    [Fact]
    public async Task BuildConfigReport_FindsDebugInPipeline()
    {
        WriteFile("ci.yml", "steps:", "  - run: dotnet build -c Debug");
        var result = await new BuildConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("流水线用 Debug: 1", result);
        Assert.Contains("应至少跑一次 Release", result);
    }

    [Fact]
    public async Task BuildConfigReport_FindsMissingGitignore()
    {
        WriteFile("A.csproj", "<Project />");
        var result = await new BuildConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains(".gitignore 问题", result);
        Assert.Contains("缺少 .gitignore", result);
    }

    [Fact]
    public async Task BuildConfigReport_IncompleteGitignoreIsFlagged()
    {
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "bin/\n*.user\n");
        WriteFile("A.csproj", "<Project />");
        var result = await new BuildConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("缺少 obj/", result);
        Assert.DoesNotContain("缺少 bin/", result);
    }

    [Fact]
    public async Task BuildConfigReport_CompleteGitignoreIsClean()
    {
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "bin/\nobj/\n*.user\n");
        WriteFile("A.csproj", "<Project />");
        var result = await new BuildConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain(".gitignore 问题", result);
    }

    [Fact]
    public async Task BuildConfigReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new BuildConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到项目或流水线文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new BuildConfigReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteFile("A.csproj", "<Project />");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BuildConfigReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
