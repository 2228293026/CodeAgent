using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitignoreQualityReportToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-ign-" + Guid.NewGuid().ToString("N"));

    public GitignoreQualityReportToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private void WriteIgnore(params string[] lines) =>
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), string.Join("\n", lines));

    [Fact]
    public async Task Gitignore_ReportsWhenMissing()
    {
        var result = await new GitignoreQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("没有 .gitignore", result);
    }

    [Fact]
    public async Task Gitignore_FindsBackslash()
    {
        WriteIgnore("bin", "src\\Generated");
        var result = await new GitignoreQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("反斜杠", result);
        Assert.Contains("跨平台失效", result);
    }

    [Fact]
    public async Task Gitignore_FindsAbsolutePath()
    {
        WriteIgnore("bin/", "/build-out", "obj/");
        var result = await new GitignoreQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("绝对路径", result);
    }

    [Fact]
    public async Task Gitignore_FindsOverbroadPattern()
    {
        WriteIgnore("bin/", "obj/", "*");
        var result = await new GitignoreQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("通配符过宽", result);
        Assert.Contains("等于没有 .gitignore", result);
    }

    [Fact]
    public async Task Gitignore_FindsMissingCommonEntries()
    {
        WriteIgnore("*.log");
        var result = await new GitignoreQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("常见产物漏忽略", result);
        Assert.Contains("bin/", result);
        Assert.Contains(".vs/", result);
    }

    [Fact]
    public async Task Gitignore_CompleteFileIsClean()
    {
        WriteIgnore("bin/", "obj/", ".vs/", ".idea/", "*.user", "# 注释");
        var result = await new GitignoreQualityReportTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("未发现 .gitignore 质量问题", result);
    }

    [Fact]
    public async Task Gitignore_RejectsOutsideAndCancellation()
    {
        WriteIgnore("bin/");
        await Assert.ThrowsAsync<ToolException>(() => new GitignoreQualityReportTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitignoreQualityReportTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
