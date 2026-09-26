using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ApiVersionPinReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-ver-" + Guid.NewGuid().ToString("N"));

    public ApiVersionPinReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteProject(string fileName, params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, fileName), string.Join("\n", lines));

    [Fact]
    public async Task Ver_FindsCaretAndTildeRanges()
    {
        WriteProject("A.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "  <ItemGroup>",
            "    <PackageReference Include=\"Dapper\" Version=\"^2.1.24\" />",
            "    <PackageReference Include=\"Moq\" Version=\"~4.20.70\" />",
            "  </ItemGroup>", "</Project>");
        var result = await new ApiVersionPinReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("依赖版本报告: 1 个项目文件", result);
        Assert.Contains("版本未锁死", result);
        Assert.Contains("Dapper", result);
        Assert.Contains("Moq", result);
    }

    [Fact]
    public async Task Ver_ExactVersionsAreClean()
    {
        WriteProject("A.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "  <ItemGroup>",
            "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />",
            "    <PackageReference Include=\"Npgsql\" Version=\"8.0.1.1\" />",
            "  </ItemGroup>", "</Project>");
        var result = await new ApiVersionPinReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现依赖版本问题", result);
    }

    [Fact]
    public async Task Ver_FindsPrerelease()
    {
        WriteProject("A.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "  <ItemGroup>",
            "    <PackageReference Include=\"AngleSharp\" Version=\"1.0.0-beta.3\" />",
            "  </ItemGroup>", "</Project>");
        var result = await new ApiVersionPinReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("预发布版本", result);
    }

    [Fact]
    public async Task Ver_FindsSamePackageTwoVersions()
    {
        WriteProject("A.csproj", "<Project>", "  <ItemGroup>", "    <PackageReference Include=\"Serilog\" Version=\"3.1.1\" />", "  </ItemGroup>", "</Project>");
        WriteProject("B.csproj", "<Project>", "  <ItemGroup>", "    <PackageReference Include=\"Serilog\" Version=\"2.12.0\" />", "  </ItemGroup>", "</Project>");
        var result = await new ApiVersionPinReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("同一包多版本", result);
        Assert.Contains("Serilog", result);
    }

    [Fact]
    public void TheVersionShapeHelpersAreExact()
    {
        // 锁定的版本：纯数字，含四段修订号
        Assert.False(ApiVersionPinReportTool.IsRange("13.0.3"));
        Assert.False(ApiVersionPinReportTool.IsRange("8.0.1.1"));
        // 范围
        Assert.True(ApiVersionPinReportTool.IsRange("^2.1.24"));
        Assert.True(ApiVersionPinReportTool.IsRange("~4.20.70"));
        Assert.True(ApiVersionPinReportTool.IsRange("*"));
        // 预发布只有带字母后缀的才算；四段修订号里的点不算
        Assert.True(ApiVersionPinReportTool.IsPreRelease("1.0.0-beta.3"));
        Assert.True(ApiVersionPinReportTool.IsPreRelease("2.0.0-rc1"));
        Assert.False(ApiVersionPinReportTool.IsPreRelease("8.0.1.1"));
        Assert.False(ApiVersionPinReportTool.IsPreRelease("13.0.3"));
    }

    [Fact]
    public async Task Ver_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ApiVersionPinReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到项目文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ApiVersionPinReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteProject("A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ApiVersionPinReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
