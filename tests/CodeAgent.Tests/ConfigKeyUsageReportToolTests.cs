using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ConfigKeyUsageReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-cfgkey-" + Guid.NewGuid().ToString("N"));

    public ConfigKeyUsageReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteSource(params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, "A.cs"), string.Join("\n", lines));

    [Fact]
    public async Task ConfigKey_FindsUnreadProperty()
    {
        WriteSource(
            "public class Config",
            "{",
            "    public string Theme { get; set; } = \"\";",
            "}");
        var result = await new ConfigKeyUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("配置项使用报告: 1 个 .cs 文件", result);
        Assert.Contains("声明了却从未读取", result);
        Assert.Contains("Theme", result);
    }

    [Fact]
    public async Task ConfigKey_ReadPropertyIsFine()
    {
        WriteSource(
            "public class Config",
            "{",
            "    public string Theme { get; set; } = \"\";",
            "    public string Describe() => Theme;",
            "}");
        var result = await new ConfigKeyUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("声明了却从未读取", result);
    }

    [Fact]
    public async Task ConfigKey_FindsReadWithoutDeclaration()
    {
        WriteSource(
            "public class Config",
            "{",
            "    public int Timeout { get; set; }",
            "}",
            "class User",
            "{",
            "    int M(Config config) => config.MissingKey;",
            "}");
        var result = await new ConfigKeyUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("硬编码/拼写不一致", result);
        Assert.Contains("MissingKey", result);
    }

    [Fact]
    public async Task ConfigKey_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new ConfigKeyUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现配置项使用问题", result);
    }

    [Fact]
    public async Task ConfigKey_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ConfigKeyUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ConfigKeyUsageReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConfigKeyUsageReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
