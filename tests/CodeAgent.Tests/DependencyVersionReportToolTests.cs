using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DependencyVersionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-depver-" + Guid.NewGuid().ToString("N"));

    public DependencyVersionReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteProject(string name, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", lines));

    [Fact]
    public async Task DependencyVersionReport_FindsWildcardVersion()
    {
        WriteProject("A.csproj", "<Project>", "  <PackageReference Include=\"X\" Version=\"1.2.*\" />", "</Project>");
        var result = await new DependencyVersionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("依赖版本报告: 1 个项目文件", result);
        Assert.Contains("通配符版本: 1", result);
        Assert.Contains("每次还原可能拿到不同包", result);
    }

    [Fact]
    public async Task DependencyVersionReport_FindsFloatingVersion()
    {
        WriteProject("A.csproj", "<Project>", "  <PackageReference Include=\"X\" Version=\"1.2\" />", "</Project>");
        var result = await new DependencyVersionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("浮动版本: 1", result);
        Assert.Contains("未锁定补丁/次版本", result);
    }

    [Fact]
    public async Task DependencyVersionReport_FindsMachineSpecific()
    {
        WriteProject("A.csproj", "<Project>", "  <PackageReference Include=\"X\" Version=\"1.2.3\" MachineSpecific=\"true\" />", "</Project>");
        var result = await new DependencyVersionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("平台锁定: 1", result);
        Assert.Contains("只在本机平台可还原", result);
    }

    [Fact]
    public async Task DependencyVersionReport_FullyPinnedProjectIsClean()
    {
        WriteProject("A.csproj", "<Project>", "  <PackageReference Include=\"X\" Version=\"1.2.3\" />", "</Project>");
        var result = await new DependencyVersionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("依赖版本均已锁定", result);
    }

    [Fact]
    public async Task DependencyVersionReport_IgnoresNonProjectFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.cs"), "Version = \"9.9.*\"");
        var result = await new DependencyVersionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到项目文件", result);
    }

    [Fact]
    public async Task DependencyVersionReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new DependencyVersionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteProject("A.csproj", "<Project />");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DependencyVersionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
