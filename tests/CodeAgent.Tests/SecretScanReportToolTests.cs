using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class SecretScanReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-secrets-" + Guid.NewGuid().ToString("N"));

    public SecretScanReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task SecretScanReport_FindsAndRedactsPatterns()
    {
        File.WriteAllText(Path.Combine(_dir, "config.env"), "AWS_KEY=AKIA1234567890ABCDEF\nGITHUB_TOKEN=ghp_123456789012345678901234\npassword = supersecretvalue\n-----BEGIN PRIVATE KEY-----\n");
        File.WriteAllText(Path.Combine(_dir, "safe.txt"), "password = short\n");
        var result = await new SecretScanReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("疑似秘密扫描", result);
        Assert.Contains("AWS access key", result);
        Assert.Contains("GitHub token", result);
        Assert.Contains("Private key", result);
        Assert.Contains("Credential assignment", result);
        Assert.Contains("AK************EF", result);
        Assert.DoesNotContain("supersecretvalue", result);
    }

    [Fact]
    public async Task SecretScanReport_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new SecretScanReportTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new SecretScanReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task SecretScanReport_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SecretScanReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
