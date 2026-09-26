using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ObservabilityReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-observ-" + Guid.NewGuid().ToString("N"));

    public ObservabilityReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task Observability_FindsSecretInLog()
    {
        WriteSource("class A", "{", "    void M() => logger.Info($\"apiKey={apiKey}\");", "}");
        var result = await new ObservabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("可观测性报告: 1 个 .cs 文件", result);
        Assert.Contains("日志含敏感字段: 1", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task Observability_RedactedSecretIsFine()
    {
        WriteSource("class A", "{", "    void M() => logger.Info($\"apiKey={Redact(apiKey)}\");", "}");
        var result = await new ObservabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("日志含敏感字段", result);
    }

    [Fact]
    public async Task Observability_FindsSilentlySwallowedError()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        try { Risky(); }",
            "        catch (Exception) { }",
            "    }",
            "}");
        var result = await new ObservabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("错误被彻底静默", result);
        Assert.Contains("既不记日志也不上抛", result);
    }

    [Fact]
    public async Task Observability_LoggedOrRethrownCatchIsFine()
    {
        WriteSource(
            "class A",
            "{",
            "    void M()",
            "    {",
            "        try { Risky(); }",
            "        catch (Exception e) { logger.Error(e); }",
            "    }",
            "}");
        var result = await new ObservabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("错误被彻底静默", result);
    }

    [Fact]
    public async Task Observability_FindsConsoleInsteadOfLog()
    {
        WriteSource("class A", "{", "    void M(string s) => Console.WriteLine(s);", "}");
        var result = await new ObservabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Console 代替日志", result);
        Assert.Contains("分级与落盘", result);
    }

    [Fact]
    public async Task Observability_FixedConsoleBannerIsFine()
    {
        WriteSource("class A", "{", "    void M() => Console.WriteLine(\"启动完成\");", "}");
        var result = await new ObservabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("Console 代替日志", result);
    }

    [Fact]
    public async Task Observability_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    int V => 3;", "}");
        var result = await new ObservabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现可观测性问题", result);
    }

    [Fact]
    public async Task Observability_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ObservabilityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ObservabilityReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ObservabilityReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
