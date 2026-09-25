using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class RestoreBackupToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-restore-" + Guid.NewGuid().ToString("N"));

    public RestoreBackupToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task RestoreBackup_RestoresNewFile()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt.bak"), "backup");
        var result = await new RestoreBackupTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt.bak", ["destination"] = "a.txt" }, Context(), CancellationToken.None);
        Assert.Contains("已恢复文件", result);
        Assert.Equal("backup", File.ReadAllText(Path.Combine(_dir, "a.txt")));
    }

    [Fact]
    public async Task RestoreBackup_PreservesExistingBeforeOverwrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt.bak"), "backup");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "current");
        var result = await new RestoreBackupTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt.bak", ["destination"] = "a.txt", ["overwrite"] = true },
            Context(), CancellationToken.None);
        Assert.Contains(".pre-restore", result);
        Assert.Equal("backup", File.ReadAllText(Path.Combine(_dir, "a.txt")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(_dir, "a.txt.pre-restore")));
    }

    [Fact]
    public async Task RestoreBackup_DryRunDoesNotWrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt.bak"), "backup");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "current");
        var result = await new RestoreBackupTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt.bak", ["destination"] = "a.txt", ["overwrite"] = true, ["dry_run"] = true },
            Context(), CancellationToken.None);
        Assert.Contains("[dry_run]", result);
        Assert.Equal("current", File.ReadAllText(Path.Combine(_dir, "a.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "a.txt.pre-restore")));
    }

    [Fact]
    public async Task RestoreBackup_RejectsExistingWithoutOverwrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt.bak"), "backup");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "current");
        await Assert.ThrowsAsync<ToolException>(() => new RestoreBackupTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt.bak", ["destination"] = "a.txt" }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task RestoreBackup_RejectsMissingAndOutside()
    {
        await Assert.ThrowsAsync<ToolException>(() => new RestoreBackupTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing", ["destination"] = "a.txt" }, Context(), CancellationToken.None));
        File.WriteAllText(Path.Combine(_dir, "a.txt.bak"), "backup");
        await Assert.ThrowsAsync<ToolException>(() => new RestoreBackupTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt.bak", ["destination"] = ".." }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task RestoreBackup_CanceledToken_StopsBeforeWrite()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt.bak"), "backup");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RestoreBackupTool().ExecuteAsync(
            new JsonObject { ["path"] = "a.txt.bak", ["destination"] = "a.txt" }, Context(), cts.Token));
    }
}
