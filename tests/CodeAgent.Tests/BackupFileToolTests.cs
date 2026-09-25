using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class BackupFileToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-backup-" + Guid.NewGuid().ToString("N"));

    public BackupFileToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task BackupFile_CreatesDefaultBakAndPreservesContent()
    {
        var source = Path.Combine(_dir, "a.txt");
        File.WriteAllText(source, "important");
        var result = await new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt" }, Context(), CancellationToken.None);
        Assert.Contains("已创建备份", result);
        Assert.Equal("important", File.ReadAllText(Path.Combine(_dir, "a.txt.bak")));
    }

    [Fact]
    public async Task BackupFile_CustomDestinationAndOverwrite()
    {
        var source = Path.Combine(_dir, "a.txt");
        File.WriteAllText(source, "v1");
        var context = Context();
        await new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt", ["destination"] = "backups/a.save" }, context, CancellationToken.None);
        File.WriteAllText(source, "v2");
        var result = await new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt", ["destination"] = "backups/a.save", ["overwrite"] = true },
            context, CancellationToken.None);
        Assert.Contains("已创建备份", result);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(_dir, "backups", "a.save")));
    }

    [Fact]
    public async Task BackupFile_DryRunDoesNotWrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        var result = await new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt", ["dry_run"] = true }, Context(), CancellationToken.None);
        Assert.Contains("[dry_run]", result);
        Assert.False(File.Exists(Path.Combine(_dir, "a.txt.bak")));
    }

    [Fact]
    public async Task BackupFile_HandlesMissingAndRejectsDirectory()
    {
        var result = await new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing.txt", ["missing_ok"] = true }, Context(), CancellationToken.None);
        Assert.Contains("已跳过", result);
        Directory.CreateDirectory(Path.Combine(_dir, "dir"));
        await Assert.ThrowsAsync<ToolException>(() => new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "dir" }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task BackupFile_RejectsOutsideAndExistingWithoutOverwrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        await Assert.ThrowsAsync<ToolException>(() => new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        await new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt" }, Context(), CancellationToken.None);
        await Assert.ThrowsAsync<ToolException>(() => new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt" }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task BackupFile_CanceledToken_StopsBeforeCopy()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BackupFileTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt" }, Context(), cts.Token));
    }
}
