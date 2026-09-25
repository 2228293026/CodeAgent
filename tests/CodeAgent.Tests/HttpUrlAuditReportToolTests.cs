using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class HttpUrlAuditReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-urlaudit-" + Guid.NewGuid().ToString("N"));

    public HttpUrlAuditReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    [Theory]
    [InlineData("https://api.example.com/v1/chat", "api.example.com")]
    [InlineData("http://localhost:8080/health", "localhost")]
    [InlineData("https://github.com/a/b", "github.com")]
    [InlineData("https://host", "host")]
    public void HostOf_ExtractsLowercasedHost(string url, string expected) =>
        Assert.Equal(expected, HttpUrlAuditReportTool.HostOf(url));

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("EXAMPLE.ORG", true)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("github.com", false)]
    public void IsPlaceholder_RecognizesSampleHosts(string host, bool expected) =>
        Assert.Equal(expected, HttpUrlAuditReportTool.IsPlaceholder(host));

    [Fact]
    public async Task HttpUrlAuditReport_GroupsByHostAndFlagsPlaceholders()
    {
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "const U = \"https://api.example.com/v1\";\n");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "见 https://api.example.com/v1 与 https://github.com/x/y\n");
        var result = await new HttpUrlAuditReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("3 处，2 个主机", result);
        Assert.Contains("api.example.com: 2", result);
        Assert.Contains("github.com: 1", result);
        Assert.Contains("占位地址:", result);
    }

    [Fact]
    public async Task HttpUrlAuditReport_IncludeDocsToggle()
    {
        File.WriteAllText(Path.Combine(_dir, "README.md"), "https://docs.example.org/x\n");
        var on = await new HttpUrlAuditReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("docs.example.org", on);
        var off = await new HttpUrlAuditReportTool().ExecuteAsync(
            new JsonObject { ["include_docs"] = false }, Context(), CancellationToken.None);
        Assert.Contains("未发现硬编码 URL", off);
    }

    [Fact]
    public async Task HttpUrlAuditReport_RejectsOutsideAndCancellation()
    {
        await Assert.ThrowsAsync<ToolException>(() => new HttpUrlAuditReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "a.cs"), "var u = \"https://x.dev\";\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HttpUrlAuditReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
