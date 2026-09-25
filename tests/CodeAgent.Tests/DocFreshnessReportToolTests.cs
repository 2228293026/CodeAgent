using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class DocFreshnessReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-docfresh-" + Guid.NewGuid().ToString("N"));

    public DocFreshnessReportToolTests() => Directory.CreateDirectory(_dir);

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
    [InlineData("## 安装步骤", "安装步骤")]
    [InlineData("### Deep Dive ###", "Deep Dive")]
    [InlineData("# 标题", "标题")]
    [InlineData("正文", "正文")] // 无 # 前缀时原样返回（调用方已按 StartsWith('#') 过滤）
    public void HeadingText_StripsMarkers(string line, string expected) =>
        Assert.Equal(expected, DocFreshnessReportTool.HeadingText(line));

    [Theory]
    [InlineData("Getting Started", "getting-started")]
    [InlineData("API (v2) & 参考", "api-v2--参考")] // 标点先被删除再转空格为连字符，连续空格产生连续连字符
    [InlineData("a_b-c", "a_b-c")]
    public void Anchor_MatchesGitHubSlug(string heading, string expected) =>
        Assert.Equal(expected, DocFreshnessReportTool.Anchor(heading));

    [Fact]
    public async Task DocFreshnessReport_FindsBrokenAnchorAndMissingFile()
    {
        File.WriteAllText(Path.Combine(_dir, "A.md"), """
            # 标题

            [好锚点](#标题)
            [坏锚点](#不存在的章节)
            [好文件](B.md)
            [坏文件](missing.md)
            [外链](https://example.com)
            """);
        File.WriteAllText(Path.Combine(_dir, "B.md"), "# 另一个标题\n");
        var result = await new DocFreshnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("文档新鲜度报告: 2 个 Markdown 文件", result);
        Assert.Contains("失效锚点: 1", result);
        Assert.Contains("A.md → #不存在的章节", result);
        Assert.Contains("失效文件链接: 1", result);
        Assert.Contains("A.md → missing.md", result);
        Assert.DoesNotContain("example.com", result); // 外链不检查
    }

    [Fact]
    public async Task DocFreshnessReport_FindsVersionDrift()
    {
        File.WriteAllText(Path.Combine(_dir, "A.md"), "适用于 1.2.0，旧版 1.0.0 已废弃。\n");
        var result = await new DocFreshnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("版本号不一致: 1", result);
        Assert.Contains("1.0.0", result);
        Assert.Contains("1.2.0", result);
    }

    [Fact]
    public async Task DocFreshnessReport_CleanDocumentHasNoIssues()
    {
        File.WriteAllText(Path.Combine(_dir, "A.md"), "# 标题\n\n只有一个 1.2.0 版本。\n");
        var result = await new DocFreshnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现文档陈旧问题", result);
    }

    [Fact]
    public async Task DocFreshnessReport_RejectsEmptyOutsideAndCancellation()
    {
        var empty = await new DocFreshnessReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 Markdown 文件", empty);
        await Assert.ThrowsAsync<ToolException>(() => new DocFreshnessReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "A.md"), "# t\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocFreshnessReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
