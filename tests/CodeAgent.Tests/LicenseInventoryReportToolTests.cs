using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class LicenseInventoryReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-license-" + Guid.NewGuid().ToString("N"));

    public LicenseInventoryReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task LicenseInventoryReport_FindsFilesManifestsAndSpdx()
    {
        File.WriteAllText(Path.Combine(_dir, "LICENSE"), "MIT License");
        File.WriteAllText(Path.Combine(_dir, "COPYING.txt"), "Apache License");
        File.WriteAllText(Path.Combine(_dir, "package.json"), "{\"name\":\"demo\",\"license\":\"MIT\"}");
        File.WriteAllText(Path.Combine(_dir, "app.csproj"), "<Project><PropertyGroup><PackageLicenseExpression>Apache-2.0</PackageLicenseExpression></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_dir, "main.c"), "// SPDX-License-Identifier: BSD-3-Clause\n");
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "LICENSE"), "ignored");
        var result = await new LicenseInventoryReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("许可证清单报告: 2 个许可证文件", result);
        Assert.Contains("LICENSE", result);
        Assert.Contains("COPYING.txt", result);
        Assert.Contains("package.json = MIT", result);
        Assert.Contains("app.csproj = Apache-2.0", result);
        Assert.Contains("SPDX: BSD-3-Clause", result);
        Assert.DoesNotContain("bin/LICENSE", result);
    }

    [Fact]
    public async Task LicenseInventoryReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new LicenseInventoryReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new LicenseInventoryReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task LicenseInventoryReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LicenseInventoryReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
