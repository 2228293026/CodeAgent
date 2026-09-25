using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class NuGetReferenceReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-nugetref-" + Guid.NewGuid().ToString("N"));

    public NuGetReferenceReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task NuGetReferenceReport_FlagsFloatingVersions()
    {
        File.WriteAllText(Path.Combine(_dir, "A.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="*" />
                <PackageReference Include="xunit" Version="[2.0.0,3.0.0)" />
                <PackageReference Include="Microsoft.Extensions.Logging" />
              </ItemGroup>
            </Project>
            """);
        var result = await new NuGetReferenceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("NuGet 引用报告: 1 个项目，3 个 PackageReference", result);
        Assert.Contains("浮动/缺失版本: 3", result);
        Assert.Contains("Serilog 使用浮动版本 *", result);
        Assert.Contains("xunit 使用浮动版本 [2.0.0,3.0.0)", result);
        Assert.Contains("Microsoft.Extensions.Logging 未指定 Version", result);
    }

    [Fact]
    public async Task NuGetReferenceReport_FlagsDuplicatesAndDeprecated()
    {
        File.WriteAllText(Path.Combine(_dir, "A.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Newtonsoft.Json" Version="12.0.3" />
              </ItemGroup>
            </Project>
            """);
        var result = await new NuGetReferenceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("同一包重复引用: 1", result);
        Assert.Contains("Newtonsoft.Json 被引用 2 次", result);
        Assert.Contains("已弃用包名: 1", result);
        Assert.Contains("System.Text.Json", result);
    }

    [Fact]
    public async Task NuGetReferenceReport_HintsMissingCentralPackageManagement()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "b"));
        File.WriteAllText(Path.Combine(_dir, "a", "A.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(Path.Combine(_dir, "b", "B.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var result = await new NuGetReferenceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("没有 Directory.Packages.props", result);
    }

    [Fact]
    public async Task NuGetReferenceReport_CleanProjectHasNoIssues()
    {
        File.WriteAllText(Path.Combine(_dir, "A.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.2" />
              </ItemGroup>
            </Project>
            """);
        var result = await new NuGetReferenceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现包引用问题", result);
    }

    [Fact]
    public async Task NuGetReferenceReport_RejectsEmptyOutsideAndCancellation()
    {
        var empty = await new NuGetReferenceReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 csproj", empty);
        await Assert.ThrowsAsync<ToolException>(() => new NuGetReferenceReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "A.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NuGetReferenceReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
