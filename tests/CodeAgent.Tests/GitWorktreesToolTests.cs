using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public sealed class GitWorktreesToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-gitworktrees-" + Guid.NewGuid().ToString("N"));
    private readonly string _linked = Path.Combine(Path.GetTempPath(), "codeagent-gitworktree-" + Guid.NewGuid().ToString("N"));

    public GitWorktreesToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        TryGit("worktree", "remove", "--force", _linked);
        try { Directory.Delete(_dir, true); } catch { }
        try { Directory.Delete(_linked, true); } catch { }
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

    private bool InitWithCommit()
    {
        if (!TryGit("init", "--quiet"))
            return false;
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "content");
        if (!TryGit("add", "file.txt"))
            return false;
        return TryGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "initial");
    }

    [Fact]
    public async Task GitWorktrees_ListsMainAndLinkedWorktrees()
    {
        if (!InitWithCommit())
            return;
        if (!TryGit("worktree", "add", "--quiet", "-b", "linked", _linked))
            return;

        var result = await new GitWorktreesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None);
        Assert.Contains("Git worktree", result);
        Assert.Contains("linked", result);
        Assert.Contains("<external>", result);
        Assert.DoesNotContain(_linked, result);
    }

    [Fact]
    public async Task GitWorktrees_NonRepositoryReturnsHelpfulError()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => new GitWorktreesTool().ExecuteAsync(
            new JsonObject(), Context(), CancellationToken.None));
        Assert.Contains("git worktree list 失败", ex.Message);
    }

    [Fact]
    public async Task GitWorktrees_CanceledToken_StopsBeforeProcess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitWorktreesTool().ExecuteAsync(
            new JsonObject(), Context(), cts.Token));
    }
}
