using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitWorktreeDetailsToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-worktree-details-" + Guid.NewGuid().ToString("N"));

    public GitWorktreeDetailsToolTests() => Directory.CreateDirectory(_dir);

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
    public async Task GitWorktreeDetails_ReportsCurrentWorktree()
    {
        if (!TryGit("init", "--quiet"))
            return;
        Assert.True(TryGit("branch", "-M", "main"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        Assert.True(TryGit("add", "a.txt"));
        Assert.True(TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial"));
        var result = await new GitWorktreeDetailsTool().ExecuteAsync(new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git worktree 详情", result);
        Assert.Contains("路径:", result);
        Assert.Contains("HEAD:", result);
        Assert.Contains("分支: refs/heads/main", result);
    }

    [Fact]
    public async Task GitWorktreeDetails_RejectsOutsideAndUnknownRepository()
    {
        await Assert.ThrowsAsync<ToolException>(() => new GitWorktreeDetailsTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        if (TryGit("init", "--quiet"))
        {
            await Assert.ThrowsAsync<ToolException>(() => new GitWorktreeDetailsTool().ExecuteAsync(
                new JsonObject { ["path"] = ".." }, Context(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task GitWorktreeDetails_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitWorktreeDetailsTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
