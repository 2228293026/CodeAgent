using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class EditorConfigReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-editorconfig-" + Guid.NewGuid().ToString("N"));

    public EditorConfigReportToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task EditorConfigReport_FlagsDuplicatesAndMissingRoot()
    {
        File.WriteAllText(Path.Combine(_dir, ".editorconfig"), """
            # 缺少 root = true
            [*.cs]
            indent_size = 4
            indent_size = 2
            dotnet_diagnostic.CA1000.severity = warning
            """);
        var result = await new EditorConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("EditorConfig 报告: 1 个 .editorconfig", result);
        Assert.Contains("indent_size 出现 2 次", result);
        Assert.Contains("4 → 2", result);
        Assert.Contains("只生效最后一个", result);
        Assert.Contains("缺少 root = true", result);
        Assert.Contains(".editorconfig", result);
    }

    [Fact]
    public async Task EditorConfigReport_CleanFileHasNoProblems()
    {
        File.WriteAllText(Path.Combine(_dir, ".editorconfig"), """
            root = true

            [*.cs]
            indent_size = 4
            dotnet_diagnostic.CA1000.severity = warning
            """);
        var result = await new EditorConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现配置问题", result);
        Assert.DoesNotContain("重复条目", result);
    }

    [Fact]
    public async Task EditorConfigReport_SameKeyInDifferentSectionsIsNotDuplicate()
    {
        // 键在不同节里重复是合法的（各自作用于不同 glob），不应误报
        File.WriteAllText(Path.Combine(_dir, ".editorconfig"), """
            root = true

            [*.cs]
            indent_size = 4

            [*.md]
            indent_size = 2
            """);
        var result = await new EditorConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("重复条目", result);
    }

    [Fact]
    public async Task EditorConfigReport_FindsNestedUnversionedConfig()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        File.WriteAllText(Path.Combine(_dir, ".editorconfig"), "root = true\n");
        File.WriteAllText(Path.Combine(_dir, "src", ".editorconfig"), "[*.cs]\nindent_size = 4\n");
        var result = await new EditorConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("EditorConfig 报告: 2 个 .editorconfig", result);
        Assert.Contains("未纳入版本控制的 .editorconfig", result);
        Assert.Contains("src/.editorconfig", result);
    }

    [Fact]
    public async Task EditorConfigReport_RejectsEmptyOutsideAndCancellation()
    {
        var empty = await new EditorConfigReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未找到 .editorconfig", empty);
        await Assert.ThrowsAsync<ToolException>(() => new EditorConfigReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, ".editorconfig"), "root = true\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EditorConfigReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
