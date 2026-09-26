using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class HardcodedSecretReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-secret-" + Guid.NewGuid().ToString("N"));

    public HardcodedSecretReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Secret_FindsHardcodedKey()
    {
        WriteFile("A.cs", "class A", "{", "    const string ApiKey = \"sk-live-9f3a2b7c4d1e\";", "}");
        var result = await new HardcodedSecretReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("硬编码凭据报告: 1 个文件", result);
        Assert.Contains("疑似硬编码的凭据", result);
    }

    [Fact]
    public async Task Secret_NeverEchoesTheWholeKey()
    {
        // 关键回归：报告本身会进会话日志，完整回显密钥等于把密钥抄进日志。
        WriteFile("A.cs", "class A", "{", "    const string ApiKey = \"sk-live-9f3a2b7c4d1e\";", "}");
        var result = await new HardcodedSecretReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("9f3a2b7c4d1e", result);
        Assert.Contains("sk-…d1e（20 字符）", result);
    }

    [Fact]
    public async Task Secret_PlaceholdersAreNotReported()
    {
        WriteFile("A.cs",
            "class A", "{",
            "    // api_key: your-api-key-here",
            "    const string Token = \"xxxxxxxxxxxx\";",
            "    const string Secret = \"test\";",
            "}");
        var result = await new HardcodedSecretReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现硬编码凭据", result);
    }

    [Fact]
    public async Task Secret_FindsConnectionStringPassword()
    {
        WriteFile("appsettings.json", "{", "  \"conn\": \"Server=db;Password=hunter2secret\"", "}");
        var result = await new HardcodedSecretReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("连接串里带口令", result);
        Assert.DoesNotContain("hunter2secret", result);
    }

    [Fact]
    public async Task Secret_FindsKeyInThrow()
    {
        WriteFile("A.cs", "class A", "{", "    void M(string apiKey)", "    {", "        throw new Exception($\"bad key {apiKey}\");", "    }", "}");
        var result = await new HardcodedSecretReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("密钥进了日志/异常", result);
        Assert.Contains("会话日志", result);
    }

    [Fact]
    public async Task Secret_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new HardcodedSecretReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到可检查的文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new HardcodedSecretReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteFile("A.cs", "class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HardcodedSecretReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
