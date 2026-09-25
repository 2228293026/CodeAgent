using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class ReadmeLinkCheckToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-linkcheck-" + Guid.NewGuid().ToString("N"));

    public ReadmeLinkCheckToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task ReadmeLinkCheck_ClassifiesAndReportsBrokenRelativeLinks()
    {
        File.WriteAllText(Path.Combine(_dir, "exists.md"), "ok");
        File.WriteAllText(Path.Combine(_dir, "README.md"), string.Join("\n",
            "# T",
            "[ok](exists.md)",
            "[missing](gone.md)",
            "[site](https://example.com)",
            "[anchor](#t)",
            "[dir](src/)"));
        var result = await new ReadmeLinkCheckTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("5 条链接", result);
        Assert.Contains("外链: 1   锚点: 1", result);
        Assert.Contains("失效相对链接: 2", result); // gone.md 与不存在的 src/ 目录
        Assert.Contains("README.md:3 → gone.md", result);
    }

    [Fact]
    public async Task ReadmeLinkCheck_IncludeExternalListsExternalTargets()
    {
        File.WriteAllText(Path.Combine(_dir, "README.md"), "[site](https://example.com)\n");
        var off = await new ReadmeLinkCheckTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.DoesNotContain("明细", off);
        var on = await new ReadmeLinkCheckTool().ExecuteAsync(
            new JsonObject { ["include_external"] = true }, Context(), CancellationToken.None);
        Assert.Contains("外链 README.md:1 https://example.com", on);
    }

    [Fact]
    public async Task ReadmeLinkCheck_RejectsNonMarkdownOutsideAndCancellation()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
        await Assert.ThrowsAsync<ToolException>(() => new ReadmeLinkCheckTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "README.md"), "[a](b.md)");
        await Assert.ThrowsAsync<ToolException>(() => new ReadmeLinkCheckTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReadmeLinkCheckTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }

    [Fact]
    public async Task ReadmeLinkCheck_NoLinksReportsZero()
    {
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# 只有标题\n");
        var result = await new ReadmeLinkCheckTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("0 条链接", result);
        Assert.Contains("失效相对链接: 0", result);
    }
}
