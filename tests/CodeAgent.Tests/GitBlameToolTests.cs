using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitBlameToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitblame-" + Guid.NewGuid().ToString("N"));

    public GitBlameToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentContext Context() => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(_dir),
    };

    private bool TryGit(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = _dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [Fact]
    public async Task GitBlame_ShowsLineProvenanceAndRange()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "tracked.txt"), "first\nsecond\n");
        Assert.True(TryGit("add", "tracked.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"));

        var result = await new GitBlameTool().ExecuteAsync(
            new JsonObject { ["path"] = "tracked.txt", ["start_line"] = 2, ["end_line"] = 2 },
            Context(), CancellationToken.None);
        Assert.Contains("Git blame", result);
        Assert.Contains("second", result);
        Assert.Contains("Test", result);
    }

    [Fact]
    public async Task GitBlame_UntrackedFileReturnsHelpfulError()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "untracked.txt"), "content");
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitBlameTool().ExecuteAsync(
            new JsonObject { ["path"] = "untracked.txt" }, Context(), CancellationToken.None));
        Assert.Contains("git blame 失败", ex.Message);
    }

    [Fact]
    public async Task GitBlame_InvalidLineRangeThrows()
    {
        if (!TryGit("init", "--quiet"))
            return;
        File.WriteAllText(Path.Combine(_dir, "tracked.txt"), "content");
        Assert.True(TryGit("add", "tracked.txt"));
        await Assert.ThrowsAsync<ToolException>(() => new GitBlameTool().ExecuteAsync(
            new JsonObject { ["path"] = "tracked.txt", ["start_line"] = 3, ["end_line"] = 1 },
            Context(), CancellationToken.None));
    }

    [Fact]
    public async Task GitBlame_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitBlameTool().ExecuteAsync(
            new JsonObject { ["path"] = "missing.txt" }, Context(), cts.Token));
    }
}
