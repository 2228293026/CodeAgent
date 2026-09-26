using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ApiUsageReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-apiusage-" + Guid.NewGuid().ToString("N"));

    public ApiUsageReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ApiUsageReport_FindsHardcodedUrl()
    {
        WriteSource("class A", "{", "    const string Ep = \"https://api.example.com/v1/chat\";", "}");
        var result = await new ApiUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("API 使用报告: 1 个 .cs 文件", result);
        Assert.Contains("硬编码 URL: 1", result);
        Assert.Contains("A.cs:3", result);
    }

    [Fact]
    public async Task ApiUsageReport_FindsHttpClientWithoutTimeout()
    {
        WriteSource("class A", "{", "    void M() => new HttpClient();", "}");
        var result = await new ApiUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("HttpClient 无超时: 1", result);
        Assert.Contains("请求可能永久挂起", result);
    }

    [Fact]
    public async Task ApiUsageReport_HttpClientWithTimeoutIsFine()
    {
        WriteSource("class A", "{", "    void M() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) };", "}");
        var result = await new ApiUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("HttpClient 无超时", result);
    }

    [Fact]
    public async Task ApiUsageReport_FindsUnvalidatedDeserialize()
    {
        WriteSource("class A", "{", "    void M(string s) => JsonSerializer.Deserialize<Config>(s);", "}");
        var result = await new ApiUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("反序列化未校验: 1", result);
    }

    [Fact]
    public async Task ApiUsageReport_CleanCodeHasNoIssues()
    {
        WriteSource("class A", "{", "    void M(string s) => Validate(JsonSerializer.Deserialize<Config>(s));", "}");
        var result = await new ApiUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 API 使用问题", result);
    }

    [Fact]
    public async Task ApiUsageReport_RejectsEmptyOutsideAndCancellation()
    {
        var none = await new ApiUsageReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .cs 文件", none);
        await Assert.ThrowsAsync<ToolException>(() => new ApiUsageReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        WriteSource("class A { }");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ApiUsageReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
