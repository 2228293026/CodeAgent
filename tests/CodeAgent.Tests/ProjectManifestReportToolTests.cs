using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ProjectManifestReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-manifest-" + Guid.NewGuid().ToString("N"));

    public ProjectManifestReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ProjectManifestReport_FindsEcosystemsAndSkipsIgnored()
    {
        File.WriteAllText(Path.Combine(_dir, "app.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Example\" Version=\"1.0\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_dir, "package.json"), "{\"name\":\"demo\",\"dependencies\":{\"left-pad\":\"1.0.0\"},\"devDependencies\":{\"typescript\":\"5.0.0\"}}");
        File.WriteAllText(Path.Combine(_dir, "requirements.txt"), "# comment\nflask\nrequests\n");
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "package.json"), "{}");
        var result = await new ProjectManifestReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("项目清单报告: 3 个清单", result);
        Assert.Contains("app.csproj [.NET]", result);
        Assert.Contains("TargetFramework: net10.0", result);
        Assert.Contains("PackageReference: 1", result);
        Assert.Contains("package.json [Node.js]", result);
        Assert.Contains("dependencies: 1", result);
        Assert.Contains("requirements.txt [Python]", result);
        Assert.DoesNotContain("bin/package.json", result);
    }

    [Fact]
    public async Task ProjectManifestReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new ProjectManifestReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new ProjectManifestReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task ProjectManifestReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProjectManifestReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
