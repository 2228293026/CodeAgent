using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ConfigHardeningReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-configharden-" + Guid.NewGuid().ToString("N"));

    public ConfigHardeningReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ConfigHardeningReport_FindsHardcodedSecret()
    {
        WriteFile("A.cs", "class A", "{", "    const string Key = \"sk-abcdefghijklmnopqrstuvwxyz012345\";", "}");
        var result = await new ConfigHardeningReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("配置健壮性报告: 1 个文件", result);
        Assert.Contains("疑似硬编码密钥: 1", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task ConfigHardeningReport_FindsPlaintextConfigCredential()
    {
        WriteFile("codeagent.json", "{", "  \"apiKey\": \"plaintext-value\"", "}");
        var result = await new ConfigHardeningReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("配置内明文口令: 1", result);
        Assert.Contains("应改用环境变量", result);
    }

    [Fact]
    public async Task ConfigHardeningReport_FindsInsecureCertificateDefault()
    {
        WriteFile("A.cs", "class A", "{", "    void M()", "    {", "        ServerCertificateValidationCallback = (a, b, c, d) => true;", "    }", "}");
        var result = await new ConfigHardeningReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("不安全默认: 1", result);
        Assert.Contains("HTTPS 失去意义", result);
    }

    [Fact]
    public async Task ConfigHardeningReport_CommentedSecretIsNotFlagged()
    {
        WriteFile("A.cs", "class A", "{", "    // const string Key = \"sk-abcdefghijklmnopqrstuvwxyz012345\";", "}");
        var result = await new ConfigHardeningReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("疑似硬编码密钥", result);
    }

    [Fact]
    public async Task ConfigHardeningReport_CleanConfigHasNoIssues()
    {
        WriteFile("codeagent.json", "{", "  \"apiKey\": \"\",", "  \"model\": \"gpt-5\"", "}");
        var result = await new ConfigHardeningReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现配置健壮性问题", result);
    }

    [Fact]
    public async Task ConfigHardeningReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new ConfigHardeningReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteFile("codeagent.json", "{}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConfigHardeningReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
