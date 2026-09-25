using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class TargetFrameworkReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-tfm-" + Guid.NewGuid().ToString("N"));

    public TargetFrameworkReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Theory]
    [InlineData("net8.0", "net8.0")]
    [InlineData("net10.0", "net10.0")]
    [InlineData(".NETCoreApp,Version=v6.0", "netcoreapp6.0")]
    [InlineData(".NETStandard,Version=v2.0", "netstandard2.0")]
    [InlineData("NET10.0", "net10.0")]
    public void Normalize_UnifiesTfmForms(string tfm, string expected) =>
        Assert.Equal(expected, TargetFrameworkReportTool.Normalize(tfm));

    [Fact]
    public async Task TargetFrameworkReport_FlagsPreviewAndEol()
    {
        File.WriteAllText(Path.Combine(_dir, "A.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net6.0;net10.0-preview</TargetFrameworks></PropertyGroup></Project>""");
        var result = await new TargetFrameworkReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("目标框架报告: 1 个项目，2 种 TFM", result);
        Assert.Contains("多目标框架项目: 1", result);
        Assert.Contains("已停止支持(EOL)的目标框架", result);
        Assert.Contains("net6.0", result);
        Assert.Contains("预览版目标框架", result);
        Assert.Contains("net10.0-preview", result);
        Assert.Contains("[SDK Microsoft.NET.Sdk]", result);
    }

    [Fact]
    public async Task TargetFrameworkReport_HealthyProjectHasNoWarnings()
    {
        File.WriteAllText(Path.Combine(_dir, "A.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var result = await new TargetFrameworkReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("net10.0: 1 个项目", result);
        Assert.DoesNotContain("已停止支持", result);
        Assert.DoesNotContain("预览版", result);
    }

    [Fact]
    public async Task TargetFrameworkReport_ReportsUnparsableProject()
    {
        // 全部项目都解析失败：必须说出"解析失败"，不能谎报"未发现 csproj"
        File.WriteAllText(Path.Combine(_dir, "Bad.csproj"), "<Project>");
        var result = await new TargetFrameworkReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现可解析的 csproj（解析失败 1 个", result);
        Assert.Contains("Bad.csproj", result);
    }

    [Fact]
    public async Task TargetFrameworkReport_ReportsUnparsableAlongsideParsed()
    {
        File.WriteAllText(Path.Combine(_dir, "Good.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(_dir, "Bad.csproj"), "<Project>");
        var result = await new TargetFrameworkReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("目标框架报告: 1 个项目，1 种 TFM", result);
        Assert.Contains("解析失败: 1 个", result);
    }

    [Fact]
    public async Task TargetFrameworkReport_RejectsEmptyOutsideAndCancellation()
    {
        var empty = await new TargetFrameworkReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 csproj", empty);
        await Assert.ThrowsAsync<ToolException>(() => new TargetFrameworkReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "A.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TargetFrameworkReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
