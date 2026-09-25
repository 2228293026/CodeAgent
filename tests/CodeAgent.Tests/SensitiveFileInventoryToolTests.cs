using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class SensitiveFileInventoryToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-sensitivefiles-" + Guid.NewGuid().ToString("N"));

    public SensitiveFileInventoryToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task SensitiveFileInventory_FindsNamesWithoutReadingContent()
    {
        File.WriteAllText(Path.Combine(_dir, ".env"), "TOKEN=do-not-output");
        File.WriteAllText(Path.Combine(_dir, "server.key"), "private");
        File.WriteAllText(Path.Combine(_dir, "credentials.yml"), "password: hidden");
        File.WriteAllText(Path.Combine(_dir, ".npmrc"), "//registry/:_authToken=hidden");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "safe");
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", ".env"), "ignored");
        var result = await new SensitiveFileInventoryTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("敏感文件清单", result);
        Assert.Contains(".env [环境配置]", result);
        Assert.Contains("server.key [私钥/证书]", result);
        Assert.Contains("credentials.yml [凭据文件]", result);
        Assert.Contains(".npmrc [包管理器认证]", result);
        Assert.DoesNotContain("do-not-output", result);
        Assert.DoesNotContain("bin/.env", result);
    }

    [Fact]
    public async Task SensitiveFileInventory_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new SensitiveFileInventoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing" }, Context(), CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => new SensitiveFileInventoryTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task SensitiveFileInventory_CanceledToken_StopsBeforeScan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SensitiveFileInventoryTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
