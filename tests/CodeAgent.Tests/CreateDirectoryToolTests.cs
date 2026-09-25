using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class CreateDirectoryToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-mkdir-" + Guid.NewGuid().ToString("N"));

    public CreateDirectoryToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task CreateDirectory_CreatesNestedDirectory()
    {
        var result = await new CreateDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "a/b/c" }, Context(), CancellationToken.None);
        Assert.Contains("已创建目录", result);
        Assert.True(Directory.Exists(Path.Combine(_dir, "a", "b", "c")));
    }

    [Fact]
    public async Task CreateDirectory_DryRunDoesNotCreate()
    {
        var result = await new CreateDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "preview", ["dry_run"] = true }, Context(), CancellationToken.None);
        Assert.Contains("[dry_run]", result);
        Assert.False(Directory.Exists(Path.Combine(_dir, "preview")));
    }

    [Fact]
    public async Task CreateDirectory_ExistingDirectoryCanBeAcceptedOrRejected()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "existing"));
        var context = Context();
        var ok = await new CreateDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "existing" }, context, CancellationToken.None);
        Assert.Contains("目录已存在", ok);
        await Assert.ThrowsAsync<ToolException>(() => new CreateDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "existing", ["exist_ok"] = false }, context, CancellationToken.None));
    }

    [Fact]
    public async Task CreateDirectory_RejectsFileAndOutsidePath()
    {
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "x");
        var tool = new CreateDirectoryTool();
        var context = Context();
        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = "file.txt" }, context, CancellationToken.None));
        await Assert.ThrowsAsync<ToolException>(() => tool.ExecuteAsync(
            new JsonObject { ["path"] = ".." }, context, CancellationToken.None));
    }

    [Fact]
    public async Task CreateDirectory_ParentsMustExistWhenDisabled()
    {
        await Assert.ThrowsAsync<ToolException>(() => new CreateDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing/child", ["parents"] = false }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateDirectory_CanceledToken_StopsBeforeWrite()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CreateDirectoryTool().ExecuteAsync(
            new JsonObject { ["path"] = "cancelled" }, Context(), cts.Token));
    }
}
