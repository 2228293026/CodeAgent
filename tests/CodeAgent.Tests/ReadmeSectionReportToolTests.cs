using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ReadmeSectionReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-readme-" + Guid.NewGuid().ToString("N"));

    public ReadmeSectionReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ReadmeSectionReport_ListsHeadingsAndSkipsCodeFences()
    {
        File.WriteAllText(Path.Combine(_dir, "README.md"), string.Join("\n",
            "# 标题",
            "",
            "```bash",
            "# 这不是标题",
            "```",
            "",
            "## 安装",
            "",
            "## 使用",
            "",
            "## 配置",
            ""));
        var result = await new ReadmeSectionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("README 章节报告: 1 个文件", result);
        Assert.Contains("L1 h1 标题", result);
        Assert.Contains("L7 h2 安装", result);
        Assert.Contains("常见章节: 齐全", result);
        Assert.DoesNotContain("这不是标题", result);
    }

    [Fact]
    public async Task ReadmeSectionReport_ReportsMissingSections()
    {
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# 标题\n\n## 贡献\n");
        var result = await new ReadmeSectionReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("可能缺失:", result);
        Assert.Contains("安装/构建", result);
    }

    [Fact]
    public async Task ReadmeSectionReport_RejectsNonReadmeOutsideAndCancellation()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.md"), "# hi");
        await Assert.ThrowsAsync<ToolException>(() => new ReadmeSectionReportTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# ok");
        await Assert.ThrowsAsync<ToolException>(() => new ReadmeSectionReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReadmeSectionReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
